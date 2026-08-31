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
using ParallelSystemPlugin.UI;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
#if REVIT2025_OR_GREATER
        public static FabricationStepResult Generate(
            UIApplication uiApp,
            FabricationSelection selection,
            string requestedOutputPath)
        {
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            using (BeginFabricationRunCache(doc))
            {
            string finalStepFilePath =
                EnsureStepExtension(requestedOutputPath);

            FabricationStepResult result = new FabricationStepResult();

            List<ElementId> requestedSourceIds =
                selection.SourceElementIds
                    .Where(x => x != null)
                    .Distinct()
                    .ToList();

            List<Element> sourceElements = requestedSourceIds
                .Select(doc.GetElement)
                .Where(IsSupportedSourceElement)
                .ToList();

            result.SourceElementCount = sourceElements.Count;

            if (sourceElements.Count != requestedSourceIds.Count)
            {
                result.Issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    Message =
                        "One or more selected fabrication elements became " +
                        "unavailable or unsupported after selection. " +
                        "Nothing outside the verified selected scope will " +
                        "be substituted automatically. Select the assembly " +
                        "or supported elements again."
                });

                result.Succeeded = false;
                return result;
            }

            HashSet<ElementId> selectedSourceIds =
                new HashSet<ElementId>(
                    sourceElements.Select(x => x.Id));

            List<ElementId> requestedContextIds =
                (selection.CalculationContextElementIds ??
                 new List<ElementId>())
                    .Where(x => x != null)
                    .Concat(requestedSourceIds)
                    .Distinct()
                    .ToList();

            List<Element> calculationElements =
                sourceElements.ToList();

            foreach (ElementId contextId in requestedContextIds
                         .Where(x => !selectedSourceIds.Contains(x)))
            {
                Element contextElement =
                    doc.GetElement(contextId);

                // Header calculation context is intentionally limited to
                // physical Revit pipes. It is read only and never added to the
                // STEP export scope.
                if (!(contextElement is Pipe))
                {
                    result.Issues.Add(new FabricationIssue
                    {
                        Severity =
                            FabricationIssueSeverity.Blocking,
                        Message =
                            "A selected fabrication calculation-context " +
                            "element is unavailable or is not a pipe."
                    });

                    result.Succeeded = false;
                    return result;
                }

                calculationElements.Add(contextElement);
            }

            HashSet<ElementId> calculationContextIds =
                new HashSet<ElementId>(
                    calculationElements.Select(x => x.Id));

            Dictionary<ElementId, ElementId>
                explicitHeaderPipeIdsByBranch =
                    new Dictionary<ElementId, ElementId>();

            foreach (KeyValuePair<ElementId, ElementId> pair in
                     selection.ExplicitHeaderPipeIdsByBranch ??
                     new Dictionary<ElementId, ElementId>())
            {
                if (pair.Key == null ||
                    pair.Value == null ||
                    !selectedSourceIds.Contains(pair.Key) ||
                    !calculationContextIds.Contains(pair.Value))
                {
                    continue;
                }

                explicitHeaderPipeIdsByBranch[pair.Key] =
                    pair.Value;
            }

            Dictionary<ElementId, PipeDimensions> pipeDimensions =
                ResolvePipeDimensions(
                    doc,
                    calculationElements,
                    result.Issues);

            Dictionary<double, PipeDimensions> dimensionsByNominal =
                BuildNominalDimensionMap(
                    pipeDimensions.Values,
                    result.Issues);

            Dictionary<double, PipeDimensions> documentDimensionsByNominal =
                BuildDocumentNominalDimensionMap(
                    doc,
                    sourceElements,
                    pipeDimensions);

            Dictionary<ElementId, ShapedBranchConnection>
                shapedBranchConnections =
                    ResolveShapedBranchConnections(
                        doc,
                        sourceElements,
                        calculationElements,
                        pipeDimensions,
                        selectedSourceIds,
                        calculationContextIds,
                        explicitHeaderPipeIdsByBranch,
                        result.Issues);

            Dictionary<ElementId, List<ShapedBranchConnection>>
                shapedBranchesByHeaderPipe =
                    shapedBranchConnections.Values
                        .Where(x =>
                            x != null &&
                            !x.IsStandaloneComponent &&
                            x.HeaderPipeId != null &&
                            !x.HeaderPipeId.Equals(
                                ElementId.InvalidElementId) &&
                            selectedSourceIds.Contains(
                                x.HeaderPipeId))
                        .GroupBy(x => x.HeaderPipeId)
                        .ToDictionary(
                            x => x.Key,
                            x => x.ToList());

            Dictionary<ElementId, SideCouplingConnection>
                sideCouplingConnections =
                    ResolveSideCouplingConnections(
                        doc,
                        sourceElements,
                        pipeDimensions,
                        selectedSourceIds,
                        result.Issues);

            Dictionary<ElementId, List<SideCouplingConnection>>
                sideCouplingsByHeaderPipe =
                    sideCouplingConnections.Values
                        .GroupBy(x => x.HeaderPipeId)
                        .ToDictionary(
                            x => x.Key,
                            x => x.ToList());

            // Special side-outlet fittings can provide the authoritative
            // branch/outlet dimensions to adjacent flanges and fittings even
            // when no physical branch pipe exists in the selected assembly.
            Dictionary<ElementId, PipeDimensions>
                componentDimensionOverrides =
                    new Dictionary<ElementId, PipeDimensions>();

            foreach (ShapedBranchConnection connection in
                     shapedBranchConnections.Values)
            {
                if (connection?.BranchDimensions != null)
                {
                    componentDimensionOverrides[
                        connection.FittingId] =
                        connection.BranchDimensions;
                }
            }

            foreach (SideCouplingConnection connection in
                     sideCouplingConnections.Values)
            {
                if (connection?.OutletDimensions != null)
                {
                    componentDimensionOverrides[
                        connection.FittingId] =
                        connection.OutletDimensions;
                }
            }

            List<FabricationElementGeometry> generated =
                new List<FabricationElementGeometry>();

            foreach (Element element in sourceElements)
            {
                if (element is Pipe pipe)
                {
                    FabricationElementGeometry pipeGeometry =
                        BuildPipeGeometry(
                            doc,
                            pipe,
                            pipeDimensions,
                            selectedSourceIds,
                            shapedBranchesByHeaderPipe,
                            shapedBranchConnections,
                            sideCouplingsByHeaderPipe,
                            sideCouplingConnections,
                            result.Issues);

                    if (pipeGeometry != null)
                        generated.Add(pipeGeometry);

                    continue;
                }

                ShapedBranchConnection shapedBranchConnection = null;

                shapedBranchConnections.TryGetValue(
                    element.Id,
                    out shapedBranchConnection);

                SideCouplingConnection sideCouplingConnection = null;

                sideCouplingConnections.TryGetValue(
                    element.Id,
                    out sideCouplingConnection);

                // Never let a special side-outlet fitting fall through to the
                // generic elbow/fitting rules. Its resolver has already added
                // a blocking issue explaining why it could not be classified.
                if ((IsShapedBranchLike(doc, element) &&
                     shapedBranchConnection == null) ||
                    (IsSideCouplingLike(doc, element) &&
                     sideCouplingConnection == null))
                {
                    continue;
                }

                FabricationElementGeometry fittingGeometry =
                    BuildFittingGeometry(
                        doc,
                        element,
                        pipeDimensions,
                        dimensionsByNominal,
                        documentDimensionsByNominal,
                        componentDimensionOverrides,
                        selectedSourceIds,
                        shapedBranchConnection,
                        sideCouplingConnection,
                        result.Issues);

                if (fittingGeometry != null)
                    generated.Add(fittingGeometry);
            }

            if (result.Issues.Any(x =>
                    x.Severity == FabricationIssueSeverity.Blocking))
            {

                result.Succeeded = false;
                return result;
            }

            if (generated.Count == 0)
            {
                result.Issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    Message = "No fabrication geometry was generated."
                });


                result.Succeeded = false;
                return result;
            }

            string baseName = Path.GetFileNameWithoutExtension(
                finalStepFilePath);

            string viewName = BuildUniqueViewName(
                doc,
                "PS FAB STEP - " + baseName);

            View3D fabricationView = null;
            List<ElementId> generatedIds = new List<ElementId>();

            // Build the fabrication model inside a transaction group.
            //
            // Failure/cancellation:
            // - roll back the group so the project remains unchanged.
            //
            // Successful verified export:
            // - assimilate the group;
            // - retain only the dedicated fabrication inspection view and
            //   generated DirectShapes;
            // - activate that view after all transactions are closed.
            //
            // The code deliberately does not hide the DirectShapes in every
            // other project view. Doing that in a workshared model attempts to
            // borrow thousands of view worksets.
            using (TransactionGroup temporaryGroup =
                   new TransactionGroup(
                       doc,
                       "Fabrication STEP Inspection Model"))
            {
                temporaryGroup.Start();

                try
                {
                    using (Transaction transaction =
                           new Transaction(
                               doc,
                               "Build Temporary Fabrication STEP Model"))
                    {
                        transaction.Start();

                        fabricationView =
                            CreateFabricationView(doc, viewName);

                        foreach (FabricationElementGeometry item in generated)
                        {
                            try
                            {
                                DirectShape directShape =
                                    DirectShape.CreateElement(
                                        doc,
                                        new ElementId(
                                            BuiltInCategory
                                                .OST_GenericModel));

                                directShape.ApplicationId = ApplicationId;
                                directShape.ApplicationDataId =
                                    baseName + "|" + item.SourceUniqueId;

                                directShape.SetShape(item.Geometry);
                                SetDirectShapeMetadata(
                                    directShape,
                                    item);

                                generatedIds.Add(directShape.Id);
                            }
                            catch (Exception ex)
                            {
                                result.Issues.Add(
                                    new FabricationIssue
                                    {
                                        Severity =
                                            FabricationIssueSeverity
                                                .Blocking,
                                        ElementId =
                                            item.SourceElementId,
                                        ElementName = item.SourceName,
                                        Message =
                                            "Revit could not create the " +
                                            "temporary fabrication " +
                                            "DirectShape: " + ex.Message
                                    });
                            }
                        }

                        if (result.Issues.Any(x =>
                                x.Severity ==
                                FabricationIssueSeverity.Blocking))
                        {
                            transaction.RollBack();
                            result.GeneratedElementCount = 0;
                            result.Succeeded = false;


                            return result;
                        }

                        ConfigureFabricationView(
                            doc,
                            fabricationView,
                            generatedIds);

                        transaction.Commit();
                    }

                    // The view name is assigned only after the transaction
                    // group is successfully assimilated. Until then the view
                    // is still temporary and may be rolled back.
                    result.FabricationViewName = null;
                    result.GeneratedElementCount = generatedIds.Count;

                    string outputDirectory = Path.GetDirectoryName(
                        finalStepFilePath);

                    if (string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        result.Issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Blocking,
                            Message =
                                "The temporary STEP output directory " +
                                "could not be resolved."
                        });

                        result.Succeeded = false;
                        return result;
                    }

                    Directory.CreateDirectory(outputDirectory);

                    // Revit exports to a private staging folder first. The
                    // requested destination is not touched until a non-empty
                    // STEP file has been generated and verified.
                    string stagingDirectory =
                        CreateTemporaryExportDirectory();

                    try
                    {
                        DateTime exportStartedUtc = DateTime.UtcNow;
                        bool exportSucceeded;
                        string exportError;

                        if (!TryExportStepWithoutBlockingForBackgroundCalculations(
                                doc,
                                fabricationView.Id,
                                stagingDirectory,
                                baseName,
                                out exportSucceeded,
                                out exportError))
                        {
                            result.Issues.Add(
                                new FabricationIssue
                                {
                                    Severity =
                                        FabricationIssueSeverity
                                            .Blocking,
                                    Message =
                                        exportError +
                                        " The temporary fabrication " +
                                        "model was discarded."
                                });

                            result.Succeeded = false;
                            return result;
                        }

                        string stagedRequestedPath = Path.Combine(
                            stagingDirectory,
                            baseName + ".step");

                        string actualStepPath =
                            FindExportedStepFile(
                                stagingDirectory,
                                baseName,
                                stagedRequestedPath,
                                exportStartedUtc);

                        if (!exportSucceeded ||
                            string.IsNullOrWhiteSpace(actualStepPath) ||
                            !File.Exists(actualStepPath) ||
                            new FileInfo(actualStepPath).Length == 0)
                        {
                            result.Issues.Add(
                                new FabricationIssue
                                {
                                    Severity =
                                        FabricationIssueSeverity
                                            .Blocking,
                                    Message =
                                        "Revit did not create a valid " +
                                        "STEP file. The temporary " +
                                        "fabrication model was discarded."
                                });

                            result.Succeeded = false;

                            return result;
                        }

                        int maximumExpectedCompactSetOnFaceCount =
                            generated.Count == 1 &&
                            generated[0].RequiresCompactSetOnTopology
                                ? generated[0].MaximumExpectedStepFaceCount
                                : 0;

                        FabricationStepTopologySummary topologySummary;
                        string topologyError;

                        if (!TryValidateStagedStepTopology(
                                actualStepPath,
                                maximumExpectedCompactSetOnFaceCount,
                                out topologySummary,
                                out topologyError))
                        {
                            result.Issues.Add(
                                new FabricationIssue
                                {
                                    Severity =
                                        FabricationIssueSeverity
                                            .Blocking,
                                    Message =
                                        topologyError +
                                        " The staged STEP was discarded."
                                });

                            result.Succeeded = false;
                            return result;
                        }

                        result.Issues.Add(
                            new FabricationIssue
                            {
                                Severity =
                                    FabricationIssueSeverity
                                        .Information,
                                Message =
                                    topologySummary.BuildMessage()
                            });

                        try
                        {
                            CommitVerifiedStepFile(
                                actualStepPath,
                                finalStepFilePath);
                        }
                        catch (Exception ex)
                        {
                            result.Issues.Add(
                                new FabricationIssue
                                {
                                    Severity =
                                        FabricationIssueSeverity
                                            .Blocking,
                                    Message =
                                        "The STEP geometry was " +
                                        "generated, but the verified " +
                                        "file could not be saved to the " +
                                        "temporary command location: " +
                                        ex.Message
                                });

                            result.Succeeded = false;

                            return result;
                        }
                    }
                    finally
                    {
                        TryDeleteDirectory(stagingDirectory);
                    }

                    result.StepFilePath = finalStepFilePath;
                    result.Succeeded = true;


                    bool inspectionViewRetained = false;

                    try
                    {
                        TransactionStatus assimilatedStatus =
                            temporaryGroup.Assimilate();

                        inspectionViewRetained =
                            assimilatedStatus ==
                            TransactionStatus.Committed;
                    }
                    catch (Exception ex)
                    {
                        // The STEP file is already generated and verified.
                        // Failure to retain the optional Revit inspection view
                        // must not turn a valid file export into a failed run.
                        result.Issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Warning,
                            Message =
                                "The STEP file was generated successfully, " +
                                "but Revit could not retain the inspection " +
                                "view: " + ex.Message
                        });
                    }

                    if (inspectionViewRetained &&
                        fabricationView != null &&
                        fabricationView.IsValidObject)
                    {
                        result.FabricationViewName =
                            fabricationView.Name;

                        try
                        {
                            // This must run after Assimilate(), when no
                            // transaction or transaction group remains open.
                            uiDoc.ActiveView = fabricationView;
                            uiDoc.RefreshActiveView();

                            FrameFabricationInspectionView(
                                uiDoc,
                                fabricationView,
                                generatedIds);
                        }
                        catch (Exception ex)
                        {
                            result.Issues.Add(new FabricationIssue
                            {
                                Severity =
                                    FabricationIssueSeverity.Warning,
                                Message =
                                    "The Revit inspection view was created " +
                                    "and retained, but it could not be " +
                                    "opened and framed automatically: " +
                                    ex.Message
                            });
                        }
                    }

                    return result;
                }
                finally
                {
                    if (temporaryGroup.GetStatus() ==
                        TransactionStatus.Started)
                    {
                        temporaryGroup.RollBack();
                    }
                }
            }
            }
        }
#endif

        private static void ConfigureFabricationView(
            Document doc,
            View3D view,
            IList<ElementId> generatedIds)
        {
            if (doc == null ||
                view == null ||
                generatedIds == null ||
                generatedIds.Count == 0)
            {
                return;
            }

            HashSet<ElementId> generatedIdSet =
                new HashSet<ElementId>(generatedIds);

            // DirectShape geometry and bounding boxes were created earlier in
            // the same transaction. Regenerate once before reading those
            // bounds so the section box is based on the finished solids.
            doc.Regenerate();

            // Build the section box from the generated fabrication
            // DirectShapes, not the source family bounding boxes. Some source
            // families contain symbolic/reference geometry with oversized
            // bounds, which caused the inspection view to open extremely
            // zoomed out.
            BoundingBoxXYZ sectionBox =
                BuildSectionBox(
                    generatedIds
                        .Select(doc.GetElement)
                        .Where(x => x != null));

            if (sectionBox != null)
            {
                view.IsSectionBoxActive = true;
                view.SetSectionBox(sectionBox);

                // Apply the section box before using a view-scoped collector.
                doc.Regenerate();
            }

            ElementId genericModelCategoryId =
                new ElementId(BuiltInCategory.OST_GenericModel);

            foreach (Category category in doc.Settings.Categories)
            {
                if (category == null ||
                    category.CategoryType != CategoryType.Model ||
                    category.Id.Equals(genericModelCategoryId))
                {
                    continue;
                }

                try
                {
                    if (view.CanCategoryBeHidden(category.Id))
                        view.SetCategoryHidden(category.Id, true);
                }
                catch
                {
                    // Some system categories cannot be changed in all views.
                }
            }

            // The section box is already active, so this view-scoped collector
            // evaluates only nearby Generic Models instead of every Generic
            // Model in a large project. Only the DirectShapes created for the
            // current selected assembly remain visible and eligible for STEP
            // export.
            List<ElementId> existingGenericModels =
                new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Where(x => !generatedIdSet.Contains(x))
                    .Where(x =>
                    {
                        Element element = doc.GetElement(x);
                        return element != null &&
                               element.CanBeHidden(view);
                    })
                    .ToList();

            if (existingGenericModels.Count > 0)
                view.HideElements(existingGenericModels);
        }

        private static void FrameFabricationInspectionView(
            UIDocument uiDoc,
            View3D fabricationView,
            IList<ElementId> generatedIds)
        {
            if (uiDoc == null ||
                fabricationView == null ||
                generatedIds == null ||
                generatedIds.Count == 0)
            {
                return;
            }

            // ShowElements frames exactly the generated DirectShapes. It does
            // not add connected/model-wide elements to the export.
            uiDoc.ShowElements(generatedIds);
            uiDoc.RefreshActiveView();

            UIView uiView = uiDoc
                .GetOpenUIViews()
                .FirstOrDefault(x =>
                    x.ViewId.Equals(fabricationView.Id));

            uiView?.ZoomToFit();
        }

        private static void SetDirectShapeMetadata(
            DirectShape directShape,
            FabricationElementGeometry geometry)
        {
            Parameter comments = directShape.get_Parameter(
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);

            if (comments != null &&
                !comments.IsReadOnly &&
                comments.StorageType == StorageType.String)
            {
                comments.Set(
                    "Fabrication STEP source: " +
                    geometry.SourceName +
                    " | Source ElementId: " +
                    RevitApiCompatibility.GetElementIdValue(
                        geometry.SourceElementId).ToString(
                            CultureInfo.InvariantCulture));
            }

            Parameter mark = directShape.get_Parameter(
                BuiltInParameter.ALL_MODEL_MARK);

            if (mark != null &&
                !mark.IsReadOnly &&
                mark.StorageType == StorageType.String)
            {
                mark.Set(
                    RevitApiCompatibility.GetElementIdValue(
                        geometry.SourceElementId).ToString(
                            CultureInfo.InvariantCulture));
            }
        }

        private static string CreateTemporaryExportDirectory()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "ParallelSystemsPlugin",
                "FabricationStep",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void CommitVerifiedStepFile(
            string stagedStepPath,
            string finalStepPath)
        {
            if (string.IsNullOrWhiteSpace(stagedStepPath) ||
                !File.Exists(stagedStepPath) ||
                new FileInfo(stagedStepPath).Length == 0)
            {
                throw new InvalidOperationException(
                    "The staged STEP file is missing or empty.");
            }

            string destinationDirectory =
                Path.GetDirectoryName(finalStepPath);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new InvalidOperationException(
                    "The selected STEP destination is invalid.");
            }

            Directory.CreateDirectory(destinationDirectory);

            string pendingPath = Path.Combine(
                destinationDirectory,
                "." + Path.GetFileName(finalStepPath) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.Copy(stagedStepPath, pendingPath, true);

                if (!File.Exists(pendingPath) ||
                    new FileInfo(pendingPath).Length == 0)
                {
                    throw new IOException(
                        "The verified STEP file could not be staged in the selected folder.");
                }

                if (File.Exists(finalStepPath))
                {
                    // The replacement occurs only after the complete file is in
                    // the destination folder, so a failed generation never
                    // destroys the user's existing STEP file.
                    File.Replace(
                        pendingPath,
                        finalStepPath,
                        null,
                        true);
                }
                else
                {
                    File.Move(pendingPath, finalStepPath);
                }

                if (!File.Exists(finalStepPath) ||
                    new FileInfo(finalStepPath).Length == 0)
                {
                    throw new IOException(
                        "The final STEP file is missing or empty after saving.");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(pendingPath))
                        File.Delete(pendingPath);
                }
                catch
                {
                    // Best-effort cleanup only. The save result is determined by
                    // the verified final file, not by temporary-file cleanup.
                }
            }
        }

        private static void TryDeleteDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch
            {
                // Temporary export cleanup must not replace the real result.
            }
        }

#if REVIT2025_OR_GREATER
        private static bool
            TryExportStepWithoutBlockingForBackgroundCalculations(
                Document doc,
                ElementId viewId,
                string outputDirectory,
                string baseName,
                out bool exportSucceeded,
                out string error)
        {
            exportSucceeded = false;
            error = null;

            if (doc == null ||
                viewId == null ||
                viewId.Equals(ElementId.InvalidElementId))
            {
                error =
                    "The STEP export view is unavailable.";

                return false;
            }

            /*
             * Do not wait for RevitWorker.exe here.
             *
             * RevitWorker is shared by several background operations and can
             * remain alive after the calculation that launched it has ended.
             * Polling for the process from an external command blocked Revit's
             * UI thread for as long as five minutes and made Fabrication STEP
             * appear frozen even when no relevant calculation was running.
             *
             * The supported behavior is to attempt the export once. If Revit
             * reports an active MEP/background calculation, return control to
             * the user immediately so Revit can continue processing normally.
             */
            try
            {
                using (STEPExportOptions options =
                       new STEPExportOptions())
                {
                    options.ViewId = viewId;
                    options.TargetUnit =
                        ExportUnit.Millimeter;

                    exportSucceeded = doc.Export(
                        outputDirectory,
                        baseName,
                        options);
                }

                if (exportSucceeded)
                    return true;

                error =
                    "Revit did not start the STEP export. If the Background " +
                    "Processes panel shows Network Calculation, System " +
                    "Volumes, Color Fills, or another calculation, allow it " +
                    "to finish and run Fabrication STEP again.";

                return false;
            }
            catch (Exception ex)
            {
                if (IsBackgroundCalculationExportException(ex))
                {
                    error =
                        "Revit is still completing a background calculation, " +
                        "so STEP export was not started. Check the Background " +
                        "Processes panel, wait until it is empty, and run " +
                        "Fabrication STEP again. Revit was not placed in a " +
                        "blocking wait loop. Revit message: " +
                        ex.Message;

                    return false;
                }

                error =
                    "Revit could not generate the STEP file: " +
                    ex.Message;

                return false;
            }
        }
#endif

        private static bool
            IsBackgroundCalculationExportException(
                Exception exception)
        {
            if (exception == null)
                return false;

            string message =
                (exception.Message ?? string.Empty)
                    .ToUpperInvariant();

            return
                message.Contains("NETWORK CALCULATION") ||
                message.Contains("NETWORK CALCULATIONS") ||
                message.Contains("SYSTEM VOLUMES") ||
                message.Contains("COLOR FILLS") ||
                message.Contains("BACKGROUND PROCESS") ||
                message.Contains("CALCULATION IN PROGRESS") ||
                (message.Contains("CALCULATION") &&
                 message.Contains("CALCULATING"));
        }

        private static string FindExportedStepFile(
            string outputDirectory,
            string baseName,
            string requestedPath,
            DateTime exportStartedUtc)
        {
            DateTime minimumTimestamp = exportStartedUtc.AddSeconds(-2);

            IEnumerable<string> candidates = new[]
                {
                    requestedPath,
                    Path.Combine(outputDirectory, baseName + ".step"),
                    Path.Combine(outputDirectory, baseName + ".stp")
                }
                .Concat(Directory.GetFiles(
                    outputDirectory,
                    baseName + "*.step"))
                .Concat(Directory.GetFiles(
                    outputDirectory,
                    baseName + "*.stp"))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x =>
                    File.GetLastWriteTimeUtc(x) >= minimumTimestamp)
                .OrderByDescending(File.GetLastWriteTimeUtc);

            return candidates.FirstOrDefault();
        }

        private static string BuildUniqueViewName(
            Document doc,
            string preferredName)
        {
            HashSet<string> names = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Select(x => x.Name)
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            if (!names.Contains(preferredName))
                return preferredName;

            for (int index = 2; index < 1000; index++)
            {
                string candidate = preferredName + " (" +
                    index.ToString(CultureInfo.InvariantCulture) + ")";

                if (!names.Contains(candidate))
                    return candidate;
            }

            return preferredName + " - " +
                DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture);
        }

        private static string EnsureStepExtension(string path)
        {
            if (string.Equals(
                    Path.GetExtension(path),
                    ".step",
                    StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return Path.ChangeExtension(path, ".step");
        }

    }
}
