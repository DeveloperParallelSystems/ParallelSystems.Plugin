using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models;
using ParallelSystemsPlugin.UI.Dialogs;
using static ParallelSystemsPlugin.Helpers.Elements;

using ParallelSystemsPlugin.Compatibility;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RenameSheetNumbersCommand : IExternalCommand
    {
        // ==============================
        // SETTINGS
        // ==============================
        private const int RUN_SCRIPT = 0; // 0 = CHECK ONLY, 1 = EXECUTE
        private string _filterContains = ""; // filter text
        private const string PAGE_NAME = "Sheet Check";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            _filterContains = AppConfig.CurrentConfig.ToolsConfig?.SheetCheckAndBomCheckConfig?.FilterContains ?? "";
            Document doc = commandData.Application.ActiveUIDocument.Document;
            UIApplication uiapp = commandData.Application;

            string GetParamText(Element elem, string paramName)
            {
                try
                {
                    var p = elem.LookupParameter(paramName);
                    if (p != null && p.HasValue)
                        return p.AsString() ?? p.AsValueString() ?? "";
                }
                catch { }
                return "";
            }

            string GetAssemblyName(AssemblyInstance assembly)
            {
                var name = GetParamText(assembly, "Assembly Name");
                if (!string.IsNullOrWhiteSpace(name))
                    return name.Trim();

                var num = GetParamText(assembly, "Assembly Number");
                if (!string.IsNullOrWhiteSpace(num))
                    return num.Trim();

                return assembly.Name?.Trim() ?? "";
            }

            ElementId GetAssociatedAssemblyId(ViewSheet sheet)
            {
                try
                {
                    var prop = sheet.GetType().GetProperty("AssociatedAssemblyInstanceId");
                    if (prop != null)
                    {
                        var val = prop.GetValue(sheet) as ElementId;
                        if (!RevitApiCompatibility.IsInvalidElementId(val))
                            return val;
                    }
                }
                catch { }

                try
                {
                    var prop = sheet.GetType().GetProperty("AssemblyInstanceId");
                    if (prop != null)
                    {
                        var val = prop.GetValue(sheet) as ElementId;
                        if (!RevitApiCompatibility.IsInvalidElementId(val))
                            return val;
                    }
                }
                catch { }

                return null;
            }



            bool PassesFilter(string text)
            {
                if (string.IsNullOrWhiteSpace(_filterContains))
                    return true;

                return (text ?? "").ToLower().Contains(_filterContains.ToLower());
            }

            // --- Collect assemblies
            var assemblies = new FilteredElementCollector(doc)
                .OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>()
                .ToList();

            var assemblyMap = assemblies.ToDictionary(
                a => RevitApiCompatibility.GetElementIdValue(a.Id),
                a => GetAssemblyName(a)
            );

            // --- Collect sheets
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToList();

            var existingSheetNumbers = new HashSet<string>(
                sheets.Select(s => (s.SheetNumber ?? "").Trim())
            );

            var matches = new List<object>();
            var toFix = new List<SheetFixItem>();
            var skippedDuplicates = new List<object>();
            var skippedByFilter = new List<object>();
            var errors = new List<object>();

            foreach (var sh in sheets)
            {
                var aid = GetAssociatedAssemblyId(sh);
                if (aid == null) continue;

                long asmId = RevitApiCompatibility.GetElementIdValue(aid);
                string assemblyName = assemblyMap.ContainsKey(asmId)
                    ? assemblyMap[asmId]?.Trim()
                    : "";

                if (string.IsNullOrWhiteSpace(assemblyName))
                    continue;

                if (!PassesFilter(assemblyName))
                {
                    skippedByFilter.Add(new
                    {
                        AssemblyName = assemblyName,
                        SheetNumber = sh.SheetNumber,
                        SheetName = sh.Name
                    });
                    continue;
                }

                string sheetNumber = (sh.SheetNumber ?? "").Trim();
                string sheetName = (sh.Name ?? "").Trim();
                string tabPreview = string.IsNullOrEmpty(sheetName)
                    ? sheetNumber
                    : $"{sheetNumber} - {sheetName}";

                if (assemblyName == sheetNumber)
                {
                    matches.Add(new
                    {
                        AssemblyName = assemblyName,
                        SheetNumber = sheetNumber,
                        SheetName = sheetName,
                        TabPreview = tabPreview,
                        Status = "MATCH"
                    });
                    continue;
                }

                if (existingSheetNumbers.Contains(assemblyName))
                {
                    skippedDuplicates.Add(new
                    {
                        AssemblyName = assemblyName,
                        CurrentSheetNumber = sheetNumber,
                        SheetName = sheetName,
                        TabPreview = tabPreview,
                        Reason = "Target sheet number already exists"
                    });
                    continue;
                }

                toFix.Add(new SheetFixItem
                {
                    SheetId = RevitApiCompatibility.GetElementIdValue(sh.Id),
                    AssemblyName = assemblyName,
                    OldSheetNumber = sheetNumber,
                    NewSheetNumber = assemblyName,
                    SheetName = sheetName,
                    TabPreview = tabPreview
                });
            }

            int updated = 0;

            if (toFix.Any())
            {
                var dialog = new ConfirmApplyRenameSheetDialog(toFix);
                dialog.ShowDialog();

                if (dialog.ExecuteFix)
                {
                    if(dialog.SelectedItem != null && dialog.SelectedItem.Count > 0)
                    {
                        using (Transaction t = new Transaction(doc, "Fix Assembly Sheet Numbers"))
                        {
                            t.Start();

                            foreach (var item in dialog.SelectedItem)
                            {
                                try
                                {
                                    var targetSheet = doc.GetElement(RevitApiCompatibility.CreateElementId(item.SheetId)) as ViewSheet;
                                    if (targetSheet == null)
                                        throw new InvalidOperationException("The selected sheet no longer exists.");
                                    targetSheet.SheetNumber = item.NewSheetNumber;
                                }
                                catch (Exception ex)
                                {
                                    AppDialog.Error(uiapp, PAGE_NAME, ex.Message);
                                }
                            }

                            t.Commit();
                            AppDialog.Info(uiapp, PAGE_NAME, "Success");//
                        }
                    }

                    else
                    {
                        AppDialog.Info(uiapp, PAGE_NAME, "No Assembly Selected");//
                    }
                    
                }
            }
            else
            {
                AppDialog.Info(uiapp, PAGE_NAME, "No assembly needs fixing");//
                return Result.Succeeded;
            }

            return Result.Succeeded;
        }
    }
}
