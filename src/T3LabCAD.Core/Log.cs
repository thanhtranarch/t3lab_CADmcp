using System;
using System.IO;

namespace T3LabCAD.Core
{
    /// <summary>Append-only log at %APPDATA%\T3LabCAD\logs\t3labcad.log (rolled over at 2 MB).</summary>
    internal static class Log
    {
        static readonly object Gate = new object();

        internal static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "T3LabCAD");

        static string FilePath => Path.Combine(DataDir, "logs", "t3labcad.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message) => Write("ERROR", message);

        static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 2_000_000)
                    {
                        File.Copy(path, path + ".1", true);
                        File.Delete(path);
                    }
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // logging must never break a tool call
            }
        }
    }
}
