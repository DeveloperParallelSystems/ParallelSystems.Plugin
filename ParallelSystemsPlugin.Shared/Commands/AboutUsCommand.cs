using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ParallelSystemPlugin.Commands
{
    // No DB changes; Manual is fine
    [Transaction(TransactionMode.Manual)]
    public class AboutUsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var psi = new ProcessStartInfo("https://www.parallelsystems.com.au/") { UseShellExecute = true };
                Process.Start(psi);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
