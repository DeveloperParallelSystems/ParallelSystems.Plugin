using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.UI.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Markup;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RenameExportCommand : IExternalCommand
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
                AppDialog.Info(uiapp, "Export", "The CSV path is not defined in the configuration.");
                return Result.Succeeded;
            }

            if (File.Exists(path))
            {
                if(!AppDialog.Confirm(uiapp, "Export", "The CSV file already exists. Do you want to replace it and proceed with the export?"))
                {
                    return Result.Succeeded;//
                }
            }

            try
            {
                AssemblyCSVHelpers.Export(doc, path);
                AppDialog.Info(uiapp, "Export", "The CSV file was successfully exported.");
            }
            catch (Exception)
            {
                AppDialog.Info(uiapp, "Export", "Something went wrong while exporting the CSV file.");
            }

            return Result.Succeeded;
        }
    }
}
