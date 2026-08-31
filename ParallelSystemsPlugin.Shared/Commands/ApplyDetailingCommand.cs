using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ParallelSystemPlugin.UI;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ApplyDetailingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            ViewSheet sheet = doc.ActiveView as ViewSheet;
            if (sheet == null)
            {
                AppDialog.Error("Error", "Open a sheet view.");
                return Result.Failed;
            }

            var viewports = new FilteredElementCollector(doc, sheet.Id)
             .OfClass(typeof(Viewport))
             .Cast<Viewport>()
             .Where(vp =>
             {
                 View v = doc.GetElement(vp.ViewId) as View;
                 return v != null &&
                        v.ViewType != ViewType.Legend;
             })
             .Reverse()
             .ToList()
             ;

            foreach (var vp in viewports)
            {
                View v = doc.GetElement(vp.ViewId) as View;

                Debug.WriteLine("Debug",
                    $"Name: {v.Name}\nType: {v.ViewType}\nId: {vp.Id}");
            }

            if (viewports.Count != 4)
            {
                AppDialog.Error("Error", "This tool expects exactly 4 views.");
                return Result.Failed;
            }

            // Sheet size
            BoundingBoxUV sheetBox = sheet.Outline;
            double sheetWidth = sheetBox.Max.U - sheetBox.Min.U;
            double sheetHeight = sheetBox.Max.V - sheetBox.Min.V;

            // Spacing
            double margin = 0.2;

            // Target grid positions (2x2)
            double cellWidth = sheetWidth / 2;
            double cellHeight = sheetHeight / 2;

            List<int> scales = new List<int> { 10, 20, 25, 50, 75, 100 };

            using (Transaction t = new Transaction(doc, "Fit & Arrange Views"))
            {
                t.Start();

                foreach (int scale in scales)
                {
                    // Apply scale
                    foreach (var vp in viewports)
                    {
                        View v = doc.GetElement(vp.ViewId) as View;
                        if (v != null && !v.IsTemplate)
                            v.Scale = scale;
                    }

                    doc.Regenerate();

                    // Try positioning in 2x2 grid
                    for (int i = 0; i < viewports.Count; i++)
                    {
                        int row = i / 2;
                        int col = i % 2;

                        double x = sheetBox.Min.U + (col + 0.5) * cellWidth;
                        double y = sheetBox.Min.V + (1.5 - row) * cellHeight;

                        XYZ newCenter = new XYZ(x, y, 0);
                        viewports[i].SetBoxCenter(newCenter);
                    }

                    doc.Regenerate();

                    // Check if all fit inside cells
                    bool fits = true;

                    foreach (var vp in viewports)
                    {
                        BoundingBoxXYZ bb = vp.get_BoundingBox(sheet);

                        double width = bb.Max.X - bb.Min.X;
                        double height = bb.Max.Y - bb.Min.Y;

                        if (width > cellWidth - margin || height > cellHeight - margin)
                        {
                            fits = false;
                            break;
                        }
                    }

                    if (fits)
                    {
                        AppDialog.Success("Success", $"Arranged at scale 1:{scale}");
                        t.Commit();
                        return Result.Succeeded;
                    }
                }

                t.Commit();
            }

            AppDialog.Warn("Result", "Could not fit views properly.");
            return Result.Succeeded;
        }
    }
}
