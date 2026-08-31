using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models.Configs;

using ParallelSystemsPlugin.Compatibility;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class BOMCheckCommand : IExternalCommand
    {
        // ==========================================
        // SETTINGS
        // ==========================================
        private string FILTER_TEXT = AppConfig.CurrentConfig.ToolsConfig.SheetCheckAndBomCheckConfig.FilterContains;
        private string EXCLUDE_TEXT = AppConfig.CurrentConfig.ToolsConfig.SheetCheckAndBomCheckConfig.ExcludeText;
        private const int MIN_DUP_COUNT = 2;
        private const int MODE = 0; // 0 = Scan, 1 = Delete
        private const string PAGE_NAME = "BOM CHECK";

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {

            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            UIApplication uiapp = commandData.Application;

            try
            {
                var result = Run(doc, uiapp);

                // Simple output dialog (you can replace with JSON export if needed)
                AppDialog.Info("BOM Scan Result",
                    $"Mode: {result.Mode}\n" +
                    $"Duplicate: {result.DuplicateBOM.Count}\n" +
                    $"No BOM: {result.NoBOM.Count}\n" +
                    $"Needs Rebuild: {result.NeedsRebuild.Count}\n" +
                    $"Clean: {result.CleanFilteredAssemblies.Count}"
                );

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ==========================================
        // MAIN LOGIC
        // ==========================================
        private BomScanResult Run(Document doc, UIApplication uiapp)
        {
            var sheetData = new Dictionary<long, SheetInfo>();

            // STEP 1: Collect Sheets
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>();

            foreach (var sheet in sheets)
            {
                string name = GetAssemblyName(doc, sheet);
                if (string.IsNullOrEmpty(name)) continue;
                if (!PassesNameFilter(name)) continue;

                var quad = GetQuadrant1(sheet);
                if (quad == null) continue;

                sheetData[RevitApiCompatibility.GetElementIdValue(sheet.Id)] = new SheetInfo
                {
                    SheetId = RevitApiCompatibility.GetElementIdValue(sheet.Id),
                    SheetName = sheet.Name,
                    AssemblyName = name,
                    Quadrant = quad.Value,
                    Groups = new List<Group>()
                };
            }

            var filteredAssemblies = sheetData.Values
                .Select(x => x.AssemblyName)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            // STEP 2: Scan Groups
            var groups = new FilteredElementCollector(doc)
                .OfClass(typeof(Group))
                .WhereElementIsNotElementType()
                .Cast<Group>();

            foreach (var g in groups)
            {
                if (!IsDetailGroup(g)) continue;

                long sid = RevitApiCompatibility.GetElementIdValue(g.OwnerViewId);
                if (!sheetData.ContainsKey(sid)) continue;

                var sheet = doc.GetElement(RevitApiCompatibility.CreateElementId(sid)) as ViewSheet;
                var bbox = GetBBox(sheet, g);

                if (bbox == null) continue;

                if (Intersects(bbox.Value, sheetData[sid].Quadrant))
                    sheetData[sid].Groups.Add(g);
            }

            // STEP 3: Classification
            var dup = new List<string>();
            var nobom = new List<string>();
            var rebuild = new List<string>();
            var duplicateSheetIds = new List<long>();

            foreach (var kvp in sheetData)
            {
                var data = kvp.Value;
                int count = data.Groups.Count;

                if (count >= MIN_DUP_COUNT)
                {
                    dup.Add(data.AssemblyName);
                    duplicateSheetIds.Add(data.SheetId);
                }
                else if (count == 0)
                {
                    nobom.Add(data.AssemblyName);
                }
                else
                {
                    var gname = SafeStr(data.Groups.First().Name);
                    if (Norm(gname) != Norm(data.AssemblyName))
                        rebuild.Add(data.AssemblyName);
                }
            }

            dup = dup.Distinct().OrderBy(x => x).ToList();
            nobom = nobom.Distinct().OrderBy(x => x).ToList();
            rebuild = rebuild.Distinct().OrderBy(x => x).ToList();

            var issueNames = new HashSet<string>(dup.Concat(nobom).Concat(rebuild));

            var clean = filteredAssemblies
                .Where(x => !issueNames.Contains(x))
                .OrderBy(x => x)
                .ToList();

            // STEP 4: DELETE
            var deletedGroupIds = new List<long>();
            var deletedNames = new List<string>();

            // OUTPUT
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"Duplicate BOM ({dup.Count})");
            foreach (var d in dup) sb.AppendLine($" - {d}");

            if (duplicateSheetIds.Any())
            {
                if (AppDialog.Confirm(uiapp, "Confirm Delete", $"{sb.ToString()} \nThis will delete duplicate BOM groups. Continue?"))
                {
                    using (Transaction t = new Transaction(doc, "Delete Duplicate BOMs"))
                    {
                        t.Start();

                        foreach (var sid in duplicateSheetIds)
                        {
                            foreach (var g in sheetData[sid].Groups)
                            {
                                try
                                {
                                    var groupType = g.GroupType;
                                    Debug.WriteLine("Group Type:" + groupType);
                                    Debug.WriteLine("Group Name:" + groupType.Name);
                                    Debug.WriteLine("Group Family Name:" + groupType.FamilyName);
                                    doc.Delete(g.Id);
                                    deletedGroupIds.Add(RevitApiCompatibility.GetElementIdValue(g.Id));
                                }
                                catch { }
                            }

                            deletedNames.Add(sheetData[sid].AssemblyName);
                        }

                        t.Commit();
                    }
                }
            }

            return new BomScanResult
            {
                Mode = MODE,
                DuplicateBOM = dup,
                NoBOM = nobom,
                NeedsRebuild = rebuild,
                CleanFilteredAssemblies = clean,
                DeletedGroupIds = deletedGroupIds.Distinct().ToList(),
                DeletedAssemblyNames = deletedNames.Distinct().ToList()
            };
        }

        // ==========================================
        // HELPERS
        // ==========================================
        private string SafeStr(object v) => v?.ToString().Trim() ?? "";

        private string Norm(string s) => SafeStr(s).ToLower();

        private bool ContainsFilter(string text, string keyword)
            => string.IsNullOrEmpty(keyword) || Norm(text).Contains(Norm(keyword));

        private bool ContainsExclude(string text, string keyword)
            => !string.IsNullOrEmpty(keyword) && Norm(text).Contains(Norm(keyword));

        private bool PassesNameFilter(string name)
            => ContainsFilter(name, FILTER_TEXT) && !ContainsExclude(name, EXCLUDE_TEXT);

        private bool IsDetailGroup(Element e)
            => e is Group g && g.Category?.Name == "Detail Groups";

        private string GetAssemblyName(Document doc, ViewSheet sheet)
        {
            try
            {
                var aid = sheet.AssociatedAssemblyInstanceId;
                if (aid != ElementId.InvalidElementId)
                {
                    var a = doc.GetElement(aid);
                    return SafeStr(a?.Name);
                }
            }
            catch { }
            return "";
        }

        private (double, double, double, double)? GetQuadrant1(ViewSheet sheet)
        {
            try
            {
                var o = sheet.Outline;
                double midX = (o.Min.U + o.Max.U) / 2.0;
                double midY = (o.Min.V + o.Max.V) / 2.0;
                return (midX, midY, o.Max.U, o.Max.V);
            }
            catch { return null; }
        }

        private (double, double, double, double)? GetBBox(ViewSheet sheet, Group g)
        {
            try
            {
                var bb = g.get_BoundingBox(sheet);
                if (bb != null)
                    return (bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
            }
            catch { }
            return null;
        }

        private bool Intersects(
            (double, double, double, double) a,
            (double, double, double, double) b)
        {
            return !(a.Item3 < b.Item1 || a.Item1 > b.Item3 ||
                     a.Item4 < b.Item2 || a.Item2 > b.Item4);
        }

        // ==========================================
        // DATA CLASS
        // ==========================================
        private sealed class BomScanResult
        {
            public int Mode { get; set; }
            public List<string> DuplicateBOM { get; set; }
            public List<string> NoBOM { get; set; }
            public List<string> NeedsRebuild { get; set; }
            public List<string> CleanFilteredAssemblies { get; set; }
            public List<long> DeletedGroupIds { get; set; }
            public List<string> DeletedAssemblyNames { get; set; }
        }

        private sealed class SheetInfo
        {
            public long SheetId;
            public string SheetName;
            public string AssemblyName;
            public (double, double, double, double) Quadrant;
            public List<Group> Groups;
        }
    }
}
