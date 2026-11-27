using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace T3LabCAD.Core
{
    /// <summary>The Agent Cursors bar: a dockable AutoCAD palette with each AI app, its goal and tasks, what runs now and the last steps.</summary>
    internal sealed class ActivityPalette : IDisposable
    {
        static readonly Guid PaletteId = new Guid("8C2E6B71-4D3A-4F0E-9B5A-1E7D2C9F3A60");
        readonly PaletteSet _set;
        readonly ActivityView _view;
        bool _disposed;

        public bool Alive => !_disposed;

        public bool Visible
        {
            get
            {
                try { return !_disposed && _set.Visible; }
                catch (Exception) { return false; }
            }
        }

        public ActivityPalette()
        {
            _view = new ActivityView();
            _set = new PaletteSet(T3Style.Title, PaletteId)
            {
                Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowPropertiesMenu,
                MinimumSize = new Size(260, 280),
                DockEnabled = DockSides.Left | DockSides.Right,
            };
            _set.Add("Activity", _view);
            _set.StateChanged += OnStateChanged;
            _set.Visible = true;
        }

        void OnStateChanged(object sender, PaletteSetStateEventArgs e)
        {
            // Hide comes from the X, but also from HIDEPALETTES, clean screen, a workspace switch or no drawing open:
            // the bar just stays closed (T3CURSORS opens it again, or turns Agent Cursors off when it is open).
            if (!_disposed && e.NewState == StateEventIndex.Show) _view.Invalidate(true);   // also after a COLORTHEME switch
        }

        /// <summary>New content; reveal = open the bar if it is closed (refreshes never reopen a bar the user closed).</summary>
        public void Show(AgentCursors.View view, bool reveal)
        {
            if (_disposed) return;
            _view.SetView(view);
            if (reveal && !_set.Visible) _set.Visible = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _set.StateChanged -= OnStateChanged;
                _view.Stop();
                _set.Visible = false;
                _set.Dispose();
            }
            catch (Exception) { /* AutoCAD is closing */ }
        }
    }

    /// <summary>
    /// The content of the bar, laid out like T3Lab Revit MCP's bar (same order, words and T3Style colours; COLORTHEME picks
    /// light or dark): per agent its pointer, name, status, counts, goal, plan with progress and the band of what runs now;
    /// RECENT; then Show on drawing / Follow and Pause / Select changes / Replay / Clear. One owner-drawn canvas + controls.
    /// </summary>
    internal sealed class ActivityView : UserControl
    {
        const string Host = "AutoCAD", Place = "drawing";

        sealed class Canvas : Panel
        {
            public Canvas()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }

        readonly Canvas _canvas = new Canvas { Dock = DockStyle.Fill };
        readonly FlowLayoutPanel _buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6, 4, 6, 6), WrapContents = true };
        readonly Button _pause, _select, _replay, _clear;
        readonly CheckBox _onDrawing, _follow;
        readonly ToolTip _tips = new ToolTip();
        readonly Timer _tick = new Timer { Interval = 500 };
        AgentCursors.View _model = new AgentCursors.View();
        DateTime _modelAt = DateTime.Now;
        bool _updating;
        T3Style.Palette _p = T3Style.Dark;

        readonly Font _font = new Font("Segoe UI", 9f), _bold = new Font("Segoe UI", 9f, FontStyle.Bold), _small = new Font("Segoe UI", 8f),
            _italic = new Font("Segoe UI", 8f, FontStyle.Italic);
        // GDI+ objects are cached per colour (a handful) and disposed with the control: no allocation per repaint
        readonly Dictionary<int, SolidBrush> _brushes = new Dictionary<int, SolidBrush>();
        readonly Dictionary<int, Pen> _pens = new Dictionary<int, Pen>();
        readonly StringFormat _oneLine = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        static Color C(uint argb) => AgentCursors.C(argb);

        SolidBrush B(Color c)
        {
            if (!_brushes.TryGetValue(c.ToArgb(), out SolidBrush b)) _brushes[c.ToArgb()] = b = new SolidBrush(c);
            return b;
        }

        SolidBrush B(uint argb) => B(C(argb));

        Pen P(Color c)
        {
            if (!_pens.TryGetValue(c.ToArgb(), out Pen p)) _pens[c.ToArgb()] = p = new Pen(c, 1f);
            return p;
        }

        /// <summary>The action's colour (T3Style, the same in T3Lab Revit MCP).</summary>
        public static Color ActionColor(ActionKind kind) => C(T3Style.Action(kind.ToString()));

        public ActivityView()
        {
            Font = _font;
            _onDrawing = MakeCheck(T3Style.ShowOn(Place), "show_on_drawing", T3Style.ShowOnTip(Place));
            _follow = MakeCheck(T3Style.Follow, "zoom_to_work", T3Style.FollowTip(Host));
            _buttons.SetFlowBreak(_follow, true);
            _pause = MakeButton(T3Style.Pause, T3Style.PauseTip, (s, e) => AgentCursors.SetPaused(!_model.Paused));
            _select = MakeButton(T3Style.Select, T3Style.SelectTip("entity"), (s, e) => AgentCursors.SelectTouched());
            _replay = MakeButton(T3Style.Replay, T3Style.ReplayTip, (s, e) => AgentCursors.Replay());
            _clear = MakeButton(T3Style.Clear, T3Style.ClearTip, (s, e) => AgentCursors.ClearFeed());
            Controls.Add(_canvas);
            Controls.Add(_buttons);
            _canvas.Paint += PaintCanvas;
            _tick.Tick += (s, e) =>
            {
                try
                {
                    // the seconds count, "in line" -> "waiting for AutoCAD", thinking -> idle: a fresh snapshot (main thread)
                    if (_model.Agents.Any(a => a.Working || a.State == AgentCursors.AgentState.Thinking)) SetView(AgentCursors.Snapshot());
                    else if (_model.Feed.Count > 0 && DateTime.Now.Second % 5 == 0) _canvas.Invalidate();   // "3 min ago"
                }
                catch (Exception ex) { Log.Error("agent cursors bar tick: " + ex.Message); }
            };
            _tick.Start();
            ApplyTheme();
        }

        Button MakeButton(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(2), Padding = new Padding(4, 1, 4, 1) };
            _tips.SetToolTip(b, tip);
            b.Click += (s, e) =>
            {
                try { click(s, e); }
                catch (Exception ex) { Log.Error("agent cursors button: " + ex.Message); }
            };
            _buttons.Controls.Add(b);
            return b;
        }

        CheckBox MakeCheck(string text, string option, string tip)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(4, 4, 8, 2) };
            _tips.SetToolTip(c, tip);
            c.CheckedChanged += (s, e) => { if (!_updating) AgentCursors.SetOption(option, c.Checked); };
            _buttons.Controls.Add(c);
            return c;
        }

        static bool Dark()
        {
            try { return Convert.ToInt32(AcApp.GetSystemVariable("COLORTHEME")) == 0; }
            catch (Exception) { return true; }
        }

        void ApplyTheme()
        {
            _p = T3Style.For(Dark());
            BackColor = _canvas.BackColor = _buttons.BackColor = C(_p.Bg);
            foreach (Control c in _buttons.Controls)
            {
                c.ForeColor = C(_p.Text);
                c.BackColor = c is Button ? C(_p.Card) : C(_p.Bg);
                if (c is Button b) b.FlatAppearance.BorderColor = C(_p.Line);
            }
            if (_model.Paused)
            {
                _pause.BackColor = Color.FromArgb(0x33, C(_p.Warn));
                _pause.FlatAppearance.BorderColor = C(_p.Warn);
            }
        }

        public void SetView(AgentCursors.View view)
        {
            _model = view;
            _modelAt = DateTime.Now;
            _updating = true;
            try
            {
                _pause.Text = view.Paused ? T3Style.Resume : T3Style.Pause;
                _onDrawing.Checked = view.ShowOnDrawing;
                _follow.Checked = view.ZoomToWork;
                _follow.Enabled = view.ShowOnDrawing;
                _select.Text = T3Style.SelectLabel(view.Touched);
                _select.Enabled = view.Touched > 0;
                ApplyTheme();
            }
            finally { _updating = false; }
            _canvas.Invalidate();
        }

        public void Stop()
        {
            _tick.Stop();
            _tick.Dispose();
        }

        // ---------------------------------------------------------------- drawing

        void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = _canvas.ClientSize.Width, x = 12, y = 10, inner = Math.Max(60, w - 24);
            double since = (DateTime.Now - _modelAt).TotalSeconds;

            if (_model.Agents.Count == 0)
            {
                Line(g, T3Style.Empty, _bold, C(_p.Text), x, ref y, inner);
                Line(g, T3Style.EmptyHint(Host), _font, C(_p.Muted), x, ref y, inner);
                y += 6;
            }

            foreach (AgentCursors.AgentView agent in _model.Agents.Take(T3Style.AgentRows))
            {
                // name row: the agent's pointer, name, status
                Pointer(g, agent.Color, x, y + 1);
                g.DrawString(agent.Name, _bold, B(_p.Text), x + 16, y);
                int secs = (int)(agent.NowSeconds + since);
                string status;
                uint statusColor;
                switch (agent.State)
                {
                    case AgentCursors.AgentState.Working: status = $"Working {secs} s"; statusColor = T3Style.Action(agent.NowAction.ToString()); break;
                    case AgentCursors.AgentState.Blocked: status = $"Waiting for {Host} {secs} s"; statusColor = _p.Warn; break;
                    case AgentCursors.AgentState.Queued:
                    case AgentCursors.AgentState.Paused: status = agent.Status; statusColor = _p.Warn; break;
                    case AgentCursors.AgentState.Thinking: status = "Thinking" + T3Style.Dots(); statusColor = _p.Info; break;
                    default: status = agent.LastEnd.HasValue ? "Idle · last step " + T3Style.Ago(agent.LastEnd.Value) : "Idle"; statusColor = _p.Muted; break;
                }
                SizeF ss = g.MeasureString(status, _small);
                g.DrawString(status, _small, B(statusColor), x + inner - ss.Width, y + 1);
                y += 18;
                g.DrawString(T3Style.Counts(agent.Calls, agent.Changes), _small, B(_p.Muted), x + 16, y);
                y += 16;

                if (!string.IsNullOrEmpty(agent.Goal)) Line(g, agent.Goal, _font, C(_p.Text), x + 16, ref y, inner - 16);
                if (agent.Tasks.Count > 0)
                {
                    int done = agent.PlanDone ? agent.Tasks.Count : Math.Max(0, agent.Step - 1);
                    g.DrawString(T3Style.StepOf(agent.Step, agent.Tasks.Count, agent.PlanDone), _small, B(_p.Muted), x + 16, y);
                    int barX = x + 80, barW = Math.Max(20, inner - 80);
                    g.FillRectangle(B(_p.Track), barX, y + 5, barW, 5);
                    g.FillRectangle(B(agent.Color), barX, y + 5, (int)(barW * (double)done / agent.Tasks.Count), 5);
                    y += 18;
                    for (int i = 0; i < agent.Tasks.Count; i++)
                    {
                        bool isDone = agent.PlanDone || i < agent.Step - 1, now = !agent.PlanDone && i == agent.Step - 1;
                        g.DrawString(T3Style.TaskMark(isDone, now), _small, isDone ? B(_p.Ok) : now ? B(agent.Color) : B(_p.Muted), x + 16, y);
                        int ty = y;
                        Line(g, $"{i + 1}  {agent.Tasks[i]}", now ? _bold : _small, C(now ? _p.Text : _p.Muted), x + 30, ref ty, inner - 30);
                        y = ty;
                    }
                }
                if (agent.Working && agent.Now != null)
                {
                    // the band: the action's colour while it runs; amber while it waits its turn or for AutoCAD
                    bool waits = agent.State == AgentCursors.AgentState.Queued || agent.State == AgentCursors.AgentState.Blocked;
                    Color ac = waits ? C(_p.Warn) : ActionColor(agent.NowAction);
                    string head = agent.State == AgentCursors.AgentState.Queued
                        ? "IN LINE · " + (agent.Behind != null ? "after " + agent.Behind : "waiting its turn") + (agent.Ahead > 0 ? $", {agent.Ahead} more ahead" : "")
                        : agent.State == AgentCursors.AgentState.Blocked ? $"WAITING · {Host} is busy (a dialog or a long operation)"
                        : agent.NowAction.ToString().ToUpperInvariant();
                    y += 4;
                    g.FillRectangle(B(Color.FromArgb(40, ac)), x, y, inner, 34);
                    g.FillRectangle(B(ac), x, y, 4, 34);
                    g.DrawString(head, _small, B(ac), new RectangleF(x + 10, y + 2, inner - 14, 14), _oneLine);
                    g.DrawString(agent.Now, _bold, B(_p.Text), new RectangleF(x + 10, y + 16, inner - 14, 16), _oneLine);
                    y += 38;
                }
                else if (agent.State == AgentCursors.AgentState.Thinking)
                {
                    y += 2;
                    Line(g, T3Style.Thinking + " " + T3Style.Dots(), _italic, C(_p.Muted), x + 16, ref y, inner - 16);
                }
                y += 6;
                g.DrawLine(P(C(_p.Line)), x, y, x + inner, y);
                y += 8;
            }

            if (_model.Paused)
            {
                Line(g, T3Style.PausedNote, _small, C(_p.Warn), x, ref y, inner);
                y += 4;
            }

            g.DrawString(T3Style.Recent, _small, B(_p.Muted), x, y);
            y += 16;
            if (_model.Feed.Count == 0) Line(g, "Nothing yet.", _font, C(_p.Muted), x, ref y, inner);
            foreach (AgentCursors.StepView step in _model.Feed.Take(T3Style.FeedRows))
            {
                if (y > _canvas.ClientSize.Height - 18) break;
                string state = step.State.ToString();
                g.FillRectangle(B(ActionColor(step.Action)), x, y + 2, 3, 13);
                g.DrawString(T3Style.Mark(state), _small, B(T3Style.MarkColor(state, _p)), x + 6, y);
                string right = step.Agent + " · " + T3Style.Ago(step.When);
                SizeF rs = g.MeasureString(right, _small);
                g.DrawString(right, _small, B(_p.Muted), x + inner - rs.Width, y);
                g.DrawString(step.Caption, _font, B(_p.Text), new RectangleF(x + 20, y - 1, Math.Max(10, inner - rs.Width - 26), 16), _oneLine);
                y += 17;
                string detail = T3Style.Detail(state, step.Changed, step.Seconds, step.Error);
                if (detail.Length > 0)
                {
                    g.DrawString(detail, _small, B(step.State == StepState.Ok ? _p.Muted : T3Style.MarkColor(state, _p)),
                        new RectangleF(x + 20, y - 2, inner - 20, 14), _oneLine);
                    y += 14;
                }
                y += 2;
            }
        }

        void Line(Graphics g, string text, Font font, Color color, int x, ref int y, int width)
        {
            var area = new RectangleF(x, y, width, 400);
            SizeF size = g.MeasureString(text, font, width);
            g.DrawString(text, font, B(color), area);
            y += (int)Math.Ceiling(size.Height) + 2;
        }

        /// <summary>The agent's avatar on the bar: the pointer it moves over the drawing.</summary>
        void Pointer(Graphics g, Color color, int x, int y)
        {
            var pts = new[] { new PointF(x, y), new PointF(x, y + 13), new PointF(x + 3.6f, y + 9.6f), new PointF(x + 6.2f, y + 14.5f),
                              new PointF(x + 8.2f, y + 13.6f), new PointF(x + 5.8f, y + 8.8f), new PointF(x + 10.5f, y + 8.8f) };
            g.FillPolygon(B(color), pts);
            g.DrawPolygon(P(Color.White), pts);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _font.Dispose();
                _bold.Dispose();
                _small.Dispose();
                _italic.Dispose();
                _oneLine.Dispose();
                _tips.Dispose();
                foreach (SolidBrush b in _brushes.Values) b.Dispose();
                foreach (Pen p in _pens.Values) p.Dispose();
                _brushes.Clear();
                _pens.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
