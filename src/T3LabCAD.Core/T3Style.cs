using System;
using System.Globalization;

namespace T3LabCAD.Core
{
    /// <summary>
    /// How Agent Cursors looks and speaks, the same in T3Lab Revit MCP and T3Lab CAD MCP (same file in both repos, other
    /// namespace), taken from t3lab-revit-api core/agent_cursors.py: the bar's light / dark palettes (HUD_PALETTES), action
    /// colours (ACTIONS), agent colours (AGENTS, _SPARE), the cursor tag (pastel wash + dark ink) and every word on the bar.
    /// Plain ARGB numbers: each UI (WPF in Revit, WinForms and transients in AutoCAD) makes its own brushes.
    /// </summary>
    internal static class T3Style
    {
        internal sealed class Palette
        {
            public uint Bg, Card, Line, Text, Text2, Muted, Faint, Accent, Ok, Bad, Warn, Info, Track;
        }

        public static readonly Palette Light = new Palette
        {
            Bg = 0xFFFFFFFF, Card = 0xFFF3F4F6, Line = 0xFFE5E7EB, Text = 0xFF111827, Text2 = 0xFF374151, Muted = 0xFF6B7280,
            Faint = 0xFF9CA3AF, Accent = 0xFFEA680C, Ok = 0xFF16A34A, Bad = 0xFFDC2626, Warn = 0xFFB45309, Info = 0xFF2563EB,
            Track = 0xFFD1D5DB,
        };

        public static readonly Palette Dark = new Palette
        {
            Bg = 0xFF1E2430, Card = 0xFF273042, Line = 0xFF334155, Text = 0xFFF8FAFC, Text2 = 0xFFCBD5E1, Muted = 0xFF94A3B8,
            Faint = 0xFF64748B, Accent = 0xFFF27522, Ok = 0xFF4ADE80, Bad = 0xFFF87171, Warn = 0xFFFBBF24, Info = 0xFF93C5FD,
            Track = 0xFF475569,
        };

        public static Palette For(bool dark) => dark ? Dark : Light;

        /// <summary>Colour of an action, by its ActionKind name (Read, Create, Modify, Delete, View, File, Code / Command).</summary>
        public static uint Action(string kind)
        {
            switch (kind)
            {
                case "Create": return 0xFF16A34A;
                case "Modify": return 0xFF2563EB;
                case "Delete": return 0xFFDC2626;
                case "View": return 0xFF7C3AED;
                case "File": return 0xFF0891B2;
                case "Code":
                case "Command": return 0xFFD97706;
                default: return 0xFF64748B;   // Read
            }
        }

        // ---------------------------------------------------------------- agents

        public static readonly uint[] Spare = { 0xFFE11D48, 0xFF0EA5E9, 0xFF22C55E, 0xFFF59E0B, 0xFF8B5CF6, 0xFF14B8A6 };

        /// <summary>Display name and colour of an AI app from its MCP clientInfo.name.</summary>
        public static string Agent(string clientName, out uint color)
        {
            string key = string.IsNullOrWhiteSpace(clientName) ? "ai" : clientName.Trim();
            string lower = key.ToLowerInvariant();
            if (lower.Contains("claude-code") || lower == "claude code") { color = 0xFFB4532A; return "Claude Code"; }
            if (lower.Contains("claude")) { color = 0xFFD97757; return "Claude"; }
            if (lower.Contains("cursor")) { color = 0xFF7C3AED; return "Cursor"; }
            if (lower.Contains("codex") || lower.Contains("openai") || lower.Contains("chatgpt")) { color = 0xFF10A37F; return "ChatGPT"; }
            if (lower.Contains("gemini") || lower.Contains("antigravity")) { color = 0xFF4285F4; return "Gemini"; }
            if (lower.Contains("copilot") || lower.Contains("vscode") || lower.Contains("visual studio")) { color = 0xFF0078D4; return "Copilot"; }
            if (lower.Contains("windsurf") || lower.Contains("cascade")) { color = 0xFF14B8A6; return "Windsurf"; }
            if (key == "ai") { color = 0xFF64748B; return "AI"; }
            color = Spare[StableHash(key) % Spare.Length];
            return key.Length > 16 ? key.Substring(0, 16) : key;
        }

        /// <summary>Colour of the n-th extra chat of one app ("Claude Code 2").</summary>
        public static uint Extra(string name, int n) => Spare[(StableHash(name) + n) % Spare.Length];

        /// <summary>Same number in every run (string.GetHashCode changes per process on .NET 8+).</summary>
        static int StableHash(string s)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in s) h = h * 31 + c;
                return h & 0x7FFFFFFF;
            }
        }

        // ---------------------------------------------------------------- the cursor tag

        /// <summary>Tag ink on the pastel wash: 10:1 or better on every colour here (white on the solid colour was ~3:1).</summary>
        public const uint TagInk = 0xFF1F2937;

        /// <summary>The colour mixed into white: amount 0 = white, 1 = the colour.</summary>
        public static uint Wash(uint color, double amount = 0.2)
        {
            uint Mix(int shift) => (uint)Math.Round(255 + (((color >> shift) & 0xFF) - 255.0) * amount);
            return 0xFF000000 | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
        }

        // ---------------------------------------------------------------- states and words

        /// <summary>Mark of a finished step, by its StepState name (Ok, Refused, Busy, Failed, Running).</summary>
        public static string Mark(string state) =>
            state == "Ok" ? "✓" : state == "Refused" ? "⏸" : state == "Busy" ? "–" : state == "Failed" ? "✕" : "…";

        public static uint MarkColor(string state, Palette p) =>
            state == "Ok" ? p.Ok : state == "Refused" || state == "Busy" ? p.Warn : state == "Failed" ? p.Bad : p.Muted;

        /// <summary>A plan task: ✓ done, ▸ now, ○ to do.</summary>
        public static string TaskMark(bool done, bool now) => done ? "✓" : now ? "▸" : "○";

        public const string Title = "T3Lab Agent Cursors", Recent = "RECENT", Empty = "Waiting for an AI app", Thinking = "Waiting for the agent";
        public const string Pause = "Pause", Resume = "Resume", Select = "Select changes", Replay = "Replay", Clear = "Clear", Follow = "Follow";
        public const string PausedNote = "Paused: AI changes are refused until you press Resume. Read-only tools still run.";
        public const string PauseTip = "Pause AI changes: tools that change the model or write files are refused until you press Resume";
        public const string ReplayTip = "Show the AI's last changes again, one by one, with its cursor";
        public const string ClearTip = "Forget the list, the goals and the changed items";
        public const string CloseTip = "Hide the bar (Agent Cursors stays on; the ribbon button shows it again)";
        public const int FeedRows = 6, AgentRows = 3;

        public static string EmptyHint(string host) =>
            $"When Claude, ChatGPT, Cursor or another AI app works in {host} through T3Lab MCP, each step shows here.";

        /// <summary>"Show on model" (Revit) / "Show on drawing" (AutoCAD).</summary>
        public static string ShowOn(string place) => "Show on " + place;

        public static string ShowOnTip(string place) => $"Move a cursor per AI app over the {place} and frame what it creates, changes and deletes";
        public static string FollowTip(string host) => $"Zoom {host} to what the AI changes";
        public static string SelectTip(string items) => $"Select every {items} the AI created or changed here, and zoom to them";
        public static string SelectLabel(int touched) => touched > 0 ? $"{Select} ({touched})" : Select;

        public static string Counts(int calls, int changes) => $"{calls} calls · {changes} changes";
        public static string StepOf(int step, int tasks, bool done) => done ? "Done" : $"Step {Math.Max(1, step)}/{tasks}";

        /// <summary>The second line of a feed row: "3 changed · 1.2 s", or the error.</summary>
        public static string Detail(string state, int changed, double seconds, string error) =>
            state == "Ok" ? (changed > 0 ? $"{changed} changed · " : "") + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" : error ?? "";

        /// <summary>"now", "12 s ago", "3 min ago", "14:05".</summary>
        public static string Ago(DateTime when)
        {
            double s = (DateTime.Now - when).TotalSeconds;
            if (s < 5) return "now";
            if (s < 60) return (int)s + " s ago";
            if (s < 3600) return (int)(s / 60) + " min ago";
            return when.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>Three pulsing dots for "thinking" (one step every half second).</summary>
        public static string Dots() => new string('·', 1 + (int)(DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond / 500 % 3));
    }
}
