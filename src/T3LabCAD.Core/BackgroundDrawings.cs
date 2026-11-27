using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Drawings opened in the background (open_document background=true): a database read into memory, no window, nothing
    /// for the user to see. Tools pinned with autocad_document work in them like in an open drawing; save_document /
    /// close_document (document=...) write them back. The file is read whole and released (CloseInput), so saving goes to
    /// the same path. Main thread only (every tool runs there).
    /// </summary>
    internal static class BackgroundDrawings
    {
        internal sealed class Drawing
        {
            public string Path;
            public Database Db;
            public bool ReadOnly, Modified;
            public DateTime Opened = DateTime.Now;
        }

        static readonly Dictionary<string, Drawing> Open = new Dictionary<string, Drawing>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<Drawing> All => Open.Values.ToList();

        /// <summary>By full path, file name, or file name without extension; null when none matches.</summary>
        public static Drawing Find(string nameOrPath)
        {
            string want = (nameOrPath ?? "").Trim().Trim('"');
            if (want.Length == 0) return null;
            if (Open.TryGetValue(want, out Drawing exact)) return exact;
            return Open.Values.FirstOrDefault(d =>
                string.Equals(System.IO.Path.GetFileName(d.Path), want, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(System.IO.Path.GetFileNameWithoutExtension(d.Path), want, StringComparison.OrdinalIgnoreCase));
        }

        public static Drawing Load(string path, bool readOnly)
        {
            if (Open.TryGetValue(path, out Drawing known)) return known;
            if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) throw new ToolError("Only a .dwg opens in the background.");
            var db = new Database(false, true);
            try
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);   // all in memory, the file is free again: saving writes to the same path
            }
            catch (Exception)
            {
                db.Dispose();
                throw;
            }
            var d = new Drawing { Path = path, Db = db, ReadOnly = readOnly };
            Open[path] = d;
            return d;
        }

        /// <summary>Write it to its file (or to path: the drawing is then that file), in its own format unless version is given.</summary>
        public static void Save(Drawing d, string path, DwgVersion? version)
        {
            string target = path ?? d.Path;
            if (d.ReadOnly && string.Equals(target, d.Path, StringComparison.OrdinalIgnoreCase))
                throw new ToolError($"{System.IO.Path.GetFileName(d.Path)} was opened read-only in the background: save it with another path.");
            d.Db.SaveAs(target, version ?? d.Db.OriginalFileVersion);
            if (!string.Equals(target, d.Path, StringComparison.OrdinalIgnoreCase))
            {
                Open.Remove(d.Path);
                d.Path = target;
                d.ReadOnly = false;
                Open[target] = d;
            }
            d.Modified = false;
        }

        public static void Close(Drawing d)
        {
            Open.Remove(d.Path);
            try { d.Db.Dispose(); } catch (Exception) { /* nothing left to free */ }
        }

        /// <summary>Server stop: unsaved background changes are lost (the log says which).</summary>
        public static void CloseAll()
        {
            foreach (Drawing d in Open.Values.ToList())
            {
                if (d.Modified) Log.Error("background drawing closed with unsaved changes: " + d.Path);
                Close(d);
            }
        }

        public static Dictionary<string, object> Info(Drawing d) => new Dictionary<string, object>
        {
            ["name"] = System.IO.Path.GetFileName(d.Path),
            ["path"] = d.Path,
            ["background"] = true,
            ["read_only"] = d.ReadOnly,
            ["modified"] = d.Modified,
        };
    }
}
