using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RenameImportCommand : IExternalCommand
    {
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

            Document doc = commandData.Application.ActiveUIDocument.Document;
            var uiapp = commandData.Application;

            string path = AppConfig.CurrentConfig.ToolsConfig?.RenamingConfig?.CsvPath ?? "";

            if (string.IsNullOrEmpty(path))
            {
                AppDialog.Info(uiapp, "Import", "CSV path is not defined in the configurations.");
                return Result.Succeeded;
            }

            if (!File.Exists(path))
            {
                AppDialog.Info(uiapp, "Import", "File does not exists.");
                return Result.Succeeded;
            }

            try
            {
                int updated = 0;
                AssemblyCSVHelpers.Import(doc, path, out updated);
                AppDialog.Info(uiapp, "Import", $"Successfully renamed {updated} assemblies.");
            }
            catch (Exception)
            {
                AppDialog.Info(uiapp, "Import", "Something went wrong while exporting the CSV file.");
            }
            

            return Result.Succeeded;
        }
    }
}
