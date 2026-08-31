using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ParallelSystemPlugin.UI;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        public static int ShowFabricationStatus(
            UIApplication uiApp,
            bool showReady,
            out string statusViewName)
        {
            statusViewName = string.Empty;

            UIDocument uiDoc = uiApp?.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            View activeView = uiDoc?.ActiveView;

            if (doc == null)
            {
                throw new InvalidOperationException(
                    "No active Revit project is open.");
            }

            if (doc.IsFamilyDocument)
            {
                throw new InvalidOperationException(
                    "Fabrication status can only be shown in a Revit project.");
            }

            if (activeView == null || activeView.IsTemplate)
            {
                throw new InvalidOperationException(
                    "Open a graphical model view before using Show Ready.");
            }

            if (activeView is ViewSheet || activeView is ViewSchedule)
            {
                throw new InvalidOperationException(
                    "Show Ready must be run from a graphical model view, such as a 3D, plan, section, or elevation view.");
            }

            HashSet<string> processedSourceUniqueIds =
                GetProcessedSourceUniqueIds(doc);

            List<Element> sourceElements;

            try
            {
                // Restrict the status check to elements that belong to the
                // current working view. This keeps the command useful on large
                // projects and avoids switching the user to a generated view.
                ElementMulticategoryFilter pipingCategoryFilter =
                    new ElementMulticategoryFilter(
                        new[]
                        {
                            BuiltInCategory.OST_PipeCurves,
                            BuiltInCategory.OST_PipeFitting,
                            BuiltInCategory.OST_PipeAccessory
                        });

                sourceElements =
                    new FilteredElementCollector(doc, activeView.Id)
                        .WhereElementIsNotElementType()
                        .WherePasses(pipingCategoryFilter)
                        .ToElements()
                        .Where(IsSupportedSourceElement)
                        .ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The active view does not support element isolation. Open a graphical model view and try again.",
                    ex);
            }

            List<ElementId> targetIds = sourceElements
                .Where(x =>
                {
                    bool processed =
                        processedSourceUniqueIds.Contains(
                            x.UniqueId ?? string.Empty);

                    return showReady
                        ? processed
                        : !processed;
                })
                .Select(x => x.Id)
                .Distinct()
                .ToList();

            statusViewName = activeView.Name;

            if (targetIds.Count == 0)
                return 0;

            using (Transaction transaction =
                   new Transaction(
                       doc,
                       showReady
                           ? "Show Fabrication Ready Components"
                           : "Show Unprocessed Fabrication Components"))
            {
                transaction.Start();

                // Replace an existing temporary isolate in the current view.
                // The user can restore the view with Revit's built-in
                // Reset Temporary Hide/Isolate command.
                if (activeView.IsTemporaryHideIsolateActive())
                {
                    activeView.DisableTemporaryViewMode(
                        TemporaryViewMode.TemporaryHideIsolate);
                }

                activeView.IsolateElementsTemporary(targetIds);

                transaction.Commit();
            }

            return targetIds.Count;
        }

        public static bool ResetFabricationStatusIsolation(
            UIApplication uiApp)
        {
            UIDocument uiDoc = uiApp?.ActiveUIDocument;
            Document doc = uiDoc?.Document;
            View activeView = uiDoc?.ActiveView;

            if (doc == null || activeView == null)
                return false;

            if (!activeView.IsTemporaryHideIsolateActive())
                return false;

            using (Transaction transaction =
                   new Transaction(
                       doc,
                       "Reset Fabrication Status Isolation"))
            {
                transaction.Start();

                activeView.DisableTemporaryViewMode(
                    TemporaryViewMode.TemporaryHideIsolate);

                transaction.Commit();
            }

            return true;
        }

        private static HashSet<string> GetProcessedSourceUniqueIds(
            Document doc)
        {
            // Successful exports are tracked outside the Revit model so
            // Show Ready never needs to edit source elements. Retained
            // inspection DirectShapes and older DirectShape markers are also
            // read for compatibility.
            HashSet<string> result =
                FabricationProcessedRegistry.Read(doc);

            foreach (DirectShape directShape in
                     new FilteredElementCollector(doc)
                         .OfClass(typeof(DirectShape))
                         .Cast<DirectShape>())
            {
                if (!string.Equals(
                        directShape.ApplicationId,
                        ApplicationId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string applicationDataId =
                    directShape.ApplicationDataId;

                if (string.IsNullOrWhiteSpace(applicationDataId))
                    continue;

                int separator =
                    applicationDataId.LastIndexOf('|');

                string sourceUniqueId =
                    separator >= 0 &&
                    separator < applicationDataId.Length - 1
                        ? applicationDataId.Substring(
                            separator + 1)
                        : applicationDataId;

                if (!string.IsNullOrWhiteSpace(sourceUniqueId))
                    result.Add(sourceUniqueId);
            }

            return result;
        }

        private static View3D GetOrCreateStatusView(
            Document doc,
            string viewName)
        {
            View3D existing =
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(x =>
                        !x.IsTemplate &&
                        string.Equals(
                            x.Name,
                            viewName,
                            StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing;

            return CreateFabricationView(
                doc,
                viewName);
        }

        private static View3D CreateFabricationView(
            Document doc,
            string viewName)
        {
            ViewFamilyType viewFamilyType =
                new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(x =>
                        x.ViewFamily == ViewFamily.ThreeDimensional);

            if (viewFamilyType == null)
            {
                throw new InvalidOperationException(
                    "No 3D ViewFamilyType is available in this project.");
            }

            View3D view = View3D.CreateIsometric(
                doc,
                viewFamilyType.Id);

            view.Name = viewName;
            view.DetailLevel = ViewDetailLevel.Fine;
            view.DisplayStyle = DisplayStyle.Shading;

            return view;
        }

        private static BoundingBoxXYZ BuildSectionBox(
            IEnumerable<Element> elements)
        {
            XYZ minimum = null;
            XYZ maximum = null;

            foreach (Element element in
                     elements ?? Enumerable.Empty<Element>())
            {
                BoundingBoxXYZ box = null;

                try
                {
                    box = GetElementBoundingBoxCached(element);
                }
                catch
                {
                    // Skip invalid elements and continue with the remaining
                    // generated/source geometry.
                }

                if (box == null)
                    continue;

                foreach (XYZ corner in
                         GetBoundingBoxWorldCorners(box))
                {
                    minimum = minimum == null
                        ? corner
                        : new XYZ(
                            Math.Min(minimum.X, corner.X),
                            Math.Min(minimum.Y, corner.Y),
                            Math.Min(minimum.Z, corner.Z));

                    maximum = maximum == null
                        ? corner
                        : new XYZ(
                            Math.Max(maximum.X, corner.X),
                            Math.Max(maximum.Y, corner.Y),
                            Math.Max(maximum.Z, corner.Z));
                }
            }

            if (minimum == null || maximum == null)
                return null;

            double padding = 150.0 / FeetToMillimetres;
            XYZ offset = new XYZ(padding, padding, padding);

            return new BoundingBoxXYZ
            {
                Min = minimum - offset,
                Max = maximum + offset
            };
        }

        private static IEnumerable<XYZ>
            GetBoundingBoxWorldCorners(
                BoundingBoxXYZ box)
        {
            if (box == null)
                yield break;

            Transform transform =
                box.Transform ?? Transform.Identity;

            double[] xValues = { box.Min.X, box.Max.X };
            double[] yValues = { box.Min.Y, box.Max.Y };
            double[] zValues = { box.Min.Z, box.Max.Z };

            foreach (double x in xValues)
            {
                foreach (double y in yValues)
                {
                    foreach (double z in zValues)
                    {
                        yield return transform.OfPoint(
                            new XYZ(x, y, z));
                    }
                }
            }
        }

    }
}
