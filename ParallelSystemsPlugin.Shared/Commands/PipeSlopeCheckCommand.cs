using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin.UI.Dialogs;
using ParallelSystemsPlugin.Configs;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using static ParallelSystemsPlugin.Helpers.Elements;
using System.Windows.Markup;

using ParallelSystemsPlugin.Compatibility;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class PipeSlopeCheckCommand : IExternalCommand
    {
        private static List<double> ALLOWED_ANGLES_DEG = new List<double> { 0.0, 45.0, 90.0 };
        private static double TOL_DEG = 0;
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            ALLOWED_ANGLES_DEG = AppConfig.CurrentConfig.ToolsConfig.PipeSlopeConfig.AllowedAngles;
            TOL_DEG = AppConfig.CurrentConfig.ToolsConfig.PipeSlopeConfig.AcceptedTolerance; 
            
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            var collector = new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType();

            UIApplication uiapp = commandData.Application;

            if (collector.ToElements().Count == 0)
            {
                AppDialog.Info(uiapp, "Elevation Checker", "No pipes found in the active view.");
                return Result.Succeeded;
            }

            int checkedCount = 0;
            List<Element> badPipes = new List<Element>();
            List<string> badInfo = new List<string>();

            foreach (Element elem in collector)
            {
                if (!(elem is Pipe pipe))
                    continue;

                LocationCurve loc = pipe.Location as LocationCurve;
                if (loc?.Curve == null)
                    continue;

                var (dir, angleDeg) = GetDirectionAndAngleDeg(loc.Curve);

                if (angleDeg == null)
                {
                    badPipes.Add(pipe);
                    badInfo.Add($"PipeId: {RevitApiCompatibility.GetElementIdValue(pipe.Id)}, Angle: NULL");
                    continue;
                }

                checkedCount++;

                if (!IsAllowedAngle(angleDeg.Value, ALLOWED_ANGLES_DEG, TOL_DEG))
                {
                    badPipes.Add(pipe);
                    badInfo.Add($"PipeId: {RevitApiCompatibility.GetElementIdValue(pipe.Id)}, Angle: {angleDeg.Value:F4}");
                }
            }

            if (badPipes.Any())
            {
                List<ElementId> ids = badPipes.Select(x => x.Id).ToList();

                using (Transaction t = new Transaction(doc, "Temporary Isolate Invalid Pipes"))
                {
                    t.Start();
                    activeView.IsolateElementsTemporary(ids);
                    t.Commit();
                }

                AppDialog.Warn("Pipe Slope QA",
                    $"Temporary isolation applied.\n\n" +
                    $"Checked Pipes: {checkedCount}\n" +
                    $"Invalid Pipes: {badPipes.Count}\n\n" +
                    $"Use 'Reset Temporary Hide/Isolate' to restore view.");

                return Result.Succeeded;
            }
            else
            {
                string allowedText = FormatAllowedAngles(ALLOWED_ANGLES_DEG);
                AppDialog.Success("Pipe Slope QA",
                    $"No invalid slopes found.\n\n" +
                    $"Checked Pipes: {checkedCount}\n" +
                    $"All pipes are approximately {allowedText}");

                return Result.Succeeded;
            }
        }

        // ---------------------------------------------------
        // Helper: Compute angle relative to horizontal plane
        // ---------------------------------------------------
        private (XYZ direction, double? angleDeg) GetDirectionAndAngleDeg(Curve curve)
        {
            XYZ dirVec = curve.ComputeDerivatives(0.5, true).BasisX;

            double length = dirVec.GetLength();
            if (length < 1e-9)
                return (dirVec, null);

            XYZ dirNorm = new XYZ(
                dirVec.X / length,
                dirVec.Y / length,
                dirVec.Z / length
            );

            double horizMag = Math.Sqrt(dirNorm.X * dirNorm.X + dirNorm.Y * dirNorm.Y);
            double vertMag = Math.Abs(dirNorm.Z);

            double angleDeg;

            if (horizMag < 1e-9 && vertMag > 0)
            {
                angleDeg = 90.0;
            }
            else
            {
                double angleRad = Math.Atan2(vertMag, horizMag);
                angleDeg = Math.Abs(angleRad * 180.0 / Math.PI);
            }

            return (dirNorm, angleDeg);
        }

        private bool IsAllowedAngle(double angleDeg, List<double> allowedList, double tolDeg)
        {
            foreach (double a in allowedList)
            {
                if (Math.Abs(angleDeg - a) <= tolDeg)
                    return true;
            }
            return false;
        }

        private string FormatAllowedAngles(List<double> angles)
        {
            if (angles == null || angles.Count == 0)
                return "no defined angles";

            var ordered = angles.OrderBy(a => a).ToList();

            if (ordered.Count == 1)
                return $"{ordered[0]}°";

            if (ordered.Count == 2)
                return $"{ordered[0]}° or {ordered[1]}°";

            return string.Join(", ", ordered.Take(ordered.Count - 1).Select(a => $"{a}°"))
                   + $", or {ordered.Last()}°";
        }
    }
}

