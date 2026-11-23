using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>Drawing files on disk: find, rename, delete (to the Recycle Bin).</summary>
    internal static class FileTools
    {
        /// <summary>delete_drawing_file only touches AutoCAD files.</summary>
        static readonly HashSet<string> DeletableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dwg", ".dxf", ".dwt", ".dws", ".bak", ".sv$", ".dwl", ".dwl2",
        };

        public static void Register()
        {
            Registry.Add("list_drawing_files", "Find drawing files", ToolKind.Read,
                "List drawing files in a folder (default: the folder of the active drawing, else Documents), with size, " +
                "modified time and whether each is open in AutoCAD. Also lists subfolders, to browse before open_document.",
                Obj(P("folder", Str("Folder to list (default: folder of the active drawing)")),
                    P("pattern", Str("File pattern(s), ; separated (default *.dwg), e.g. *.dwg;*.dxf")),
                    P("recursive", Bool("Include subfolders (default false)")),
                    P("sort", Enum("name (default) or modified (newest first)", "name", "modified")),
                    P("limit", Int("Max files returned (default 300, max 2000)"))),
                ListFiles, needsDocument: false, tx: false, background: true);

            Registry.Add("rename_document", "Rename drawing file", ToolKind.App,
                "Rename a drawing file on disk. An open drawing is closed, renamed and opened again (refused when it has unsaved " +
                "changes unless save_changes=true). new_name is a file name (same folder) or a full path; the extension stays. " +
                "Xrefs and sheet sets that point at the old name are not updated.",
                ObjR("new_name",
                    P("document", Str("Open drawing to rename (name or path, default the active drawing)")),
                    P("path", Str("Or: a drawing file on disk")),
                    P("new_name", Str("New file name, e.g. A-101.dwg, or a full path")),
                    P("save_changes", Bool("Save unsaved changes before renaming (default false)")),
                    P("reopen", Bool("Open the renamed drawing again when it was open (default true)"))),
                RenameDocument, needsDocument: false, destructive: true);

            Registry.Add("delete_drawing_file", "Delete drawing file", ToolKind.App,
                "Delete a drawing file (.dwg, .dxf, .dwt, .dws, .bak, .sv$) that is not open. Goes to the Recycle Bin unless permanent=true; " +
                "on network or removable drives (no Recycle Bin) permanent=true is required.",
                ObjR("path", P("path", Str("File to delete")), P("permanent", Bool("Delete for good instead of the Recycle Bin (default false)"))),
                DeleteFile, needsDocument: false, destructive: true);
        }

        // ---------------------------------------------------------------- list

        /// <summary>Runs on the socket thread (file system only); one main-thread call reads the open drawings.</summary>
        static object ListFiles(ToolContext unused, Args a)
        {
            string activeFolder = null;
            var open = Entry.Main.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Document d in AcApp.DocumentManager)
                    if (d.IsNamedDrawing) names.Add(d.Name);
                Document active = AcApp.DocumentManager.MdiActiveDocument;
                if (active != null && active.IsNamedDrawing) activeFolder = Path.GetDirectoryName(active.Name);
                return names;
            });

            string folder = a.Str("folder");
            folder = string.IsNullOrWhiteSpace(folder)
                ? activeFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : folder.Trim().Trim('"');
            if (!Path.IsPathRooted(folder))
            {
                if (activeFolder == null) throw new ToolError($"'{folder}' is not a full path.");
                folder = Path.Combine(activeFolder, folder);
            }
            folder = Path.GetFullPath(folder);
            if (!Directory.Exists(folder)) throw new ToolError($"Folder not found: {folder}");

            string[] patterns = (a.Str("pattern") ?? "*.dwg").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (patterns.Length == 0) patterns = new[] { "*.dwg" };
            bool recursive = a.Bool("recursive", false);
            int limit = Math.Max(1, Math.Min(2000, a.Int("limit", 300)));
            const int ScanCap = 20000;

            var found = new List<FileInfo>();
            int skippedFolders = 0;
            bool capped = false;
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0 && !capped)
            {
                string dir = pending.Pop();
                try
                {
                    // the Win32 pattern also matches longer extensions (*.dwg -> x.dwgold): filter again exactly
                    foreach (string f in Directory.EnumerateFiles(dir))
                    {
                        if (!Wild.Any(Path.GetFileName(f), patterns)) continue;
                        found.Add(new FileInfo(f));
                        if (found.Count >= ScanCap) { capped = true; break; }
                    }
                    if (recursive)
                        foreach (string sub in Directory.EnumerateDirectories(dir))
                            if (!IsHiddenOrSystem(sub)) pending.Push(sub);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    skippedFolders++;
                }
            }

            IEnumerable<FileInfo> ordered = string.Equals(a.Str("sort"), "modified", StringComparison.OrdinalIgnoreCase)
                ? found.OrderByDescending(f => f.LastWriteTime)
                : found.OrderBy(f => f.DirectoryName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var files = new List<object>();
            foreach (FileInfo f in ordered.Take(limit))
            {
                var item = new Dictionary<string, object>
                {
                    ["name"] = f.Name,
                    ["path"] = f.FullName,
                    ["size_kb"] = (long)Math.Ceiling(f.Length / 1024.0),
                    ["modified"] = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                };
                if (open.Contains(f.FullName)) item["open"] = true;
                if (f.IsReadOnly) item["read_only"] = true;
                files.Add(item);
            }

            var result = new Dictionary<string, object>
            {
                ["folder"] = folder,
                ["pattern"] = string.Join(";", patterns),
                ["total"] = found.Count,
                ["returned"] = files.Count,
                ["has_more"] = found.Count > files.Count || capped,
                ["files"] = files,
            };
            if (!recursive)
            {
                try
                {
                    result["subfolders"] = Directory.EnumerateDirectories(folder).Where(d => !IsHiddenOrSystem(d))
                        .Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(200).Cast<object>().ToList();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    // listing the files worked; subfolders are a bonus
                }
            }
            if (skippedFolders > 0) result["folders_not_readable"] = skippedFolders;
            return result;
        }

        static bool IsHiddenOrSystem(string dir)
        {
            try { return (File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
            catch (Exception) { return true; }
        }

        // ---------------------------------------------------------------- rename

        static object RenameDocument(ToolContext c, Args a)
        {
            DocumentCollection dm = AcApp.DocumentManager;
            Document d;
            string oldPath;
            if (a.Has("document"))
            {
                string name = a.ReqStr("document");
                d = DocumentTools.Find(name) ?? throw new ToolError($"No open drawing matches '{name}'. To rename a closed file pass path.");
                oldPath = d.Name;
            }
            else if (a.Has("path"))
            {
                oldPath = DocumentTools.ResolvePath(a.ReqStr("path"), ".dwg");
                d = DocumentTools.Find(oldPath);
            }
            else
            {
                d = dm.MdiActiveDocument ?? throw new ToolError("No drawing is open: pass path (a file on disk).");
                oldPath = d.Name;
            }
            if (d != null && !d.IsNamedDrawing)
                throw new ToolError("This drawing has never been saved, so there is no file to rename: use save_document with a path.");
            if (d == null && !File.Exists(oldPath)) throw new ToolError($"File not found: {oldPath}");

            string newPath = TargetPath(oldPath, a.ReqStr("new_name"));
            if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase)) throw new ToolError("new_name is the current name.");
            if (File.Exists(newPath)) throw new ToolError($"{newPath} already exists.");
            if (!Directory.Exists(Path.GetDirectoryName(newPath))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(newPath)}");

            if (d == null)
            {
                Move(oldPath, newPath);
                return new Dictionary<string, object> { ["renamed"] = oldPath, ["to"] = newPath, ["was_open"] = false };
            }

            dynamic acadDoc = d.GetAcadDocument();
            bool unsaved = !(bool)acadDoc.Saved;
            bool readOnly = d.IsReadOnly;
            if (unsaved && !a.Bool("save_changes", false))
                throw new ToolError("The drawing has unsaved changes. Pass save_changes=true to save them into the renamed file, " +
                                    "or save / close the drawing first.");
            if (unsaved && readOnly) throw new ToolError("The drawing is open read-only, so its unsaved changes cannot be saved.");

            Document active = dm.MdiActiveDocument;
            bool wasActive = active == d;
            if (unsaved) d.CloseAndSave(oldPath);
            else d.CloseAndDiscard();

            try
            {
                Move(oldPath, newPath);
            }
            catch (ToolError te)
            {
                dm.Open(oldPath, readOnly);
                throw new ToolError(te.Message + " The drawing was opened again under its old name.");
            }

            Document reopened = null;
            if (a.Bool("reopen", true))
            {
                reopened = dm.Open(newPath, readOnly);
                if (!wasActive && active != null)
                {
                    try { dm.MdiActiveDocument = active; }
                    catch (Exception) { /* the other drawing was closed meanwhile */ }
                }
            }
            return new Dictionary<string, object>
            {
                ["renamed"] = oldPath,
                ["to"] = newPath,
                ["was_open"] = true,
                ["saved_changes"] = unsaved,
                ["reopened"] = reopened != null,
            };
        }

        /// <summary>new_name: a file name (same folder as the old file) or a path; the extension cannot change.</summary>
        static string TargetPath(string oldPath, string newName)
        {
            newName = newName.Trim().Trim('"');
            string oldExt = Path.GetExtension(oldPath);
            string target = Path.IsPathRooted(newName) ? newName : Path.Combine(Path.GetDirectoryName(oldPath), newName);
            string ext = Path.GetExtension(target);
            if (ext.Length == 0) target += oldExt;
            else if (!ext.Equals(oldExt, StringComparison.OrdinalIgnoreCase))
                throw new ToolError($"Renaming cannot change the file type ({oldExt} -> {ext}). Use save_document to save in another format.");
            if (Path.GetFileNameWithoutExtension(target).Trim().Length == 0 || Path.GetFileName(target).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ToolError($"'{newName}' is not a valid file name.");
            return Path.GetFullPath(target);
        }

        static void Move(string from, string to)
        {
            try { File.Move(from, to); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ToolError($"Could not rename {from}: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- delete

        static object DeleteFile(ToolContext c, Args a)
        {
            string path = DocumentTools.ResolvePath(a.ReqStr("path"));
            if (!File.Exists(path)) throw new ToolError($"File not found: {path}");
            if (!DeletableExtensions.Contains(Path.GetExtension(path)))
                throw new ToolError("Only AutoCAD files can be deleted here (.dwg, .dxf, .dwt, .dws, .bak, .sv$).");
            if (DocumentTools.Find(path) != null) throw new ToolError($"{Path.GetFileName(path)} is open in AutoCAD: close_document first.");
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            catch (UnauthorizedAccessException)
            {
                throw new ToolError($"{path} is read-only or you have no permission to delete it.");
            }
            catch (IOException)
            {
                throw new ToolError($"{path} is in use (open in another AutoCAD or program).");
            }

            bool permanent = a.Bool("permanent", false);
            if (permanent)
            {
                File.Delete(path);
            }
            else
            {
                if (!HasRecycleBin(path))
                    throw new ToolError("This drive has no Recycle Bin (network or removable): pass permanent=true to delete the file for good.");
                SendToRecycleBin(path);
            }
            return new Dictionary<string, object> { ["deleted"] = path, ["recycle_bin"] = !permanent };
        }

        static bool HasRecycleBin(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(path));
                return drive.DriveType == DriveType.Fixed;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShFileOp
        {
            public IntPtr Hwnd;
            public uint Func;
            public string From;
            public string To;
            public ushort Flags;
            [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
            public IntPtr NameMappings;
            public string ProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        static extern int SHFileOperation(ref ShFileOp op);

        const uint FoDelete = 3;
        const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

        static void SendToRecycleBin(string path)
        {
            var op = new ShFileOp
            {
                Func = FoDelete,
                From = path + "\0\0",
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
            };
            int code = SHFileOperation(ref op);
            if (code != 0 || op.AnyOperationsAborted || File.Exists(path))
                throw new ToolError($"Could not move {path} to the Recycle Bin (error 0x{code:X}). Nothing was deleted.");
        }
    }
}
