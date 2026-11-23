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
            _set = new PaletteSet("T3Lab Agent Cursors", PaletteId)
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
            if (!_disposed && e.NewState == StateEventIndex.Show) _view.Invalidate(true);
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

    /// <summary>The content of the bar: drawn by hand (one owner-drawn canvas) plus a row of buttons.</summary>
    internal sealed class ActivityView : UserControl
    {
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
        readonly Button _pause, _select, _zoom, _replay, _clear;
        readonly CheckBox _onDrawing, _zoomToWork;
        readonly Timer _tick = new Timer { Interval = 1000 };
        AgentCursors.View _model = new AgentCursors.View();
        DateTime _modelAt = DateTime.Now;
        bool _updating;

        Color _back, _fore, _muted, _line, _panel;
        readonly Font _font = new Font("Segoe UI", 9f), _bold = new Font("Segoe UI", 9f, FontStyle.Bold), _small = new Font("Segoe UI", 8f);
        // GDI+ objects are cached per colour (a handful) and disposed with the control: no allocation per repaint
        readonly Dictionary<int, SolidBrush> _brushes = new Dictionary<int, SolidBrush>();
        readonly Dictionary<int, Pen> _pens = new Dictionary<int, Pen>();
        readonly StringFormat _oneLine = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        SolidBrush B(Color c)
        {
            if (!_brushes.TryGetValue(c.ToArgb(), out SolidBrush b)) _brushes[c.ToArgb()] = b = new SolidBrush(c);
            return b;
        }

        Pen P(Color c)
        {
            if (!_pens.TryGetValue(c.ToArgb(), out Pen p)) _pens[c.ToArgb()] = p = new Pen(c, 1f);
            return p;
        }

        public static Color ActionColor(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Create: return Color.FromArgb(0x14, 0xAE, 0x5C);
                case ActionKind.Modify: return Color.FromArgb(0xFF, 0x8A, 0x00);
                case ActionKind.Delete: return Color.FromArgb(0xF2, 0x48, 0x22);
                case ActionKind.File: return Color.FromArgb(0xA2, 0x59, 0xFF);
                case ActionKind.View: return Color.FromArgb(0x8C, 0x8C, 0x8C);
                case ActionKind.Command: return Color.FromArgb(0xE0, 0xA8, 0x00);
                default: return Color.FromArgb(0x0D, 0x99, 0xFF);
            }
        }

        public ActivityView()
        {
            Font = _font;
            _pause = MakeButton("⏸ Pause", (s, e) => AgentCursors.SetPaused(!_model.Paused));
            _select = MakeButton("Select touched", (s, e) => AgentCursors.SelectTouched());
            _zoom = MakeButton("Zoom", (s, e) => AgentCursors.ZoomTouched());
            _replay = MakeButton("Replay", (s, e) => AgentCursors.Replay());
            _clear = MakeButton("Clear", (s, e) => AgentCursors.ClearFeed());
            _onDrawing = MakeCheck("Show on the drawing", "show_on_drawing");
            _zoomToWork = MakeCheck("Zoom to the work", "zoom_to_work");
            Controls.Add(_canvas);
            Controls.Add(_buttons);
            _canvas.Paint += PaintCanvas;
            _tick.Tick += (s, e) => { if (_model.Agents.Any(a => a.Working) || _model.Feed.Count > 0) _canvas.Invalidate(); };
            _tick.Start();
            ApplyTheme();
        }

        Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(2), Padding = new Padding(4, 1, 4, 1) };
            b.Click += (s, e) =>
            {
                try { click(s, e); }
                catch (Exception ex) { Log.Error("agent cursors button: " + ex.Message); }
            };
            _buttons.Controls.Add(b);
            return b;
        }

        CheckBox MakeCheck(string text, string option)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(4, 4, 4, 2) };
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
            bool dark = Dark();
            _back = dark ? Color.FromArgb(0x2B, 0x30, 0x38) : Color.FromArgb(0xF7, 0xF7, 0xF7);
            _panel = dark ? Color.FromArgb(0x34, 0x3A, 0x44) : Color.White;
            _fore = dark ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Color.FromArgb(0x1A, 0x1A, 0x1A);
            _muted = dark ? Color.FromArgb(0xA0, 0xA6, 0xB0) : Color.FromArgb(0x6A, 0x6A, 0x6A);
            _line = dark ? Color.FromArgb(0x45, 0x4C, 0x58) : Color.FromArgb(0xDD, 0xDD, 0xDD);
            BackColor = _canvas.BackColor = _buttons.BackColor = _back;
            foreach (Control c in _buttons.Controls)
            {
                c.ForeColor = _fore;
                c.BackColor = c is Button ? _panel : _back;
                if (c is Button b) b.FlatAppearance.BorderColor = _line;
            }
        }

        public void SetView(AgentCursors.View view)
        {
            _model = view;
            _modelAt = DateTime.Now;
            _updating = true;
            try
            {
                _pause.Text = view.Paused ? "▶ Resume" : "⏸ Pause";
                _pause.ForeColor = view.Paused ? Color.FromArgb(0xE0, 0x8E, 0x00) : _fore;
                _onDrawing.Checked = view.ShowOnDrawing;
                _zoomToWork.Checked = view.ZoomToWork;
                _zoomToWork.Enabled = view.ShowOnDrawing;
                _select.Text = view.Touched > 0 ? $"Select touched ({view.Touched})" : "Select touched";
            }
            finally { _updating = false; }
            _canvas.Invalidate();
        }

        public void Stop()
        {
            _tick.Stop();
            _tick.Dispose();
        }

        static string Ago(DateTime when)
        {
            double s = (DateTime.Now - when).TotalSeconds;
            if (s < 5) return "now";
            if (s < 60) return $"{(int)s} s ago";
            if (s < 3600) return $"{(int)(s / 60)} min ago";
            return when.ToString("HH:mm");
        }

        // ---------------------------------------------------------------- drawing

        void PaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = _canvas.ClientSize.Width, x = 10, y = 8, inner = Math.Max(60, w - 20);
            double since = (DateTime.Now - _modelAt).TotalSeconds;

            if (_model.Agents.Count == 0)
            {
                Line(g, "Waiting for an AI app…", _bold, _fore, x, ref y, inner);
                Line(g, "When Claude, ChatGPT, Cursor or another AI app works in AutoCAD through T3Lab MCP, each step shows here.", _font, _muted, x, ref y, inner);
            }

            foreach (AgentCursors.AgentView agent in _model.Agents.Take(3))
            {
                // name row: pointer in the app's colour, name, status
                Pointer(g, agent.Color, x, y + 1);
                g.DrawString(agent.Name, _bold, B(_fore), x + 16, y);
                string status = agent.Status == "Working" ? $"Working {(int)(agent.NowSeconds + since)} s" : agent.Status;
                Color statusColor = agent.Status == "Paused" ? Color.FromArgb(0xE0, 0x8E, 0x00) : agent.Working ? ActivityView.ActionColor(agent.NowAction) : _muted;
                SizeF ss = g.MeasureString(status, _small);
                float sx = x + inner - ss.Width;
                if (agent.Working)
                {
                    int pulse = 3 + (int)((DateTime.Now.Millisecond / 250) % 2);
                    g.FillEllipse(B(statusColor), sx - 10, y + 5, pulse * 2, pulse * 2);
                }
                g.DrawString(status, _small, B(statusColor), sx, y + 1);
                y += 20;

                if (!string.IsNullOrEmpty(agent.Goal)) Line(g, agent.Goal, _font, _fore, x, ref y, inner);
                if (agent.Tasks.Count > 0)
                {
                    int done = agent.PlanDone ? agent.Tasks.Count : Math.Max(0, agent.Step - 1);
                    string label = agent.PlanDone ? "Done" : $"Step {Math.Max(1, agent.Step)}/{agent.Tasks.Count}";
                    g.DrawString(label, _small, B(_muted), x, y);
                    int barX = x + 70, barW = Math.Max(20, inner - 70);
                    g.FillRectangle(B(_line), barX, y + 5, barW, 5);
                    g.FillRectangle(B(agent.Color), barX, y + 5, (int)(barW * (double)done / agent.Tasks.Count), 5);
                    y += 18;
                    for (int i = 0; i < agent.Tasks.Count; i++)
                    {
                        bool isDone = agent.PlanDone || i < agent.Step - 1, now = !agent.PlanDone && i == agent.Step - 1;
                        string mark = isDone ? "✓" : now ? "▶" : "○";
                        Color c = isDone ? _muted : now ? _fore : _muted;
                        g.DrawString(mark, _small, B(isDone ? Color.FromArgb(0x14, 0xAE, 0x5C) : now ? agent.Color : _muted), x + 2, y);
                        int ty = y;
                        Line(g, $"{i + 1}  {agent.Tasks[i]}", now ? _bold : _small, c, x + 16, ref ty, inner - 16);
                        y = ty;
                    }
                }
                if (agent.Working && agent.Now != null)
                {
                    // what runs now: a block in the action's colour
                    Color ac = ActivityView.ActionColor(agent.NowAction);
                    var block = new Rectangle(x, y + 2, inner, 34);
                    g.FillRectangle(B(Color.FromArgb(40, ac)), block);
                    g.FillRectangle(B(ac), x, y + 2, 4, 34);
                    g.DrawString(agent.NowAction.ToString().ToUpperInvariant(), _small, B(ac), x + 10, y + 4);
                    g.DrawString(agent.Now, _bold, B(_fore), new RectangleF(x + 10, y + 17, inner - 14, 16));
                    y += 42;
                }
                y += 6;
                g.DrawLine(P(_line), x, y, x + inner, y);
                y += 8;
            }

            if (_model.Paused)
            {
                Line(g, "Paused: changes from AI apps are held (read-only tools still run). Resume to let them work.", _small,
                    Color.FromArgb(0xE0, 0x8E, 0x00), x, ref y, inner);
                y += 4;
            }

            if (_model.Feed.Count > 0)
            {
                g.DrawString("LAST STEPS", _small, B(_muted), x, y);
                y += 16;
                foreach (AgentCursors.StepView step in _model.Feed.Take(8))
                {
                    if (y > _canvas.ClientSize.Height - 18) break;
                    Color mc = step.State == StepState.Ok ? Color.FromArgb(0x14, 0xAE, 0x5C)
                        : step.State == StepState.Refused ? Color.FromArgb(0xE0, 0x8E, 0x00) : Color.FromArgb(0xF2, 0x48, 0x22);
                    string mark = step.State == StepState.Ok ? "✓" : step.State == StepState.Refused ? "⏸" : "✕";
                    g.FillRectangle(B(ActivityView.ActionColor(step.Action)), x, y + 2, 3, 13);
                    g.DrawString(mark, _small, B(mc), x + 6, y);
                    string right = $"{step.Agent} · {Ago(step.When)}";
                    SizeF rs = g.MeasureString(right, _small);
                    g.DrawString(right, _small, B(_muted), x + inner - rs.Width, y);
                    g.DrawString(step.Caption, _font, B(_fore), new RectangleF(x + 20, y - 1, Math.Max(10, inner - rs.Width - 26), 16),
                        _oneLine);
                    y += 17;
                    string detail = step.State == StepState.Ok
                        ? (step.Changed > 0 ? $"{step.Changed} changed · {step.Seconds:0.0} s" : $"{step.Seconds:0.0} s")
                        : step.Error ?? "";
                    if (detail.Length > 0)
                    {
                        g.DrawString(detail, _small, B(step.State == StepState.Ok ? _muted : mc),
                            new RectangleF(x + 20, y - 2, inner - 20, 14), _oneLine);
                        y += 14;
                    }
                }
            }
        }

        void Line(Graphics g, string text, Font font, Color color, int x, ref int y, int width)
        {
            var area = new RectangleF(x, y, width, 400);
            SizeF size = g.MeasureString(text, font, width);
            g.DrawString(text, font, B(color), area);
            y += (int)Math.Ceiling(size.Height) + 2;
        }

        /// <summary>A small mouse pointer, the avatar of an AI app.</summary>
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
                _oneLine.Dispose();
                foreach (SolidBrush b in _brushes.Values) b.Dispose();
                foreach (Pen p in _pens.Values) p.Dispose();
                _brushes.Clear();
                _pens.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
