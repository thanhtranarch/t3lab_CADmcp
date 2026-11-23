using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace T3LabCAD.Core
{
    /// <summary>CSV files for Excel: UTF-8 with BOM (Vietnamese text stays readable), invariant numbers.</summary>
    internal static class Csv
    {
        /// <summary>Writes the file and returns its full path. delimiter: "," (default) or ";" for Excel set to a comma-decimal locale.</summary>
        public static string Write(string path, IList<string> header, IEnumerable<IList<object>> rows, string delimiter)
        {
            string file = DocumentTools.ResolvePath(path, ".csv");
            if (!Directory.Exists(Path.GetDirectoryName(file))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(file)}");
            string sep = string.IsNullOrEmpty(delimiter) ? "," : delimiter;
            if (sep != "," && sep != ";" && sep != "\t") throw new ToolError("delimiter must be , ; or a tab.");

            var sb = new StringBuilder();
            sb.Append(string.Join(sep, header.Select(h => Cell(h, sep)))).Append("\r\n");
            foreach (IList<object> row in rows) sb.Append(string.Join(sep, row.Select(v => Cell(v, sep)))).Append("\r\n");
            try
            {
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ToolError($"Could not write {file}: {ex.Message} (is it open in Excel?)");
            }
            return file;
        }

        static string Cell(object v, string sep)
        {
            string s;
            switch (v)
            {
                case null: return "";
                case double d: s = Geo.R(d).ToString(CultureInfo.InvariantCulture); break;
                case bool b: s = b ? "TRUE" : "FALSE"; break;
                default: s = Convert.ToString(v, CultureInfo.InvariantCulture); break;
            }
            bool quote = s.Contains(sep) || s.Contains("\"") || s.Contains("\n") || s.Contains("\r") ||
                         (s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[s.Length - 1])));
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
