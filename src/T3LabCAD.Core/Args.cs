using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.Geometry;

namespace T3LabCAD.Core
{
    /// <summary>An error the model should see and can fix: returned as a tool result with isError=true.</summary>
    internal sealed class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }

    /// <summary>Typed, case-insensitive access to a tool's JSON arguments.</summary>
    internal sealed class Args
    {
        readonly Dictionary<string, object> _d;

        public Args(object value)
        {
            _d = value is Dictionary<string, object> d
                ? new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<string> Keys => _d.Keys;

        public bool Has(string key) => _d.TryGetValue(key, out var v) && v != null;

        public object Raw(string key) => _d.TryGetValue(key, out var v) ? v : null;

        public string Str(string key, string def = null)
        {
            var v = Raw(key);
            return v == null ? def : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public string ReqStr(string key)
        {
            var s = Str(key);
            if (string.IsNullOrWhiteSpace(s)) throw Missing(key);
            return s;
        }

        public double Num(string key, double def) => Has(key) ? ToDouble(Raw(key), key) : def;

        public double ReqNum(string key)
        {
            if (!Has(key)) throw Missing(key);
            return ToDouble(Raw(key), key);
        }

        public int Int(string key, int def) => (int)Math.Round(Num(key, def));

        public bool Bool(string key, bool def)
        {
            var v = Raw(key);
            if (v == null) return def;
            if (v is bool b) return b;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "0" || s == "no") return false;
            throw new ToolError($"Argument '{key}' must be true or false.");
        }

        public Point3d Pt(string key, Point3d def) => Has(key) ? ToPoint(Raw(key), key) : def;

        public Point3d ReqPt(string key)
        {
            if (!Has(key)) throw Missing(key);
            return ToPoint(Raw(key), key);
        }

        public IList List(string key)
        {
            var v = Raw(key);
            if (v == null) return null;
            if (v is IList list && !(v is string)) return list;
            return new[] { v };
        }

        public List<string> Strs(string key)
        {
            var result = new List<string>();
            var list = List(key);
            if (list == null) return result;
            foreach (var o in list)
                if (o != null) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            return result;
        }

        public List<Point3d> Pts(string key)
        {
            var list = List(key) ?? throw Missing(key);
            var result = new List<Point3d>();
            for (int i = 0; i < list.Count; i++) result.Add(ToPoint(list[i], $"{key}[{i}]"));
            return result;
        }

        public Args Obj(string key) => new Args(Raw(key));

        static ToolError Missing(string key) => new ToolError($"Missing required argument '{key}'.");

        public static double ToDouble(object v, string key)
        {
            try
            {
                if (v is string s) return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new ToolError($"Argument '{key}' must be a number.");
            }
        }

        public static Point3d ToPoint(object v, string key)
        {
            if (v is string s) v = s.Split(',');
            if (v is IList l && l.Count >= 2 && l.Count <= 3)
                return new Point3d(ToDouble(l[0], key), ToDouble(l[1], key), l.Count == 3 ? ToDouble(l[2], key) : 0);
            if (v is Dictionary<string, object> d)
            {
                var a = new Args(d);
                return new Point3d(a.ReqNum("x"), a.ReqNum("y"), a.Num("z", 0));
            }
            throw new ToolError($"Argument '{key}' must be a point [x, y] or [x, y, z].");
        }
    }
}
