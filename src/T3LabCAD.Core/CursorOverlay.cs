using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using SysColor = System.Drawing.Color;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Agent Cursors on the drawing: AutoCAD transient graphics (never saved in the DWG, not undoable, no layer). Per AI app one
    /// pointer with a pastel tag ("Claude · Line 3450") that glides to the work, and draws along the lines, polylines and arcs a
    /// call created; a frame round each entity it touched (green = created, the app's colour = changed, red cross = erased).
    /// Between calls the tag says "thinking ···"; an idle pointer goes after IdleHideS. Main thread only.
    /// TransientManager.CurrentTransientManager follows the active drawing, so everything is erased before that drawing stops
    /// being active (DocumentToBeDeactivated / ToBeDestroyed). A transient that could not be erased is never disposed (the
    /// drawing may still draw it): it is kept alive instead.
    /// </summary>
    internal sealed class CursorOverlay : IDisposable
    {
        /// <summary>What a pointer's tag says between frames: the running call, the result (then thinking), done.</summary>
        internal enum Mode { Working, Done, Finished }

        const int MaxBoxes = 12, MaxPath = 8, MaxPathPoints = 40;
        const double BoxHoldS = 3.5, GlideMs = 650, HopMs = 380, ReplayStepMs = 900, ThinkAfterS = 2.5;
        const double IdleHideS = AgentCursors.ThinkS + 10;

        static readonly IntegerCollection AllViewports = new IntegerCollection();
        static readonly SysColor Created = SysColor.FromArgb(0x14, 0xAE, 0x5C), Erased = SysColor.FromArgb(0xF2, 0x48, 0x22);
        static readonly AcColor Ink = AcColor.FromRgb(0x1F, 0x29, 0x37);
        static readonly List<Entity> Orphans = new List<Entity>();

        sealed class Leg
        {
            public Point3d From, To;
            public double Ms;
            public bool Draw;        // straight, with a line growing behind the tip
        }

        sealed class Pointer
        {
            public readonly List<Entity> Parts = new List<Entity>();
            public readonly Queue<Leg> Legs = new Queue<Leg>();
            public MText Label;
            public Leg Leg;
            public Line Trail;
            public Point3d At;
            public DateTime LegStart, Seen, ModeSince;
            public int Slot;
            public double U;          // the view unit it was built at
            public SysColor Color;
            public string Agent, Text = "", Shown;
            public Mode Mode = Mode.Done;
        }

        readonly List<Entity> _boxes = new List<Entity>();
        readonly Dictionary<string, Pointer> _pointers = new Dictionary<string, Pointer>();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 30 };
        readonly Queue<Tuple<string, SysColor, AgentStep, List<ObjectId>>> _replay = new Queue<Tuple<string, SysColor, AgentStep, List<ObjectId>>>();
        DateTime _boxesUntil, _nextReplay;
        Document _doc;
        ObjectId _textStyle; // text in transients needs a style of the drawing
        double _u = 1;

        public CursorOverlay()
        {
            _timer.Tick += (s, e) =>
            {
                try { Tick(); }
                catch (Exception ex) { Log.Error("agent cursors tick: " + ex.Message); Clear(); }
            };
            AcApp.DocumentManager.DocumentToBeDeactivated += OnLeaving;
            AcApp.DocumentManager.DocumentToBeDestroyed += OnLeaving;
        }

        /// <summary>Still the active drawing: its transient manager is the current one.</summary>
        void OnLeaving(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                if (_doc != null && e.Document == _doc) Clear();
            }
            catch (Exception ex) { Log.Error("agent cursors leave: " + ex.Message); }
        }

        bool OnActiveDocument() => _doc != null && AcApp.DocumentManager.MdiActiveDocument == _doc;

        static TransientManager Tm => TransientManager.CurrentTransientManager;

        static AcColor ToAcad(SysColor c) => AcColor.FromRgb(c.R, c.G, c.B);

        /// <summary>The colour mixed into white (amount 0 = white): the tag's wash, read with dark ink (10:1 or better).</summary>
        static AcColor Pastel(SysColor c, double amount = 0.22) => AcColor.FromRgb(
            (byte)Math.Round(255 + (c.R - 255) * amount), (byte)Math.Round(255 + (c.G - 255) * amount), (byte)Math.Round(255 + (c.B - 255) * amount));

        /// <summary>A size that looks the same on screen at any zoom: 1/45 of the view height.</summary>
        static double Unit(Document doc)
        {
            try
            {
                using (ViewTableRecord view = doc.Editor.GetCurrentView()) return Math.Max(view.Height, 1e-6) / 45.0;
            }
            catch (Exception) { return 1.0; }
        }

        void Add(Entity e, List<Entity> into)
        {
            Tm.AddTransient(e, TransientDrawingMode.DirectTopmost, 128, AllViewports);
            into.Add(e);
        }

        void Erase(List<Entity> items)
        {
            bool current = OnActiveDocument();
            foreach (Entity e in items)
            {
                bool erased = false;
                if (current)
                {
                    try { erased = Tm.EraseTransient(e, AllViewports); }
                    catch (Exception) { /* not shown any more */ }
                }
                if (erased) e.Dispose();
                else Orphans.Add(e); // maybe still drawn by another drawing: never free it
            }
            items.Clear();
        }

        void ClearBoxes()
        {
            foreach (Pointer p in _pointers.Values) p.Trail = null;   // trails live in _boxes
            Erase(_boxes);
        }

        public void Clear()
        {
            _replay.Clear();
            ClearBoxes();
            foreach (Pointer p in _pointers.Values) Erase(p.Parts);
            _pointers.Clear();
            _timer.Stop();
        }

        public void Dispose()
        {
            Clear();
            _doc = null;
            try
            {
                AcApp.DocumentManager.DocumentToBeDeactivated -= OnLeaving;
                AcApp.DocumentManager.DocumentToBeDestroyed -= OnLeaving;
            }
            catch (Exception) { /* AutoCAD is closing */ }
            _timer.Dispose();
        }

        // ---------------------------------------------------------------- one step

        /// <summary>One step: frames round its targets (frames=false: only the pointer moves, for reads and view changes).</summary>
        public void Show(Document doc, string agent, SysColor color, AgentStep step, List<ObjectId> targets, bool frames)
        {
            if (_doc != doc) Clear();
            _doc = doc;
            if (!OnActiveDocument()) return;
            ClearBoxes();
            double u = _u = Unit(doc);
            var boxes = new List<Tuple<Extents3d, SysColor, string>>();
            var created = new HashSet<string>(step.Created, StringComparer.OrdinalIgnoreCase); // main thread, like every writer
            SysColor tint = step.Action == ActionKind.Read ? ActivityView.ActionColor(ActionKind.Read) : color;
            var union = new Extents3d();
            bool any = false;
            var subjects = new List<string>();
            var kinds = new HashSet<string>();
            var path = new List<List<Point3d>>();
            _textStyle = doc.Database.Textstyle;
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in targets)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                    Extents3d? b;
                    try { b = ent.Bounds; } catch (Exception) { continue; }
                    if (!b.HasValue) continue;
                    if (any) union.AddExtents(b.Value); else union = b.Value;
                    any = true;
                    kinds.Add(Kind(ent));
                    bool isNew = created.Contains(id.Handle.ToString());
                    if (subjects.Count < 2) subjects.Add(Subject(ent, tr));
                    if (boxes.Count < MaxBoxes)
                        boxes.Add(Tuple.Create(b.Value, isNew ? Created : tint, Subject(ent, tr) + " · " + ent.Layer));
                    if (frames && isNew && step.Action == ActionKind.Create && path.Count < MaxPath)
                    {
                        List<Point3d> pts = PathOf(ent);
                        if (pts != null) path.Add(pts);
                    }
                }
                tr.Commit();
            }
            double hold = BoxHoldS;
            if (frames)
            {
                foreach (var b in boxes) Frame(b.Item1, b.Item2, b.Item3, u, false);
                if (targets.Count > MaxBoxes && any) Frame(union, tint, $"×{targets.Count} · {step.Action}", u, false);
                foreach (Extents3d gone in step.ErasedBoxes.Take(MaxBoxes)) Frame(gone, Erased, "erased", u, true);
            }

            // the tag says what was made when it is one thing or one kind: "Line 3450", "Circle ×4"
            string subject = !frames || subjects.Count == 0 ? null : subjects.Count == 1 ? subjects[0] : kinds.Count == 1 ? kinds.First() + " ×" + targets.Count : null;
            Pointer p = PointerFor(agent, color, boxes.Count > 0 || step.ErasedBoxes.Count > 0, boxes.Count > 0 ? TopLeft(boxes[0].Item1, u) : (Point3d?)null);
            if (p != null)
            {
                if (Math.Abs(u - p.U) > p.U * 0.25) Build(p);   // the user zoomed: same size on screen again
                SetText(p, Mode.Done, subject ?? step.Caption);
                if (path.Count > 0) hold += DrawAlong(p, path, u) / 1000.0;
                else
                {
                    Point3d? target = boxes.Count > 0 ? TopLeft(boxes[boxes.Count - 1].Item1, u)
                        : frames && step.ErasedBoxes.Count > 0 ? TopLeft(step.ErasedBoxes[0], u) : (Point3d?)null;
                    // two agents on one entity sit side by side
                    if (target.HasValue) GlideTo(p, target.Value + new Vector3d(1, -1, 0) * (p.Slot % 4) * 0.7 * u, GlideMs);
                }
            }
            _boxesUntil = DateTime.Now.AddSeconds(hold);
            Run();
        }

        static Point3d TopLeft(Extents3d b, double u) => new Point3d(b.MinPoint.X - u * 0.4, b.MaxPoint.Y + u * 0.4, 0);

        void Frame(Extents3d b, SysColor color, string label, double u, bool cross)
        {
            double pad = u * 0.4;
            Point3d min = b.MinPoint, max = b.MaxPoint;
            var rect = new Polyline();
            rect.AddVertexAt(0, new Point2d(min.X - pad, min.Y - pad), 0, 0, 0);
            rect.AddVertexAt(1, new Point2d(max.X + pad, min.Y - pad), 0, 0, 0);
            rect.AddVertexAt(2, new Point2d(max.X + pad, max.Y + pad), 0, 0, 0);
            rect.AddVertexAt(3, new Point2d(min.X - pad, max.Y + pad), 0, 0, 0);
            rect.Closed = true;
            rect.ConstantWidth = u * 0.08;
            rect.Color = ToAcad(color);
            Add(rect, _boxes);
            if (cross)
            {
                Add(new Line(new Point3d(min.X, min.Y, 0), new Point3d(max.X, max.Y, 0)) { Color = ToAcad(color) }, _boxes);
                Add(new Line(new Point3d(min.X, max.Y, 0), new Point3d(max.X, min.Y, 0)) { Color = ToAcad(color) }, _boxes);
            }
            var text = new DBText
            {
                TextStyleId = _textStyle,
                TextString = label,
                Height = u * 0.45,
                Position = new Point3d(min.X - pad, max.Y + pad + u * 0.2, 0),
                Color = ToAcad(color),
            };
            Add(text, _boxes);
        }

        // ---------------------------------------------------------------- what an entity is

        static string Kind(Entity e)
        {
            switch (e)
            {
                case Line _: return "Line";
                case Polyline _: return "Polyline";
                case Circle _: return "Circle";
                case Arc _: return "Arc";
                case BlockReference _: return "Block";
                case DBText _:
                case MText _: return "Text";
                case Dimension _: return "Dimension";
                case Hatch _: return "Hatch";
                default:
                    string dxf = e.GetRXClass().DxfName ?? e.GetType().Name;
                    return dxf.Length == 0 ? e.GetType().Name : char.ToUpperInvariant(dxf[0]) + dxf.Substring(1).ToLowerInvariant();
            }
        }

        /// <summary>"Line 3450", "Circle Ø1000", "Block Door-900", "Text "LEVEL 2"", "Dimension 320" (drawing units).</summary>
        static string Subject(Entity e, Transaction tr)
        {
            try
            {
                switch (e)
                {
                    case Line l: return "Line " + Num(l.Length);
                    case Polyline pl: return "Polyline " + Num(pl.Length);
                    case Circle c: return "Circle Ø" + Num(c.Radius * 2);
                    case Arc a: return "Arc R" + Num(a.Radius);
                    case BlockReference br:
                        ObjectId def = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                        return "Block " + ((tr.GetObject(def, OpenMode.ForRead) as BlockTableRecord)?.Name ?? br.Name);
                    case DBText t: return "Text \"" + Clip(t.TextString) + "\"";
                    case MText m: return "Text \"" + Clip(m.Text) + "\"";
                    case Dimension d:
                        if (!string.IsNullOrEmpty(d.DimensionText) && d.DimensionText != "<>") return "Dimension " + Clip(d.DimensionText);
                        return d is LineAngularDimension2 || d is Point3AngularDimension
                            ? "Dimension " + Num(d.Measurement * 180 / Math.PI) + "°" : "Dimension " + Num(d.Measurement);
                    case Hatch h: return "Hatch " + h.PatternName;
                }
            }
            catch (Exception) { /* fall back to the kind */ }
            return Kind(e);
        }

        static string Clip(string s)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= 24 ? s : s.Substring(0, 23) + "…";
        }

        static string Num(double v)
        {
            double a = Math.Abs(v);
            return v.ToString(a >= 100 ? "0" : a >= 1 ? "0.##" : "0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>The points the pointer draws along for a new curve (start to end); null for anything else.</summary>
        static List<Point3d> PathOf(Entity e)
        {
            try
            {
                switch (e)
                {
                    case Line l: return new List<Point3d> { l.StartPoint, l.EndPoint };
                    case Polyline pl:
                        var pts = Enumerable.Range(0, Math.Min(pl.NumberOfVertices, 16)).Select(i => pl.GetPoint3dAt(i)).ToList();
                        if (pl.Closed && pts.Count > 2) pts.Add(pts[0]);
                        return pts.Count >= 2 ? pts : null;
                    case Arc _:
                    case Circle _:
                        var curve = (Curve)e;
                        double s = curve.StartParam, t = curve.EndParam;
                        return Enumerable.Range(0, 9).Select(i => curve.GetPointAtParameter(s + (t - s) * i / 8)).ToList();
                }
            }
            catch (Exception) { /* not drawable */ }
            return null;
        }

        // ---------------------------------------------------------------- pointers

        /// <summary>The agent's pointer; created only where it has something to point at.</summary>
        Pointer PointerFor(string agent, SysColor color, bool create, Point3d? at)
        {
            if (_pointers.TryGetValue(agent, out Pointer p)) return p;
            if (!create || !at.HasValue) return null;
            p = new Pointer { Agent = agent, Color = color, At = at.Value, Slot = _pointers.Count, Seen = DateTime.Now };
            _pointers[agent] = p;
            Build(p);
            return p;
        }

        /// <summary>Retag the pointer of an agent that is already on the drawing.</summary>
        public void SetState(string agent, Mode mode, string text)
        {
            if (agent == null || !OnActiveDocument() || !_pointers.TryGetValue(agent, out Pointer p)) return;
            SetText(p, mode, text);
            Run();
        }

        void SetText(Pointer p, Mode mode, string text)
        {
            p.Mode = mode;
            p.Text = text ?? "";
            p.ModeSince = p.Seen = DateTime.Now;
            Retag(p, p.Text);
        }

        void Retag(Pointer p, string text)
        {
            string tag = string.IsNullOrEmpty(text) ? p.Agent : p.Agent + " · " + text;
            if (tag == p.Shown || p.Label == null) return;
            p.Shown = tag;
            p.Label.Contents = tag.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
            try { Tm.UpdateTransient(p.Label, AllViewports); }
            catch (Exception) { /* drawn again at the next build */ }
        }

        /// <summary>A filled arrow (two triangles) with a pastel tag beside it (dark ink), built at p.At.</summary>
        void Build(Pointer p)
        {
            Erase(p.Parts);
            double u = p.U = _u;
            Point3d o = p.At;
            AcColor ac = ToAcad(p.Color);
            var head = new Solid(o, new Point3d(o.X, o.Y - 1.4 * u, 0), new Point3d(o.X + 1.0 * u, o.Y - 1.0 * u, 0)) { Color = ac };
            var tail = new Solid(new Point3d(o.X + 0.35 * u, o.Y - 1.0 * u, 0), new Point3d(o.X + 0.6 * u, o.Y - 1.05 * u, 0),
                new Point3d(o.X + 0.65 * u, o.Y - 1.75 * u, 0), new Point3d(o.X + 0.9 * u, o.Y - 1.65 * u, 0)) { Color = ac };
            Add(head, p.Parts);
            Add(tail, p.Parts);
            p.Shown = null;
            p.Label = new MText
            {
                TextStyleId = _textStyle,
                TextHeight = u * 0.5,
                Location = new Point3d(o.X + 1.25 * u, o.Y - 1.1 * u, 0),
                Attachment = AttachmentPoint.MiddleLeft,
                Color = Ink,
                BackgroundFill = true,
                UseBackgroundColor = false,
                BackgroundFillColor = Pastel(p.Color),
                BackgroundScaleFactor = 1.3,
            };
            string tag = string.IsNullOrEmpty(p.Text) ? p.Agent : p.Agent + " · " + p.Text;
            p.Label.Contents = tag.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
            p.Shown = tag;
            Add(p.Label, p.Parts);
        }

        void GlideTo(Pointer p, Point3d to, double ms)
        {
            p.Legs.Clear();
            p.Leg = null;
            if (to.DistanceTo(p.At) > 1e-9) p.Legs.Enqueue(new Leg { From = p.At, To = to, Ms = ms });
        }

        /// <summary>Hop to each new curve's start, then draw along it. Returns the duration (ms).</summary>
        double DrawAlong(Pointer p, List<List<Point3d>> path, double u)
        {
            p.Legs.Clear();
            p.Leg = null;
            Point3d at = p.At;
            double total = 0;
            int points = 0;
            bool first = true;
            foreach (List<Point3d> pts in path)
            {
                if (points >= MaxPathPoints) break;
                double hop = first ? GlideMs : HopMs;
                first = false;
                p.Legs.Enqueue(new Leg { From = at, To = pts[0], Ms = hop });
                total += hop;
                at = pts[0];
                foreach (Point3d q in pts.Skip(1))
                {
                    if (++points > MaxPathPoints) break;
                    // about 22 view units a second, so a long wall takes longer than a short one, within 60-900 ms
                    double ms = Math.Max(60, Math.Min(900, at.DistanceTo(q) / Math.Max(u, 1e-9) * 45));
                    p.Legs.Enqueue(new Leg { From = at, To = q, Ms = ms, Draw = true });
                    total += ms;
                    at = q;
                }
            }
            return total;
        }

        /// <summary>Keep the timer going: fast while something moves, slow while pointers only wait (thinking dots).</summary>
        void Run()
        {
            bool moving = _pointers.Values.Any(p => p.Leg != null || p.Legs.Count > 0) || _replay.Count > 0;
            _timer.Interval = moving ? 30 : 250;
            _timer.Start();
        }

        void Tick()
        {
            DateTime now = DateTime.Now;
            if (!OnActiveDocument())
            {
                Clear(); // its drawing is not active: nothing of it can be updated
                return;
            }
            foreach (Pointer p in _pointers.Values) Step(p, now);

            if (_replay.Count > 0 && now >= _nextReplay && _doc != null)
            {
                var next = _replay.Dequeue();
                Show(_doc, next.Item1, next.Item2, next.Item3, next.Item4, true);
                _nextReplay = now.AddMilliseconds(ReplayStepMs);
            }
            if (_boxes.Count > 0 && now > _boxesUntil && _replay.Count == 0) ClearBoxes();
            foreach (Pointer p in _pointers.Values.ToList())
            {
                if (p.Leg != null || p.Legs.Count > 0) continue;
                double since = (now - p.ModeSince).TotalSeconds;
                // between calls the tag says the agent is thinking (waiting for its model), like the bar
                if (p.Mode == Mode.Done && since > ThinkAfterS)
                    Retag(p, since < AgentCursors.ThinkS ? "thinking " + new string('·', 1 + (int)(since * 2) % 3) : "");
                if (_replay.Count == 0 && p.Mode != Mode.Working && (now - p.Seen).TotalSeconds > IdleHideS)
                {
                    Erase(p.Parts);
                    _pointers.Remove(p.Agent);
                }
            }
            if (_boxes.Count == 0 && _pointers.Count == 0 && _replay.Count == 0) _timer.Stop();
            else Run();
        }

        /// <summary>Move a pointer along its legs: eased hops that bend a little like a hand on a mouse, straight drawing legs.</summary>
        void Step(Pointer p, DateTime now)
        {
            if (p.Leg == null)
            {
                if (p.Legs.Count == 0) return;
                p.Leg = p.Legs.Dequeue();
                p.LegStart = now;
                p.Trail = null;
                if (p.Leg.Draw)
                {
                    p.Trail = new Line(p.Leg.From, p.Leg.From) { Color = ToAcad(p.Color) };
                    Add(p.Trail, _boxes);
                }
            }
            Leg leg = p.Leg;
            double t = Math.Min(1.0, (now - p.LegStart).TotalMilliseconds / Math.Max(1, leg.Ms));
            Vector3d chord = leg.To - leg.From;
            Point3d next;
            if (leg.Draw) next = leg.From + chord * t;
            else
            {
                double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
                Vector3d side = chord.Length > 1e-9 ? chord.GetPerpendicularVector().GetNormal() * chord.Length * 0.12 : new Vector3d();
                next = leg.From + chord * eased + side * Math.Sin(Math.PI * eased);
            }
            Matrix3d move = Matrix3d.Displacement(next - p.At);
            foreach (Entity e in p.Parts)
            {
                e.TransformBy(move);
                Tm.UpdateTransient(e, AllViewports);
            }
            p.At = next;
            if (p.Trail != null)
            {
                p.Trail.EndPoint = next;
                try { Tm.UpdateTransient(p.Trail, AllViewports); }
                catch (Exception) { /* erased with the boxes */ }
            }
            if (t >= 1)
            {
                p.Leg = null;
                p.Trail = null;
            }
        }

        // ---------------------------------------------------------------- replay / zoom

        public void Replay(Document doc, List<Tuple<string, SysColor, AgentStep, List<ObjectId>>> steps)
        {
            Clear();
            _doc = doc;
            foreach (var s in steps.Skip(Math.Max(0, steps.Count - 20))) _replay.Enqueue(s);
            _nextReplay = DateTime.Now;
            Run();
        }

        public void ZoomTo(Document doc, List<ObjectId> ids, List<Extents3d> extra)
        {
            var ext = new Extents3d();
            bool any = false;
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Extents3d? b;
                    try { b = (tr.GetObject(id, OpenMode.ForRead) as Entity)?.Bounds; }
                    catch (Exception) { continue; }
                    if (!b.HasValue) continue;
                    if (any) ext.AddExtents(b.Value); else ext = b.Value;
                    any = true;
                }
                tr.Commit();
            }
            foreach (Extents3d b in extra)
            {
                if (any) ext.AddExtents(b); else ext = b;
                any = true;
            }
            if (!any) return;
            using (doc.LockDocument()) ViewTools.ZoomTo(doc.Editor, ext.MinPoint, ext.MaxPoint, 1.25);
        }
    }
}
