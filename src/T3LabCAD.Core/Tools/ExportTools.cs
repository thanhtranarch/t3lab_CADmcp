using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using static T3LabCAD.Core.Sch;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Plot to file: export_pdf (layouts of the active drawing, PlotEngine with page setup overrides) and
    /// publish_pdf (layouts of several drawings, open or not, through the Publisher with their own page setups).
    /// DXF export is save_document with a .dxf path; a PNG of the view is screenshot.
    /// </summary>
    internal static class ExportTools
    {
        public static void Register()
        {
            Registry.Add("export_pdf", "Export to PDF", ToolKind.Write,
                "Plot layouts of the active drawing to PDF (or DWFx / DWF): one multi-sheet file, or one file per layout. " +
                "Uses each layout page setup; paper, plot_style, area, scale, center and rotation override it. Model space " +
                "defaults to its extents, fit to the paper. list_options=true lists plotters, paper sizes and plot styles instead.",
                Obj(P("layouts", Strs("Layouts to plot, * = every paper layout in tab order, Model = model space (default: the current layout)")),
                    P("path", Str("Output file (separate_files: output folder). Default: next to the drawing, named after it")),
                    P("separate_files", Bool("One file per layout, named DRAWING-LAYOUT (default false: one multi-sheet file)")),
                    P("format", Enum("Output format (default pdf)", "pdf", "dwfx", "dwf")),
                    P("plotter", Str("Plotter .pc3 (default: the page setup one if it makes this format, else DWG To PDF.pc3)")),
                    P("paper", Str("Paper size: canonical name or a part of it, e.g. A3, A1, ISO_full_bleed_A1 (default: page setup)")),
                    P("plot_style", Str("Plot style table, e.g. monochrome.ctb (default: page setup)")),
                    P("area", Enum("What to plot (default: page setup; Model: extents)", "layout", "extents", "display", "limits", "window")),
                    P("window", Pts("area=window: [[x1, y1], [x2, y2]]")),
                    P("scale", Any("fit, or paper:drawing units like 1:100 (default: page setup; fit when area changes)")),
                    P("center", Bool("Center on the paper")),
                    P("rotation", Enum("Plot rotation in degrees", "0", "90", "180", "270")),
                    P("list_options", Bool("Only list plotters, paper sizes (of plotter) and plot styles; plot nothing"))),
                ExportPdf, tx: false);

            Registry.Add("publish_pdf", "Publish to PDF", ToolKind.Write,
                "Publish layouts of one or more drawings (open, or files on disk) into one multi-sheet PDF (or DWFx / DWF), like PUBLISH. " +
                "Each sheet uses its own page setup, or page_setup (a named page setup from page_setup_file) for all. " +
                "Open drawings should be saved first: sheets may come from the file on disk.",
                Obj(P("drawings", Strs("Drawing paths or open drawing names (default: the active drawing)")),
                    P("layouts", Strs("Layout names, wildcards allowed (default * = every paper layout, in tab order)")),
                    P("path", Str("Output file (default: next to the first drawing, named after it)")),
                    P("format", Enum("Output format (default pdf)", "pdf", "dwfx", "dwf")),
                    P("page_setup", Str("Named page setup to use for every sheet")),
                    P("page_setup_file", Str("Drawing or template that holds page_setup (default: each drawing)"))),
                PublishPdf, tx: false);
        }

        static readonly Dictionary<string, string> DefaultPlotter = new Dictionary<string, string>
        {
            ["pdf"] = "DWG To PDF.pc3",
            ["dwfx"] = "DWFx ePlot (XPS Compatible).pc3",
            ["dwf"] = "DWF6 ePlot.pc3",
        };

        static string FormatArg(Args a)
        {
            string format = (a.Str("format") ?? "pdf").Trim().ToLowerInvariant().TrimStart('.');
            if (!DefaultPlotter.ContainsKey(format)) throw new ToolError("format must be pdf, dwfx or dwf.");
            return format;
        }

        /// <summary>A .pc3 that writes this format (system printers such as Microsoft Print to PDF do not plot to a file reliably).</summary>
        static bool WritesFormat(string device, string format)
        {
            if (string.IsNullOrEmpty(device) || !device.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase)) return false;
            switch (format)
            {
                case "pdf": return device.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0;
                case "dwfx": return device.IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) >= 0;
                default: return device.IndexOf("DWF6", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        static string SafeFileName(string name) =>
            string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();

        /// <summary>Remove an old output file first, so a viewer that keeps it open is reported instead of failing silently.</summary>
        static void ClearTarget(string file)
        {
            if (!File.Exists(file)) return;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ToolError($"{file} is open in another program (a PDF viewer?) or read-only: close it or pick another path.");
            }
        }

        static Dictionary<string, object> FileInfoOf(string file) => new Dictionary<string, object>
        {
            ["path"] = file,
            ["size_kb"] = (long)Math.Ceiling(new FileInfo(file).Length / 1024.0),
        };

        /// <summary>Set a system variable for the duration of a plot; returns the old value.</summary>
        static object Swap(string name, object value)
        {
            object old = AcApp.GetSystemVariable(name);
            AcApp.SetSystemVariable(name, value);
            return old;
        }

        // ---------------------------------------------------------------- export_pdf

        sealed class Sheet
        {
            public string Layout;
            public ObjectId LayoutId;
            public bool Model;
        }

        static object ExportPdf(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Database db = c.Db;
            string format = FormatArg(a);
            string ext = "." + format;
            LayoutManager lm = LayoutManager.Current;

            if (a.Bool("list_options", false)) return ListOptions(c, a, format);
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new ToolError("AutoCAD is already plotting or publishing. Retry when it is done.");

            List<Sheet> sheets = SheetsToPlot(c, a.Strs("layouts"), lm);
            bool separate = a.Bool("separate_files", false);
            string drawing = doc.IsNamedDrawing ? Path.GetFileNameWithoutExtension(doc.Name) : null;

            // output files: one multi-sheet file, or a folder with DRAWING-LAYOUT files
            var jobs = new List<KeyValuePair<string, List<Sheet>>>();
            if (separate)
            {
                string folder = a.Has("path") ? DocumentTools.ResolvePath(a.ReqStr("path"))
                    : doc.IsNamedDrawing ? Path.GetDirectoryName(doc.Name)
                    : throw new ToolError("This drawing has never been saved: pass path (an output folder).");
                if (!Directory.Exists(folder)) throw new ToolError($"Folder not found: {folder}");
                foreach (Sheet s in sheets)
                    jobs.Add(new KeyValuePair<string, List<Sheet>>(
                        Path.Combine(folder, SafeFileName((drawing ?? "Drawing") + "-" + s.Layout) + ext), new List<Sheet> { s }));
            }
            else
            {
                string file = a.Has("path") ? DocumentTools.ResolvePath(a.ReqStr("path"), ext)
                    : doc.IsNamedDrawing ? Path.ChangeExtension(doc.Name, ext)
                    : throw new ToolError("This drawing has never been saved: pass path (the output file).");
                if (!file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) throw new ToolError($"path must end with {ext}.");
                if (!Directory.Exists(Path.GetDirectoryName(file))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(file)}");
                jobs.Add(new KeyValuePair<string, List<Sheet>>(file, sheets));
            }
            foreach (var job in jobs) ClearTarget(job.Key);

            string originalLayout = lm.CurrentLayout;
            object backgroundPlot = Swap("BACKGROUNDPLOT", (short)0); // the PlotEngine has to finish before we report
            var files = new List<object>();
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    string device = a.Str("plotter") ?? CommonDevice(tr, sheets, format) ?? DefaultPlotter[format];
                    foreach (var job in jobs)
                    {
                        var described = PlotJob(c, tr, lm, a, job.Value, job.Key, device);
                        string written = Written(job.Key, ext);
                        var info = FileInfoOf(written);
                        info["sheets"] = described;
                        files.Add(info);
                    }
                    tr.Commit();
                }
            }
            finally
            {
                AcApp.SetSystemVariable("BACKGROUNDPLOT", backgroundPlot);
                try
                {
                    if (!lm.CurrentLayout.Equals(originalLayout, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = originalLayout;
                }
                catch (Exception) { /* the layout is still the plotted one: harmless */ }
            }
            return new Dictionary<string, object> { ["format"] = format, ["files"] = files };
        }

        static List<Sheet> SheetsToPlot(ToolContext c, List<string> requested, LayoutManager lm)
        {
            var all = new List<KeyValuePair<int, Sheet>>();
            using (var tr = c.Db.TransactionManager.StartTransaction())
            {
                foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(c.Db.LayoutDictionaryId, OpenMode.ForRead))
                {
                    var lo = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    all.Add(new KeyValuePair<int, Sheet>(lo.TabOrder, new Sheet { Layout = lo.LayoutName, LayoutId = entry.Value, Model = lo.ModelType }));
                }
                tr.Commit();
            }
            all.Sort((x, y) => x.Key.CompareTo(y.Key));
            if (requested.Count == 0) requested = new List<string> { lm.CurrentLayout };

            var result = new List<Sheet>();
            foreach (string pattern in requested)
            {
                bool wildcard = pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;
                var matches = all.Select(kv => kv.Value)
                    .Where(s => Wild.Match(s.Layout, pattern) && (!wildcard || !s.Model)).ToList();
                if (matches.Count == 0) throw new ToolError($"No layout matches '{pattern}' (see list_layouts).");
                foreach (Sheet s in matches)
                    if (!result.Any(r => r.LayoutId == s.LayoutId)) result.Add(s);
            }
            return result;
        }

        /// <summary>The plotter every sheet's page setup already uses, when it makes this format (keeps their media as is).</summary>
        static string CommonDevice(Transaction tr, List<Sheet> sheets, string format)
        {
            var devices = sheets.Select(s => ((Layout)tr.GetObject(s.LayoutId, OpenMode.ForRead)).PlotConfigurationName)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return devices.Count == 1 && WritesFormat(devices[0], format) ? devices[0] : null;
        }

        static List<object> PlotJob(ToolContext c, Transaction tr, LayoutManager lm, Args a, List<Sheet> sheets, string file, string device)
        {
            var described = new List<object>();
            var settings = new List<PlotSettings>();
            try
            {
                using (PlotEngine engine = PlotFactory.CreatePublishEngine())
                using (var progress = new PlotProgressDialog(false, sheets.Count, true))
                {
                    for (int i = 0; i < sheets.Count; i++)
                    {
                        Sheet sheet = sheets[i];
                        // the sheet being plotted is made current, as AutoCAD's own multi-sheet plotting does
                        if (!lm.CurrentLayout.Equals(sheet.Layout, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = sheet.Layout;
                        var layout = (Layout)tr.GetObject(sheet.LayoutId, OpenMode.ForRead);
                        var ps = new PlotSettings(layout.ModelType);
                        settings.Add(ps);
                        ps.CopyFrom(layout);
                        Configure(ps, layout, a, device, true);

                        var info = new PlotInfo { Layout = sheet.LayoutId, OverrideSettings = ps };
                        new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);
                        described.Add(Describe(sheet.Layout, info.ValidatedSettings));

                        if (i == 0)
                        {
                            progress.set_PlotMsgString(PlotMessageIndex.DialogTitle, "T3Lab MCP: plotting");
                            progress.set_PlotMsgString(PlotMessageIndex.CancelJobButtonMessage, "Cancel job");
                            progress.set_PlotMsgString(PlotMessageIndex.CancelSheetButtonMessage, "Cancel sheet");
                            progress.set_PlotMsgString(PlotMessageIndex.SheetSetProgressCaption, "Job progress");
                            progress.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, "Sheet progress");
                            progress.LowerPlotProgressRange = 0;
                            progress.UpperPlotProgressRange = 100;
                            progress.PlotProgressPos = 0;
                            progress.OnBeginPlot();
                            progress.IsVisible = true;
                            engine.BeginPlot(progress, null);
                            engine.BeginDocument(info, c.Doc.Name, null, 1, true, file);
                        }
                        progress.OnBeginSheet();
                        progress.LowerSheetProgressRange = 0;
                        progress.UpperSheetProgressRange = 100;
                        progress.SheetProgressPos = 0;
                        engine.BeginPage(new PlotPageInfo(), info, i == sheets.Count - 1, null);
                        engine.BeginGenerateGraphics(null);
                        progress.SheetProgressPos = 50;
                        engine.EndGenerateGraphics(null);
                        engine.EndPage(null);
                        progress.SheetProgressPos = 100;
                        progress.OnEndSheet();
                        progress.PlotProgressPos = (i + 1) * 100 / sheets.Count;
                    }
                    engine.EndDocument(null);
                    progress.PlotProgressPos = 100;
                    progress.OnEndPlot();
                    engine.EndPlot(null);
                }
            }
            finally
            {
                foreach (PlotSettings ps in settings) ps.Dispose();
            }
            return described;
        }

        /// <summary>Page setup of the layout, with the overrides the call asked for.</summary>
        /// <summary>
        /// Apply the plot options of a call to ps, starting from basis (the layout or page setup it was copied from; may be ps itself).
        /// modelExtents: Model plots its extents when area is not given (export_pdf), not for a page setup.
        /// </summary>
        internal static void Configure(PlotSettings ps, PlotSettings basis, Args a, string device, bool modelExtents)
        {
            PlotSettingsValidator psv = PlotSettingsValidator.Current;
            bool newDevice = !string.Equals(basis.PlotConfigurationName, device, StringComparison.OrdinalIgnoreCase);
            Point2d size = basis.PlotPaperSize; // read before the device changes it
            bool model = basis.ModelType;
            psv.SetPlotConfigurationName(ps, device, null);
            psv.RefreshLists(ps);
            if (a.Has("paper")) psv.SetCanonicalMediaName(ps, MatchPaper(psv, ps, a.ReqStr("paper")));
            else if (newDevice)
            {
                // keep the sheet size of the page setup on the new plotter
                if (size.X > 0 && size.Y > 0) psv.SetClosestMediaName(ps, size.X, size.Y, PlotPaperUnit.Millimeters, false);
            }

            string area = a.Str("area")?.Trim().ToLowerInvariant();
            if (area == null && model && modelExtents) area = "extents";
            bool areaChanged = area != null && !(area == "layout" && !model);
            switch (area)
            {
                case null:
                    break;
                case "layout":
                    if (model) throw new ToolError("area=layout is for paper space layouts; Model can use extents, display, limits or window.");
                    psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                    break;
                case "extents":
                    psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                    break;
                case "display":
                    psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Display);
                    break;
                case "limits":
                    psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Limits);
                    break;
                case "window":
                    List<Point3d> w = a.Pts("window");
                    if (w.Count != 2) throw new ToolError("area=window needs window [[x1, y1], [x2, y2]].");
                    psv.SetPlotWindowArea(ps, new Extents2d(
                        Math.Min(w[0].X, w[1].X), Math.Min(w[0].Y, w[1].Y), Math.Max(w[0].X, w[1].X), Math.Max(w[0].Y, w[1].Y)));
                    psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                    break;
                default:
                    throw new ToolError("area must be layout, extents, display, limits or window.");
            }

            string scale = a.Has("scale") ? Convert.ToString(a.Raw("scale"), System.Globalization.CultureInfo.InvariantCulture).Trim() : null;
            if (scale == null && areaChanged) scale = "fit";
            if (scale != null)
            {
                if (scale.Equals("fit", StringComparison.OrdinalIgnoreCase))
                {
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                }
                else
                {
                    double ratio = LayoutTools.ParseScale(scale);
                    psv.SetUseStandardScale(ps, false);
                    psv.SetCustomPrintScale(ps, new CustomScale(1, 1 / ratio));
                }
            }

            if (a.Has("center")) psv.SetPlotCentered(ps, a.Bool("center", true));
            else if (areaChanged) psv.SetPlotCentered(ps, true);

            if (a.Has("plot_style"))
            {
                string style = a.ReqStr("plot_style").Trim();
                var styles = psv.GetPlotStyleSheetList().Cast<string>().ToList();
                string match = styles.FirstOrDefault(s => s.Equals(style, StringComparison.OrdinalIgnoreCase))
                    ?? styles.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s).Equals(style, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolError($"Plot style '{style}' not found. Available: {string.Join(", ", styles.Take(40))}.");
                psv.SetCurrentStyleSheet(ps, match);
            }

            if (a.Has("rotation"))
            {
                switch (a.ReqStr("rotation").Trim())
                {
                    case "0": psv.SetPlotRotation(ps, PlotRotation.Degrees000); break;
                    case "90": psv.SetPlotRotation(ps, PlotRotation.Degrees090); break;
                    case "180": psv.SetPlotRotation(ps, PlotRotation.Degrees180); break;
                    case "270": psv.SetPlotRotation(ps, PlotRotation.Degrees270); break;
                    default: throw new ToolError("rotation must be 0, 90, 180 or 270.");
                }
            }
        }

        /// <summary>
        /// Canonical media name for "A3", "ISO A1", "ISO_full_bleed_A1_(841.00_x_594.00_MM)"...:
        /// exact canonical or display name, else a whole-word match preferring full bleed, then landscape.
        /// </summary>
        static string MatchPaper(PlotSettingsValidator psv, PlotSettings ps, string paper)
        {
            List<string> media = psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
            string exact = media.FirstOrDefault(m => m.Equals(paper, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            for (int i = 0; i < media.Count; i++)
                if (psv.GetLocaleMediaName(ps, i).Equals(paper, StringComparison.OrdinalIgnoreCase)) return media[i];

            var words = Regex.Split(paper.Trim(), @"[\s_]+").Where(t => t.Length > 0).Select(Regex.Escape).ToList();
            var candidates = media.Where(m => words.All(t =>
                Regex.IsMatch(m, @"(^|[_\s(])" + t + @"($|[_\s)])", RegexOptions.IgnoreCase))).ToList();
            if (candidates.Count == 0)
                throw new ToolError($"No paper size matches '{paper}' on {ps.PlotConfigurationName}. Some sizes: " +
                                    string.Join(", ", media.Take(25)) + ". export_pdf list_options=true lists them all.");
            return candidates
                .OrderByDescending(m => m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(Landscape)
                .ThenBy(m => m.Length)
                .First();
        }

        static bool Landscape(string media)
        {
            Match m = Regex.Match(media, @"\(([\d.]+)_x_([\d.]+)_", RegexOptions.IgnoreCase);
            return m.Success && double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >=
                   double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static Dictionary<string, object> Describe(string layout, PlotSettings s)
        {
            string scale;
            if (s.UseStandardScale && s.StdScaleType == StdScaleType.ScaleToFit) scale = "fit";
            else
            {
                CustomScale cs = s.CustomPrintScale;
                scale = cs.Denominator > 0 ? LayoutTools.ScaleText(cs.Numerator / cs.Denominator) : "?";
            }
            return new Dictionary<string, object>
            {
                ["layout"] = layout,
                ["plotter"] = s.PlotConfigurationName,
                ["paper"] = s.CanonicalMediaName,
                ["plot_style"] = s.CurrentStyleSheet,
                ["area"] = s.PlotType.ToString().ToLowerInvariant(),
                ["scale"] = scale,
                ["rotation"] = s.PlotRotation.ToString().Replace("Degrees", "").TrimStart('0').PadLeft(1, '0'),
            };
        }

        /// <summary>The file the plotter wrote (some drivers add the extension again).</summary>
        static string Written(string file, string ext)
        {
            if (File.Exists(file)) return file;
            if (File.Exists(file + ext)) { File.Move(file + ext, file); return file; }
            throw new ToolError($"AutoCAD finished plotting but {file} was not written. Check the plotter and paper (list_options=true).");
        }

        static object ListOptions(ToolContext c, Args a, string format)
        {
            PlotSettingsValidator psv = PlotSettingsValidator.Current;
            using (var tr = c.Db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout), OpenMode.ForRead);
                using (var ps = new PlotSettings(layout.ModelType))
                {
                    ps.CopyFrom(layout);
                    string device = a.Str("plotter") ?? (WritesFormat(layout.PlotConfigurationName, format) ? layout.PlotConfigurationName : DefaultPlotter[format]);
                    psv.SetPlotConfigurationName(ps, device, null);
                    psv.RefreshLists(ps);
                    List<string> media = psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
                    var papers = new List<object>();
                    for (int i = 0; i < media.Count && papers.Count < 400; i++)
                        papers.Add(new Dictionary<string, object> { ["name"] = media[i], ["display"] = psv.GetLocaleMediaName(ps, i) });
                    tr.Commit();
                    return new Dictionary<string, object>
                    {
                        ["plotter"] = device,
                        ["plotters"] = psv.GetPlotDeviceList().Cast<string>().Where(d => d.EndsWith(".pc3", StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList(),
                        ["papers"] = papers,
                        ["plot_styles"] = psv.GetPlotStyleSheetList().Cast<string>().Cast<object>().ToList(),
                        ["current_layout"] = new Dictionary<string, object>
                        {
                            ["name"] = layout.LayoutName,
                            ["plotter"] = layout.PlotConfigurationName,
                            ["paper"] = layout.CanonicalMediaName,
                            ["plot_style"] = layout.CurrentStyleSheet,
                        },
                    };
                }
            }
        }

        // ---------------------------------------------------------------- publish_pdf

        static object PublishPdf(ToolContext c, Args a)
        {
            string format = FormatArg(a);
            string ext = "." + format;
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new ToolError("AutoCAD is already plotting or publishing. Retry when it is done.");

            // drawings: open ones by name or path, others must exist on disk
            List<string> requested = a.Strs("drawings");
            if (requested.Count == 0)
            {
                if (!c.Doc.IsNamedDrawing) throw new ToolError("The active drawing has never been saved: save it first (publishing reads drawing files).");
                requested.Add(c.Doc.Name);
            }
            var drawings = new List<string>();
            var warnings = new List<object>();
            foreach (string name in requested)
            {
                Document open = DocumentTools.Find(name);
                string path;
                if (open != null)
                {
                    if (!open.IsNamedDrawing) throw new ToolError($"'{name}' has never been saved: save it first.");
                    path = open.Name;
                    dynamic acadDoc = open.GetAcadDocument();
                    if (!(bool)acadDoc.Saved) warnings.Add($"{Path.GetFileName(path)} has unsaved changes: save it so the PDF shows them.");
                }
                else
                {
                    path = DocumentTools.ResolvePath(name, ".dwg");
                    if (!File.Exists(path)) throw new ToolError($"Drawing not found: {path}");
                }
                if (!drawings.Contains(path, StringComparer.OrdinalIgnoreCase)) drawings.Add(path);
            }

            List<string> patterns = a.Strs("layouts");
            if (patterns.Count == 0) patterns.Add("*");
            string pageSetup = a.Str("page_setup");
            string pageSetupFile = a.Has("page_setup_file") ? DocumentTools.ResolvePath(a.ReqStr("page_setup_file"), ".dwg") : null;
            if (pageSetupFile != null && !File.Exists(pageSetupFile)) throw new ToolError($"page_setup_file not found: {pageSetupFile}");

            var entries = new DsdEntryCollection();
            var sheets = new List<object>();
            foreach (string path in drawings)
            {
                foreach (string layout in LayoutsOf(path, patterns))
                {
                    entries.Add(new DsdEntry
                    {
                        DwgName = path,
                        Layout = layout,
                        Title = SafeFileName(Path.GetFileNameWithoutExtension(path) + "-" + layout),
                        Nps = pageSetup ?? "",
                        NpsSourceDwg = pageSetup == null ? "" : pageSetupFile ?? path,
                    });
                    sheets.Add(new Dictionary<string, object> { ["drawing"] = Path.GetFileName(path), ["layout"] = layout });
                }
            }
            if (entries.Count == 0) throw new ToolError("No layout matches in these drawings (see list_layouts).");

            string output = a.Has("path") ? DocumentTools.ResolvePath(a.ReqStr("path"), ext) : Path.ChangeExtension(drawings[0], ext);
            if (!output.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) throw new ToolError($"path must end with {ext}.");
            if (!Directory.Exists(Path.GetDirectoryName(output))) throw new ToolError($"Folder not found: {Path.GetDirectoryName(output)}");
            ClearTarget(output);

            string work = Path.Combine(Path.GetTempPath(), "T3LabCAD_publish_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            string log = Path.Combine(work, "publish.log");
            try
            {
                var dsd = new DsdData();
                dsd.SetDsdEntryCollection(entries);
                dsd.ProjectPath = work;
                dsd.LogFilePath = log;
                dsd.SheetType = format == "pdf" ? SheetType.MultiPdf : format == "dwfx" ? SheetType.MultiDwfx : SheetType.MultiDwf;
                dsd.NoOfCopies = 1;
                dsd.DestinationName = output;
                dsd.SheetSetName = "T3Lab MCP";
                dsd.IsHomogeneous = false;

                // PromptForDwfName is not settable through the API in every release: write the DSD, fix it, read it back
                string dsdFile = Path.Combine(work, "publish.dsd");
                dsd.WriteDsd(dsdFile);
                string text = File.ReadAllText(dsdFile);
                File.WriteAllText(dsdFile, Regex.Replace(text, @"PromptForDwfName\s*=\s*TRUE", "PromptForDwfName=FALSE", RegexOptions.IgnoreCase));
                dsd = new DsdData();
                dsd.ReadDsd(dsdFile);

                PlotConfig config = PlotConfigManager.SetCurrentConfig(DefaultPlotter[format]);
                object backgroundPlot = Swap("BACKGROUNDPLOT", (short)0);
                object collate = Swap("PUBLISHCOLLATE", (short)1);
                try
                {
                    AcApp.Publisher.PublishExecute(dsd, config);
                }
                finally
                {
                    AcApp.SetSystemVariable("BACKGROUNDPLOT", backgroundPlot);
                    AcApp.SetSystemVariable("PUBLISHCOLLATE", collate);
                }

                if (!File.Exists(output))
                {
                    string detail = File.Exists(log) ? " Log: " + Tail(File.ReadAllText(log), 1500) : "";
                    throw new ToolError($"Publishing finished but {output} was not written.{detail}");
                }
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch (Exception) { /* temp files only */ }
            }

            var result = FileInfoOf(output);
            result["format"] = format;
            result["sheet_count"] = sheets.Count;
            result["sheets"] = sheets;
            if (pageSetup != null) result["page_setup"] = pageSetup;
            if (warnings.Count > 0) result["warnings"] = warnings;
            return result;
        }

        static string Tail(string text, int max) => text.Length <= max ? text.Trim() : "..." + text.Substring(text.Length - max).Trim();

        /// <summary>Layout names of a drawing in tab order (open drawings from memory, others from the file).</summary>
        static List<string> LayoutsOf(string path, List<string> patterns)
        {
            Document open = DocumentTools.Find(path);
            Database side = null;
            try
            {
                Database db;
                if (open != null) db = open.Database;
                else
                {
                    side = new Database(false, true);
                    side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                    db = side;
                }
                var layouts = new List<KeyValuePair<int, string>>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (DBDictionaryEntry entry in (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
                    {
                        var lo = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                        bool wanted = patterns.Any(p =>
                            Wild.Match(lo.LayoutName, p) && (!lo.ModelType || (p.IndexOf('*') < 0 && p.IndexOf('?') < 0)));
                        if (wanted) layouts.Add(new KeyValuePair<int, string>(lo.TabOrder, lo.LayoutName));
                    }
                    tr.Commit();
                }
                return layouts.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            }
            finally
            {
                side?.Dispose();
            }
        }
    }
}
