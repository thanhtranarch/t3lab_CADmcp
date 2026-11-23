using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using SysColor = System.Drawing.Color;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Agent Cursors on the drawing: AutoCAD transient graphics (never saved in the DWG, not undoable, no layer). Per AI app one
    /// pointer with a tag ("Claude · Moving 12 entities") that glides to the work; a frame round each entity it touched (green =
    /// created, the app's colour = changed, red cross = erased), with "TYPE · layer". Frames fade after a few seconds, pointers
    /// after the app is idle. Main thread only.
    /// </summary>
    internal sealed class CursorOverlay : IDisposable
    {
        const int MaxBoxes = 12;
        const double BoxHoldS = 3.5, CursorHoldS = 12, GlideMs = 650, ReplayStepMs = 900;

        static readonly IntegerCollection AllViewports = new IntegerCollection();
        static readonly SysColor Created = SysColor.FromArgb(0x14, 0xAE, 0x5C), Erased = SysColor.FromArgb(0xF2, 0x48, 0x22);

        sealed class Pointer
        {
            public readonly List<Entity> Parts = new List<Entity>();
            public Point3d At, From, To;
            public DateTime GlideStart;
            public bool Gliding;
        }

        readonly List<Entity> _boxes = new List<Entity>();
        readonly Dictionary<string, Pointer> _pointers = new Dictionary<string, Pointer>();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 30 };
        readonly Queue<Tuple<string, SysColor, AgentStep, List<ObjectId>>> _replay = new Queue<Tuple<string, SysColor, AgentStep, List<ObjectId>>>();
        DateTime _boxesUntil, _pointersUntil, _nextReplay;
        Document _doc;

        public CursorOverlay()
        {
            _timer.Tick += (s, e) =>
            {
                try { Tick(); }
                catch (Exception ex) { Log.Error("agent cursors tick: " + ex.Message); Clear(); }
            };
        }

        static TransientManager Tm => TransientManager.CurrentTransientManager;

        static AcColor ToAcad(SysColor c) => AcColor.FromRgb(c.R, c.G, c.B);

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
            foreach (Entity e in items)
            {
                try { Tm.EraseTransient(e, AllViewports); } catch (Exception) { /* the drawing changed */ }
                e.Dispose();
            }
            items.Clear();
        }

        void ClearBoxes() => Erase(_boxes);

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
            _timer.Dispose();
        }

        // ---------------------------------------------------------------- one step

        public void Show(Document doc, string agent, SysColor color, AgentStep step, List<ObjectId> targets)
        {
            if (_doc != doc) Clear();
            _doc = doc;
            ClearBoxes();
            double u = Unit(doc);
            var boxes = new List<Tuple<Extents3d, SysColor, string>>();
            var created = new HashSet<ObjectId>(step.Created);
            SysColor tint = step.Action == ActionKind.Read ? ActivityView.ActionColor(ActionKind.Read) : color;
            var union = new Extents3d();
            bool any = false;
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in targets)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                    Extents3d? b = ent.Bounds;
                    if (!b.HasValue) continue;
                    if (any) union.AddExtents(b.Value); else union = b.Value;
                    any = true;
                    if (boxes.Count < MaxBoxes)
                        boxes.Add(Tuple.Create(b.Value, created.Contains(id) ? Created : tint, ent.GetRXClass().DxfName + " · " + ent.Layer));
                }
                tr.Commit();
            }
            foreach (var b in boxes) Frame(b.Item1, b.Item2, b.Item3, u, false);
            if (targets.Count > MaxBoxes && any) Frame(union, tint, $"×{targets.Count} · {step.Action}", u, false);
            foreach (Extents3d gone in step.ErasedBoxes.Take(MaxBoxes)) Frame(gone, Erased, "erased", u, true);

            Point3d? target = boxes.Count > 0 ? TopLeft(boxes[boxes.Count - 1].Item1, u)
                : step.ErasedBoxes.Count > 0 ? TopLeft(step.ErasedBoxes[0], u) : (Point3d?)null;
            MovePointer(agent, color, $"{agent} · {step.Caption}", target, u);
            _boxesUntil = DateTime.Now.AddSeconds(BoxHoldS);
            _pointersUntil = DateTime.Now.AddSeconds(CursorHoldS);
            _timer.Start();
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
                TextString = label,
                Height = u * 0.45,
                Position = new Point3d(min.X - pad, max.Y + pad + u * 0.2, 0),
                Color = ToAcad(color),
            };
            Add(text, _boxes);
        }

        // ---------------------------------------------------------------- pointers

        void MovePointer(string agent, SysColor color, string tag, Point3d? target, double u)
        {
            if (!_pointers.TryGetValue(agent, out Pointer p))
            {
                if (target == null) return; // nothing to point at yet
                p = new Pointer { At = target.Value };
                _pointers[agent] = p;
            }
            Erase(p.Parts);
            BuildPointer(p, color, tag, u);
            if (target.HasValue && target.Value.DistanceTo(p.At) > 1e-9)
            {
                p.From = p.At;
                p.To = target.Value;
                p.GlideStart = DateTime.Now;
                p.Gliding = true;
            }
        }

        /// <summary>A filled arrow (two triangles) with the tag beside it, built at p.At.</summary>
        void BuildPointer(Pointer p, SysColor color, string tag, double u)
        {
            Point3d o = p.At;
            AcColor ac = ToAcad(color);
            var head = new Solid(o, new Point3d(o.X, o.Y - 1.4 * u, 0), new Point3d(o.X + 1.0 * u, o.Y - 1.0 * u, 0)) { Color = ac };
            var tail = new Solid(new Point3d(o.X + 0.35 * u, o.Y - 1.0 * u, 0), new Point3d(o.X + 0.6 * u, o.Y - 1.05 * u, 0),
                new Point3d(o.X + 0.65 * u, o.Y - 1.75 * u, 0), new Point3d(o.X + 0.9 * u, o.Y - 1.65 * u, 0)) { Color = ac };
            Add(head, p.Parts);
            Add(tail, p.Parts);
            var label = new MText
            {
                Contents = tag.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}"),
                TextHeight = u * 0.5,
                Location = new Point3d(o.X + 1.25 * u, o.Y - 1.1 * u, 0),
                Attachment = AttachmentPoint.MiddleLeft,
                Color = AcColor.FromRgb(255, 255, 255),
                BackgroundFill = true,
                UseBackgroundColor = false,
                BackgroundFillColor = ac,
                BackgroundScaleFactor = 1.3,
            };
            Add(label, p.Parts);
        }

        void Tick()
        {
            DateTime now = DateTime.Now;
            foreach (Pointer p in _pointers.Values)
            {
                if (!p.Gliding) continue;
                double t = Math.Min(1.0, (now - p.GlideStart).TotalMilliseconds / GlideMs);
                double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
                // the glide bends a little sideways, like a hand on a mouse
                Vector3d chord = p.To - p.From;
                Vector3d side = chord.Length > 1e-9 ? chord.GetPerpendicularVector().GetNormal() * chord.Length * 0.12 : new Vector3d();
                Point3d next = p.From + chord * eased + side * Math.Sin(Math.PI * eased);
                Matrix3d move = Matrix3d.Displacement(next - p.At);
                foreach (Entity e in p.Parts)
                {
                    e.TransformBy(move);
                    Tm.UpdateTransient(e, AllViewports);
                }
                p.At = next;
                if (t >= 1) p.Gliding = false;
            }

            if (_replay.Count > 0 && now >= _nextReplay && _doc != null)
            {
                var next = _replay.Dequeue();
                Show(_doc, next.Item1, next.Item2, next.Item3, next.Item4);
                _nextReplay = now.AddMilliseconds(ReplayStepMs);
            }
            if (_boxes.Count > 0 && now > _boxesUntil && _replay.Count == 0) ClearBoxes();
            if (now > _pointersUntil && _replay.Count == 0)
            {
                foreach (Pointer p in _pointers.Values) Erase(p.Parts);
                _pointers.Clear();
            }
            if (_boxes.Count == 0 && _pointers.Count == 0 && _replay.Count == 0) _timer.Stop();
        }

        // ---------------------------------------------------------------- replay / zoom

        public void Replay(Document doc, List<Tuple<string, SysColor, AgentStep, List<ObjectId>>> steps)
        {
            Clear();
            _doc = doc;
            foreach (var s in steps.Skip(Math.Max(0, steps.Count - 20))) _replay.Enqueue(s);
            _nextReplay = DateTime.Now;
            _timer.Start();
        }

        public void ZoomTo(Document doc, List<ObjectId> ids, List<Extents3d> extra)
        {
            var ext = new Extents3d();
            bool any = false;
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Extents3d? b = (tr.GetObject(id, OpenMode.ForRead) as Entity)?.Bounds;
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
