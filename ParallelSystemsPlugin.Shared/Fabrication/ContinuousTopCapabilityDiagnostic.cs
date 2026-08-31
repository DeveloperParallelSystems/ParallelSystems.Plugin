using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.Fabrication
{
#if REVIT2025_OR_GREATER
    internal sealed class ContinuousTopCapabilityDiagnosticSession : IDisposable
    {
        private bool _disposed;

        internal ContinuousTopCapabilityDiagnosticSession(
            UIApplication uiApp,
            Document document,
            FabricationSelection selection,
            string reportPath)
        {
            UiApp = uiApp;
            Document = document;
            Selection = selection;
            ReportPath = reportPath;
        }

        internal UIApplication UiApp { get; }
        internal Document Document { get; }
        internal FabricationSelection Selection { get; }

        public string ReportPath { get; }
        public bool Completed { get; internal set; }
        public string Decision { get; internal set; }
        public string Summary { get; internal set; }

        internal void Complete(
            string report,
            string decision,
            string summary)
        {
            if (Completed)
                return;

            string directory = Path.GetDirectoryName(ReportPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                ReportPath,
                report ?? string.Empty,
                new UTF8Encoding(false));

            Decision = decision;
            Summary = summary;
            Completed = true;
        }

        internal void CompleteFromGenerationFailure(
            FabricationStepResult result)
        {
            if (Completed)
                return;

            StringBuilder report = new StringBuilder();
            report.AppendLine("Parallel Systems Continuous-Top Capability Diagnostic");
            report.AppendLine(new string('=', 68));
            report.AppendLine();
            report.AppendLine("RESULT: DIAGNOSTIC DID NOT REACH THE SADDLE-RING TEST HOOK");
            report.AppendLine();
            report.AppendLine(
                "The selected component could not be resolved far enough to run the isolated BRep tests. " +
                "Correct the selection/header/dimension problem before using this report to judge Revit's topology capability.");
            report.AppendLine();
            report.AppendLine("Generation diagnostics:");
            report.AppendLine(result == null
                ? "No FabricationStepResult was returned."
                : result.BuildDetailedMessage());

            Complete(
                report.ToString(),
                "INCONCLUSIVE",
                "The isolated topology tests did not run. Review the report for the earlier selection or geometry-resolution failure.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            FabricationStepService.EndContinuousTopCapabilityDiagnostic(this);
        }
    }
#endif

    internal static partial class FabricationStepService
    {
#if REVIT2025_OR_GREATER
        private static ContinuousTopCapabilityDiagnosticSession
            _activeContinuousTopCapabilityDiagnostic;

        public static ContinuousTopCapabilityDiagnosticSession
            BeginContinuousTopCapabilityDiagnostic(
                UIApplication uiApp,
                Document document,
                FabricationSelection selection,
                string reportPath)
        {
            if (uiApp == null)
                throw new ArgumentNullException(nameof(uiApp));

            if (document == null)
                throw new ArgumentNullException(nameof(document));

            if (selection == null)
                throw new ArgumentNullException(nameof(selection));

            if (string.IsNullOrWhiteSpace(reportPath))
                throw new ArgumentException(
                    "A diagnostic report path is required.",
                    nameof(reportPath));

            if (_activeContinuousTopCapabilityDiagnostic != null)
            {
                throw new InvalidOperationException(
                    "A continuous-top capability diagnostic is already running.");
            }

            ContinuousTopCapabilityDiagnosticSession session =
                new ContinuousTopCapabilityDiagnosticSession(
                    uiApp,
                    document,
                    selection,
                    reportPath);

            _activeContinuousTopCapabilityDiagnostic = session;
            return session;
        }

        internal static void EndContinuousTopCapabilityDiagnostic(
            ContinuousTopCapabilityDiagnosticSession session)
        {
            if (ReferenceEquals(
                    _activeContinuousTopCapabilityDiagnostic,
                    session))
            {
                _activeContinuousTopCapabilityDiagnostic = null;
            }
        }

        public static bool ValidateContinuousTopDiagnosticSelection(
            Document document,
            FabricationSelection selection,
            out string error)
        {
            error = null;

            if (document == null || selection == null)
            {
                error = "No Revit project or fabrication selection is available.";
                return false;
            }

            List<Element> shapedBranches =
                (selection.SourceElementIds ?? new List<ElementId>())
                    .Where(id => id != null)
                    .Select(document.GetElement)
                    .Where(element =>
                        element != null &&
                        IsShapedBranchLike(document, element))
                    .ToList();

            if (shapedBranches.Count != 1)
            {
                error =
                    "Select exactly one shaped branch for the continuous-top capability test. " +
                    "The header pipe may also be selected or picked as read-only calculation context.";

                return false;
            }

            return true;
        }

        private sealed class ContinuousTopDiagnosticCase
        {
            public string Name { get; set; }
            public bool Passed { get; set; }
            public string Details { get; set; }
        }

        private sealed class ContinuousTopDiagnosticRing
        {
            public IList<double> Knots { get; set; }
            public IList<XYZ> Controls { get; set; }
            public Curve FullCurve { get; set; }
            public Curve FirstHalf { get; set; }
            public Curve SecondHalf { get; set; }
        }

        private static bool TryRunActiveContinuousTopCapabilityDiagnostic(
            IList<IList<XYZ>> ringSamples,
            int sampleCount,
            IList<SmoothBRepPatchLayout> layouts,
            double shortCurveTolerance,
            XYZ headerAxisPoint,
            XYZ headerAxisDirection,
            double headerOutsideRadius,
            bool outletShouldChamfer,
            out string stopError)
        {
            stopError = null;

            ContinuousTopCapabilityDiagnosticSession session =
                _activeContinuousTopCapabilityDiagnostic;

            if (session == null)
                return false;

            try
            {
                string decision;
                string summary;

                string report = BuildContinuousTopCapabilityReport(
                    session,
                    ringSamples,
                    sampleCount,
                    layouts,
                    shortCurveTolerance,
                    headerAxisPoint,
                    headerAxisDirection,
                    headerOutsideRadius,
                    outletShouldChamfer,
                    out decision,
                    out summary);

                session.Complete(report, decision, summary);

                stopError =
                    "The continuous-top capability diagnostic completed. " +
                    "Production STEP generation was intentionally stopped. " +
                    "Review the saved diagnostic report before changing the exporter.";

                return true;
            }
            catch (Exception ex)
            {
                StringBuilder report = new StringBuilder();
                report.AppendLine("Parallel Systems Continuous-Top Capability Diagnostic");
                report.AppendLine(new string('=', 68));
                report.AppendLine();
                report.AppendLine("RESULT: DIAGNOSTIC CRASHED");
                report.AppendLine();
                report.AppendLine(ex.ToString());

                session.Complete(
                    report.ToString(),
                    "INCONCLUSIVE",
                    "The diagnostic itself failed. Review the exception in the saved report.");

                stopError =
                    "The continuous-top capability diagnostic failed internally: " +
                    ex.Message;

                return true;
            }
        }

        private static string BuildContinuousTopCapabilityReport(
            ContinuousTopCapabilityDiagnosticSession session,
            IList<IList<XYZ>> ringSamples,
            int sampleCount,
            IList<SmoothBRepPatchLayout> layouts,
            double shortCurveTolerance,
            XYZ headerAxisPoint,
            XYZ headerAxisDirection,
            double headerOutsideRadius,
            bool outletShouldChamfer,
            out string decision,
            out string summary)
        {
            const int saddleOuterRingIndex = 2;
            const int saddleRootRingIndex = 3;

            StringBuilder report = new StringBuilder();
            UIApplication uiApp = session.UiApp;
            Document document = session.Document;

            report.AppendLine("Parallel Systems Continuous-Top Capability Diagnostic");
            report.AppendLine(new string('=', 68));
            report.AppendLine();
            report.AppendLine("Purpose");
            report.AppendLine("-------");
            report.AppendLine(
                "This command isolates Revit BRepBuilder capability from the production STEP exporter. " +
                "It does not save a STEP file and does not modify the project model.");
            report.AppendLine();
            report.AppendLine("Environment");
            report.AppendLine("-----------");
            report.AppendLine("Timestamp: " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            report.AppendLine("Revit version: " +
                SafeText(uiApp?.Application?.VersionName));
            report.AppendLine("Revit number: " +
                SafeText(uiApp?.Application?.VersionNumber));
            report.AppendLine("Revit build: " +
                SafeText(uiApp?.Application?.VersionBuild));
            report.AppendLine("Document: " + SafeText(document?.Title));
            report.AppendLine("Document path: " + SafeText(document?.PathName));
            report.AppendLine("ShortCurveTolerance: " +
                FormatDiagnosticMillimetres(shortCurveTolerance));
            report.AppendLine("Sample count: " +
                sampleCount.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("Outlet chamfered: " + outletShouldChamfer);
            report.AppendLine("Header radius: " +
                FormatDiagnosticMillimetres(headerOutsideRadius));
            report.AppendLine("Header axis point: " + FormatPoint(headerAxisPoint));
            report.AppendLine("Header axis direction: " + FormatVector(headerAxisDirection));
            report.AppendLine("Selected source IDs: " +
                string.Join(", ",
                    (session.Selection.SourceElementIds ?? new List<ElementId>())
                        .Where(id => id != null)
                        .Select(FormatElementId)));
            report.AppendLine();

            if (outletShouldChamfer)
            {
                decision = "INCONCLUSIVE";
                summary =
                    "The selected shaped branch has an outlet chamfer. Run the capability test on the same plain-outlet case used for the clean-top production requirement.";

                report.AppendLine("RESULT: INCONCLUSIVE");
                report.AppendLine(summary);
                return report.ToString();
            }

            if (ringSamples == null ||
                ringSamples.Count <= saddleRootRingIndex ||
                ringSamples[saddleOuterRingIndex] == null ||
                ringSamples[saddleRootRingIndex] == null ||
                ringSamples[saddleOuterRingIndex].Count != sampleCount ||
                ringSamples[saddleRootRingIndex].Count != sampleCount)
            {
                decision = "INCONCLUSIVE";
                summary = "The production saddle rings were not available to the diagnostic.";
                report.AppendLine("RESULT: INCONCLUSIVE");
                report.AppendLine(summary);
                return report.ToString();
            }

            List<ContinuousTopDiagnosticCase> results =
                new List<ContinuousTopDiagnosticCase>();

            results.Add(RunPlanarCompleteCircleSingleEdgeTest(
                shortCurveTolerance));

            results.Add(RunPlanarCircleHalfEdgeTest(
                shortCurveTolerance));

            List<SmoothBRepPatchLayout> diagnosticLayouts =
                (layouts ?? new List<SmoothBRepPatchLayout>())
                    .Where(layout => layout != null)
                    .GroupBy(layout =>
                        layout.SplineSpanCount.ToString(CultureInfo.InvariantCulture) + ":" +
                        layout.PatchStartOffset.ToString(CultureInfo.InvariantCulture))
                    .Select(group => group.First())
                    .Take(8)
                    .ToList();

            if (diagnosticLayouts.Count == 0)
            {
                diagnosticLayouts.Add(new SmoothBRepPatchLayout
                {
                    SplineSpanCount = 8,
                    PatchStartOffset = 0
                });
            }

            ContinuousTopDiagnosticCase actualFaceResult =
                RunActualSaddleFaceTestAcrossLayouts(
                    ringSamples[saddleOuterRingIndex],
                    ringSamples[saddleRootRingIndex],
                    sampleCount,
                    diagnosticLayouts,
                    shortCurveTolerance);

            results.Add(actualFaceResult);

            ContinuousTopDiagnosticCase simplifiedShellResult =
                RunActualSaddleSimplifiedShellTestAcrossLayouts(
                    ringSamples[saddleOuterRingIndex],
                    ringSamples[saddleRootRingIndex],
                    sampleCount,
                    diagnosticLayouts,
                    shortCurveTolerance);

            results.Add(simplifiedShellResult);

            report.AppendLine("Test results");
            report.AppendLine("------------");
            report.AppendLine();

            for (int index = 0; index < results.Count; index++)
            {
                ContinuousTopDiagnosticCase result = results[index];
                report.AppendLine(
                    (index + 1).ToString(CultureInfo.InvariantCulture) +
                    ". " + result.Name);
                report.AppendLine("   Result: " +
                    (result.Passed ? "PASS" : "FAIL"));
                report.AppendLine(Indent(result.Details, "   "));
                report.AppendLine();
            }

            bool halfCirclePassed = results[1].Passed;
            bool actualFacePassed = actualFaceResult.Passed;
            bool shellPassed = simplifiedShellResult.Passed;

            report.AppendLine("Decision");
            report.AppendLine("--------");

            if (!halfCirclePassed)
            {
                decision = "STOP_REVIT_ONLY";
                summary =
                    "The basic two-half-edge planar annulus failed. The continuous-top production path should not continue until this fundamental BRep loop test is understood.";
            }
            else if (!actualFacePassed)
            {
                decision = "USE_EXTERNAL_CAD_KERNEL";
                summary =
                    "Revit accepted the basic loop topology but rejected the actual complete saddle strip as one face across every tested safe layout. Stop iterating on the production Revit-only exporter and move to an external CAD-kernel STEP postprocessor.";
            }
            else if (!shellPassed)
            {
                decision = "CONTINUE_REVIT_ONLY_SHELL_INTEGRATION";
                summary =
                    "Revit accepted the actual saddle strip as one face, but rejected it inside a simplified closed solid. The surface is possible; the remaining problem is shell orientation/connectivity and is worth continuing in Revit.";
            }
            else
            {
                decision = "CONTINUE_REVIT_ONLY_PRODUCTION_INTEGRATION";
                summary =
                    "Revit accepted both the actual one-face saddle strip and a simplified closed solid containing it. The clean-top topology is feasible in Revit; continue by transferring the proven topology into the production shell.";
            }

            report.AppendLine("Decision code: " + decision);
            report.AppendLine(summary);
            report.AppendLine();
            report.AppendLine(
                "Interpretation rule: Test 1 is informational because a single closed edge may be disallowed even when two open half-edges work. Tests 2 through 4 determine the go/no-go decision.");

            return report.ToString();
        }

        private static ContinuousTopDiagnosticCase
            RunPlanarCompleteCircleSingleEdgeTest(
                double shortCurveTolerance)
        {
            StringBuilder details = new StringBuilder();

            try
            {
                XYZ center = XYZ.Zero;
                XYZ xAxis = XYZ.BasisX;
                XYZ yAxis = XYZ.BasisY;

                Curve outer = Arc.Create(
                    center,
                    2.0,
                    0.0,
                    2.0 * Math.PI,
                    xAxis,
                    yAxis);

                Curve inner = Arc.Create(
                    center,
                    1.0,
                    0.0,
                    2.0 * Math.PI,
                    xAxis,
                    yAxis);

                AppendCurveDetails(details, "outer complete circle", outer);
                AppendCurveDetails(details, "inner complete circle", inner);

                BRepBuilder builder = new BRepBuilder(BRepType.OpenShell);
                BRepBuilderGeometryId outerEdge = builder.AddEdge(
                    BRepBuilderEdgeGeometry.Create(outer.Clone()));
                BRepBuilderGeometryId innerEdge = builder.AddEdge(
                    BRepBuilderEdgeGeometry.Create(inner.Clone()));

                Plane plane = Plane.CreateByNormalAndOrigin(
                    XYZ.BasisZ,
                    center);

                BRepBuilderGeometryId face = builder.AddFace(
                    BRepBuilderSurfaceGeometry.Create(plane, null),
                    false);

                BRepBuilderGeometryId outerLoop = builder.AddLoop(face);
                builder.AddCoEdge(outerLoop, outerEdge, false);
                builder.FinishLoop(outerLoop);

                BRepBuilderGeometryId innerLoop = builder.AddLoop(face);
                builder.AddCoEdge(innerLoop, innerEdge, true);
                builder.FinishLoop(innerLoop);

                builder.FinishFace(face);
                builder.Finish();

                bool passed = builder.IsResultAvailable();
                details.AppendLine("BRepBuilder result available: " + passed);

                return new ContinuousTopDiagnosticCase
                {
                    Name = "Planar annulus using one complete closed edge per loop",
                    Passed = passed,
                    Details = details.ToString()
                };
            }
            catch (Exception ex)
            {
                details.AppendLine("Exception: " + ex);

                return new ContinuousTopDiagnosticCase
                {
                    Name = "Planar annulus using one complete closed edge per loop",
                    Passed = false,
                    Details = details.ToString()
                };
            }
        }

        private static ContinuousTopDiagnosticCase
            RunPlanarCircleHalfEdgeTest(
                double shortCurveTolerance)
        {
            StringBuilder details = new StringBuilder();

            try
            {
                XYZ center = XYZ.Zero;
                Curve[] outer = CreateDiagnosticCircleHalves(center, 2.0);
                Curve[] inner = CreateDiagnosticCircleHalves(center, 1.0);

                AppendLoopDetails(
                    details,
                    "outer loop",
                    new[] { outer[0], outer[1] },
                    new[] { false, false });

                AppendLoopDetails(
                    details,
                    "inner loop",
                    new[] { inner[1], inner[0] },
                    new[] { true, true });

                BRepBuilder builder = new BRepBuilder(BRepType.OpenShell);
                BRepBuilderGeometryId[] outerEdges =
                {
                    builder.AddEdge(BRepBuilderEdgeGeometry.Create(outer[0].Clone())),
                    builder.AddEdge(BRepBuilderEdgeGeometry.Create(outer[1].Clone()))
                };

                BRepBuilderGeometryId[] innerEdges =
                {
                    builder.AddEdge(BRepBuilderEdgeGeometry.Create(inner[0].Clone())),
                    builder.AddEdge(BRepBuilderEdgeGeometry.Create(inner[1].Clone()))
                };

                Plane plane = Plane.CreateByNormalAndOrigin(
                    XYZ.BasisZ,
                    center);

                BRepBuilderGeometryId face = builder.AddFace(
                    BRepBuilderSurfaceGeometry.Create(plane, null),
                    false);

                BRepBuilderGeometryId outerLoop = builder.AddLoop(face);
                builder.AddCoEdge(outerLoop, outerEdges[0], false);
                builder.AddCoEdge(outerLoop, outerEdges[1], false);
                builder.FinishLoop(outerLoop);

                BRepBuilderGeometryId innerLoop = builder.AddLoop(face);
                builder.AddCoEdge(innerLoop, innerEdges[1], true);
                builder.AddCoEdge(innerLoop, innerEdges[0], true);
                builder.FinishLoop(innerLoop);

                builder.FinishFace(face);
                builder.Finish();

                bool passed = builder.IsResultAvailable();
                details.AppendLine("BRepBuilder result available: " + passed);

                return new ContinuousTopDiagnosticCase
                {
                    Name = "Planar annulus using two open half-edges per loop",
                    Passed = passed,
                    Details = details.ToString()
                };
            }
            catch (Exception ex)
            {
                details.AppendLine("Exception: " + ex);

                return new ContinuousTopDiagnosticCase
                {
                    Name = "Planar annulus using two open half-edges per loop",
                    Passed = false,
                    Details = details.ToString()
                };
            }
        }

        private static ContinuousTopDiagnosticCase
            RunActualSaddleFaceTestAcrossLayouts(
                IList<XYZ> saddleOuter,
                IList<XYZ> saddleRoot,
                int sampleCount,
                IList<SmoothBRepPatchLayout> layouts,
                double shortCurveTolerance)
        {
            StringBuilder details = new StringBuilder();

            foreach (SmoothBRepPatchLayout layout in layouts)
            {
                details.AppendLine(
                    "Layout spans=" + layout.SplineSpanCount +
                    ", offset=" + layout.PatchStartOffset);

                string attemptDetails;
                bool passed = TryRunActualSaddleFaceTest(
                    saddleOuter,
                    saddleRoot,
                    sampleCount,
                    layout,
                    shortCurveTolerance,
                    out attemptDetails);

                details.AppendLine(Indent(attemptDetails, "  "));

                if (passed)
                {
                    return new ContinuousTopDiagnosticCase
                    {
                        Name = "Actual saddleOuter-to-saddleRoot strip as one open-shell face",
                        Passed = true,
                        Details = details.ToString()
                    };
                }
            }

            return new ContinuousTopDiagnosticCase
            {
                Name = "Actual saddleOuter-to-saddleRoot strip as one open-shell face",
                Passed = false,
                Details = details.ToString()
            };
        }

        private static bool TryRunActualSaddleFaceTest(
            IList<XYZ> saddleOuter,
            IList<XYZ> saddleRoot,
            int sampleCount,
            SmoothBRepPatchLayout layout,
            double shortCurveTolerance,
            out string detailsText)
        {
            StringBuilder details = new StringBuilder();
            detailsText = null;

            try
            {
                ContinuousTopDiagnosticRing outer;
                ContinuousTopDiagnosticRing root;
                string error;

                if (!TryCreateDiagnosticRing(
                        saddleOuter,
                        sampleCount,
                        layout,
                        shortCurveTolerance,
                        "actual saddle outer",
                        out outer,
                        out error) ||
                    !TryCreateDiagnosticRing(
                        saddleRoot,
                        sampleCount,
                        layout,
                        shortCurveTolerance,
                        "actual saddle root",
                        out root,
                        out error))
                {
                    details.AppendLine("Ring creation failed: " + error);
                    detailsText = details.ToString();
                    return false;
                }

                AppendDiagnosticRing(details, "saddle outer", outer);
                AppendDiagnosticRing(details, "saddle root", root);

                for (int reverseIndex = 0; reverseIndex < 2; reverseIndex++)
                {
                    bool reverseSurface = reverseIndex == 1;
                    details.AppendLine("Surface reversed=" + reverseSurface);

                    try
                    {
                        BRepBuilderSurfaceGeometry surface;
                        if (!TryCreateClosedRuledStripSurfaceGeometry(
                                outer.FullCurve,
                                root.FullCurve,
                                "isolated actual saddle strip",
                                out surface,
                                out error))
                        {
                            details.AppendLine("Support creation failed: " + error);
                            continue;
                        }

                        BRepBuilder builder = new BRepBuilder(BRepType.OpenShell);
                        BRepBuilderGeometryId[,] edges =
                            AddDiagnosticRingEdges(builder, outer, root);

                        Curve[,] curves =
                        {
                            { outer.FirstHalf, outer.SecondHalf },
                            { root.FirstHalf, root.SecondHalf }
                        };

                        if (!TryAddDiagnosticTwoLoopFace(
                                builder,
                                surface,
                                edges,
                                curves,
                                0,
                                1,
                                reverseSurface,
                                details,
                                "actual saddle strip",
                                out error))
                        {
                            details.AppendLine("Face construction failed: " + error);
                            continue;
                        }

                        builder.Finish();
                        bool resultAvailable = builder.IsResultAvailable();
                        details.AppendLine("Result available=" + resultAvailable);

                        if (resultAvailable)
                        {
                            detailsText = details.ToString();
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        details.AppendLine("Attempt exception: " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                details.AppendLine("Test exception: " + ex);
            }

            detailsText = details.ToString();
            return false;
        }

        private static ContinuousTopDiagnosticCase
            RunActualSaddleSimplifiedShellTestAcrossLayouts(
                IList<XYZ> saddleOuter,
                IList<XYZ> saddleRoot,
                int sampleCount,
                IList<SmoothBRepPatchLayout> layouts,
                double shortCurveTolerance)
        {
            StringBuilder details = new StringBuilder();

            foreach (SmoothBRepPatchLayout layout in layouts)
            {
                details.AppendLine(
                    "Layout spans=" + layout.SplineSpanCount +
                    ", offset=" + layout.PatchStartOffset);

                string attemptDetails;
                bool passed = TryRunActualSaddleSimplifiedShellTest(
                    saddleOuter,
                    saddleRoot,
                    sampleCount,
                    layout,
                    shortCurveTolerance,
                    out attemptDetails);

                details.AppendLine(Indent(attemptDetails, "  "));

                if (passed)
                {
                    return new ContinuousTopDiagnosticCase
                    {
                        Name = "Actual one-face saddle strip inside a simplified closed solid",
                        Passed = true,
                        Details = details.ToString()
                    };
                }
            }

            return new ContinuousTopDiagnosticCase
            {
                Name = "Actual one-face saddle strip inside a simplified closed solid",
                Passed = false,
                Details = details.ToString()
            };
        }

        private static bool TryRunActualSaddleSimplifiedShellTest(
            IList<XYZ> saddleOuter,
            IList<XYZ> saddleRoot,
            int sampleCount,
            SmoothBRepPatchLayout layout,
            double shortCurveTolerance,
            out string detailsText)
        {
            StringBuilder details = new StringBuilder();
            detailsText = null;

            double offsetDistance = Math.Max(
                10.0 / FeetToMillimetres,
                shortCurveTolerance * 20.0);

            XYZ offsetVector = -XYZ.BasisZ * offsetDistance;

            List<XYZ> bottomOuterSamples = saddleOuter
                .Select(point => point + offsetVector)
                .ToList();

            List<XYZ> bottomRootSamples = saddleRoot
                .Select(point => point + offsetVector)
                .ToList();

            ContinuousTopDiagnosticRing[] rings =
                new ContinuousTopDiagnosticRing[4];

            string error;

            if (!TryCreateDiagnosticRing(
                    saddleOuter,
                    sampleCount,
                    layout,
                    shortCurveTolerance,
                    "shell top outer",
                    out rings[0],
                    out error) ||
                !TryCreateDiagnosticRing(
                    saddleRoot,
                    sampleCount,
                    layout,
                    shortCurveTolerance,
                    "shell top root",
                    out rings[1],
                    out error) ||
                !TryCreateDiagnosticRing(
                    bottomOuterSamples,
                    sampleCount,
                    layout,
                    shortCurveTolerance,
                    "shell bottom outer",
                    out rings[2],
                    out error) ||
                !TryCreateDiagnosticRing(
                    bottomRootSamples,
                    sampleCount,
                    layout,
                    shortCurveTolerance,
                    "shell bottom root",
                    out rings[3],
                    out error))
            {
                details.AppendLine("Ring creation failed: " + error);
                detailsText = details.ToString();
                return false;
            }

            for (int index = 0; index < rings.Length; index++)
                AppendDiagnosticRing(details, "shell ring " + index, rings[index]);

            List<string> uniqueErrors = new List<string>();

            for (int reverseMask = 0; reverseMask < 16; reverseMask++)
            {
                try
                {
                    BRepBuilder builder = new BRepBuilder(BRepType.Solid);
                    BRepBuilderGeometryId[,] edgeIds =
                        AddDiagnosticRingEdges(builder, rings);

                    Curve[,] curves = new Curve[4, 2];
                    for (int ringIndex = 0; ringIndex < 4; ringIndex++)
                    {
                        curves[ringIndex, 0] = rings[ringIndex].FirstHalf;
                        curves[ringIndex, 1] = rings[ringIndex].SecondHalf;
                    }

                    BRepBuilderSurfaceGeometry topSurface;
                    BRepBuilderSurfaceGeometry outerWallSurface;
                    BRepBuilderSurfaceGeometry bottomSurface;
                    BRepBuilderSurfaceGeometry innerWallSurface;

                    if (!TryCreateClosedRuledStripSurfaceGeometry(
                            rings[0].FullCurve,
                            rings[1].FullCurve,
                            "diagnostic shell top",
                            out topSurface,
                            out error) ||
                        !TryCreateClosedRuledStripSurfaceGeometry(
                            rings[2].FullCurve,
                            rings[0].FullCurve,
                            "diagnostic shell outer wall",
                            out outerWallSurface,
                            out error) ||
                        !TryCreateClosedRuledStripSurfaceGeometry(
                            rings[3].FullCurve,
                            rings[2].FullCurve,
                            "diagnostic shell bottom",
                            out bottomSurface,
                            out error) ||
                        !TryCreateClosedRuledStripSurfaceGeometry(
                            rings[1].FullCurve,
                            rings[3].FullCurve,
                            "diagnostic shell inner wall",
                            out innerWallSurface,
                            out error))
                    {
                        details.AppendLine("Support creation failed: " + error);
                        detailsText = details.ToString();
                        return false;
                    }

                    StringBuilder attempt = new StringBuilder();
                    attempt.AppendLine("reverse mask=" + reverseMask);

                    bool added =
                        TryAddDiagnosticTwoLoopFace(
                            builder,
                            topSurface,
                            edgeIds,
                            curves,
                            0,
                            1,
                            (reverseMask & 1) != 0,
                            attempt,
                            "shell top",
                            out error) &&
                        TryAddDiagnosticTwoLoopFace(
                            builder,
                            outerWallSurface,
                            edgeIds,
                            curves,
                            2,
                            0,
                            (reverseMask & 2) != 0,
                            attempt,
                            "shell outer wall",
                            out error) &&
                        TryAddDiagnosticTwoLoopFace(
                            builder,
                            bottomSurface,
                            edgeIds,
                            curves,
                            3,
                            2,
                            (reverseMask & 4) != 0,
                            attempt,
                            "shell bottom",
                            out error) &&
                        TryAddDiagnosticTwoLoopFace(
                            builder,
                            innerWallSurface,
                            edgeIds,
                            curves,
                            1,
                            3,
                            (reverseMask & 8) != 0,
                            attempt,
                            "shell inner wall",
                            out error);

                    if (!added)
                    {
                        string concise = "mask " + reverseMask + ": " + error;
                        if (!uniqueErrors.Contains(concise))
                            uniqueErrors.Add(concise);
                        continue;
                    }

                    builder.Finish();

                    if (!builder.IsResultAvailable())
                    {
                        uniqueErrors.Add(
                            "mask " + reverseMask +
                            ": Finish completed but no result was available.");
                        continue;
                    }

                    Solid result = builder.GetResult();
                    double volume = result == null ? 0.0 : result.Volume;

                    attempt.AppendLine("Result available=True");
                    attempt.AppendLine("Volume=" +
                        (volume * FeetToMillimetres * FeetToMillimetres * FeetToMillimetres)
                            .ToString("0.######", CultureInfo.InvariantCulture) +
                        " mm^3");

                    details.AppendLine(attempt.ToString());
                    detailsText = details.ToString();
                    return result != null && volume > GeometryTolerance;
                }
                catch (Exception ex)
                {
                    string concise = "mask " + reverseMask + ": " + ex.Message;
                    if (!uniqueErrors.Contains(concise))
                        uniqueErrors.Add(concise);
                }
            }

            details.AppendLine("All 16 face-orientation combinations failed:");
            foreach (string item in uniqueErrors.Take(20))
                details.AppendLine("- " + item);

            detailsText = details.ToString();
            return false;
        }

        private static bool TryCreateDiagnosticRing(
            IList<XYZ> samples,
            int sampleCount,
            SmoothBRepPatchLayout layout,
            double shortCurveTolerance,
            string context,
            out ContinuousTopDiagnosticRing ring,
            out string error)
        {
            ring = null;

            IList<double> knots;
            IList<XYZ> controls;
            Curve full;
            Curve first;
            Curve second;

            if (!TryCreateContinuousCompositeNurbsBRepRing(
                    samples,
                    sampleCount,
                    layout.SplineSpanCount,
                    layout.PatchStartOffset,
                    shortCurveTolerance,
                    context,
                    out knots,
                    out controls,
                    out full,
                    out first,
                    out second,
                    out error))
            {
                return false;
            }

            ring = new ContinuousTopDiagnosticRing
            {
                Knots = knots,
                Controls = controls,
                FullCurve = full,
                FirstHalf = first,
                SecondHalf = second
            };

            return true;
        }

        private static Curve[] CreateDiagnosticCircleHalves(
            XYZ center,
            double radius)
        {
            return new Curve[]
            {
                Arc.Create(
                    center,
                    radius,
                    0.0,
                    Math.PI,
                    XYZ.BasisX,
                    XYZ.BasisY),
                Arc.Create(
                    center,
                    radius,
                    Math.PI,
                    2.0 * Math.PI,
                    XYZ.BasisX,
                    XYZ.BasisY)
            };
        }

        private static BRepBuilderGeometryId[,]
            AddDiagnosticRingEdges(
                BRepBuilder builder,
                params ContinuousTopDiagnosticRing[] rings)
        {
            BRepBuilderGeometryId[,] result =
                new BRepBuilderGeometryId[rings.Length, 2];

            for (int ringIndex = 0; ringIndex < rings.Length; ringIndex++)
            {
                result[ringIndex, 0] = builder.AddEdge(
                    BRepBuilderEdgeGeometry.Create(
                        rings[ringIndex].FirstHalf.Clone()));

                result[ringIndex, 1] = builder.AddEdge(
                    BRepBuilderEdgeGeometry.Create(
                        rings[ringIndex].SecondHalf.Clone()));
            }

            return result;
        }

        private static bool TryAddDiagnosticTwoLoopFace(
            BRepBuilder builder,
            BRepBuilderSurfaceGeometry surface,
            BRepBuilderGeometryId[,] edgeIds,
            Curve[,] curves,
            int firstRing,
            int secondRing,
            bool reverseSurface,
            StringBuilder details,
            string context,
            out string error)
        {
            error = null;

            try
            {
                BRepBuilderGeometryId face = builder.AddFace(
                    surface,
                    reverseSurface);

                BRepBuilderGeometryId firstLoop = builder.AddLoop(face);
                AppendLoopDetails(
                    details,
                    context + " first loop",
                    new[]
                    {
                        curves[firstRing, 0],
                        curves[firstRing, 1]
                    },
                    new[] { false, false });

                builder.AddCoEdge(firstLoop, edgeIds[firstRing, 0], false);
                builder.AddCoEdge(firstLoop, edgeIds[firstRing, 1], false);
                builder.FinishLoop(firstLoop);

                BRepBuilderGeometryId secondLoop = builder.AddLoop(face);
                AppendLoopDetails(
                    details,
                    context + " second loop",
                    new[]
                    {
                        curves[secondRing, 1],
                        curves[secondRing, 0]
                    },
                    new[] { true, true });

                builder.AddCoEdge(secondLoop, edgeIds[secondRing, 1], true);
                builder.AddCoEdge(secondLoop, edgeIds[secondRing, 0], true);
                builder.FinishLoop(secondLoop);

                builder.FinishFace(face);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                details.AppendLine(context + " exception: " + ex);
                return false;
            }
        }

        private static void AppendDiagnosticRing(
            StringBuilder builder,
            string name,
            ContinuousTopDiagnosticRing ring)
        {
            builder.AppendLine(name + ":");
            AppendCurveDetails(builder, "  full", ring.FullCurve);
            AppendCurveDetails(builder, "  first half", ring.FirstHalf);
            AppendCurveDetails(builder, "  second half", ring.SecondHalf);

            double startJoin = ring.FirstHalf.GetEndPoint(0)
                .DistanceTo(ring.FullCurve.GetEndPoint(0));
            double middleJoin = ring.FirstHalf.GetEndPoint(1)
                .DistanceTo(ring.SecondHalf.GetEndPoint(0));
            double endJoin = ring.SecondHalf.GetEndPoint(1)
                .DistanceTo(ring.FullCurve.GetEndPoint(1));
            double closure = ring.FullCurve.GetEndPoint(1)
                .DistanceTo(ring.FullCurve.GetEndPoint(0));

            builder.AppendLine("  start/full gap=" + FormatDiagnosticMillimetres(startJoin));
            builder.AppendLine("  half junction gap=" + FormatDiagnosticMillimetres(middleJoin));
            builder.AppendLine("  end/full gap=" + FormatDiagnosticMillimetres(endJoin));
            builder.AppendLine("  full closure gap=" + FormatDiagnosticMillimetres(closure));
        }

        private static void AppendCurveDetails(
            StringBuilder builder,
            string name,
            Curve curve)
        {
            if (curve == null)
            {
                builder.AppendLine(name + ": <null>");
                return;
            }

            try
            {
                builder.AppendLine(name + ":");
                builder.AppendLine("  type=" + curve.GetType().FullName);
                builder.AppendLine("  bound=" + curve.IsBound);
                builder.AppendLine("  closed=" + curve.IsClosed);
                builder.AppendLine("  cyclic=" + curve.IsCyclic);
                builder.AppendLine("  length=" + FormatDiagnosticMillimetres(curve.Length));
                builder.AppendLine("  start=" + FormatPoint(curve.GetEndPoint(0)));
                builder.AppendLine("  end=" + FormatPoint(curve.GetEndPoint(1)));
                builder.AppendLine("  endpoint gap=" +
                    FormatDiagnosticMillimetres(
                        curve.GetEndPoint(0)
                            .DistanceTo(curve.GetEndPoint(1))));
                builder.AppendLine("  parameter domain=[" +
                    curve.GetEndParameter(0).ToString("0.############", CultureInfo.InvariantCulture) +
                    ", " +
                    curve.GetEndParameter(1).ToString("0.############", CultureInfo.InvariantCulture) +
                    "]");
            }
            catch (Exception ex)
            {
                builder.AppendLine(name + " inspection exception: " + ex.Message);
            }
        }

        private static void AppendLoopDetails(
            StringBuilder builder,
            string context,
            IList<Curve> curves,
            IList<bool> flipped)
        {
            builder.AppendLine(context + ":");

            for (int index = 0; index < curves.Count; index++)
            {
                Curve curve = curves[index];
                bool isFlipped = flipped[index];
                XYZ start = curve.GetEndPoint(isFlipped ? 1 : 0);
                XYZ end = curve.GetEndPoint(isFlipped ? 0 : 1);
                Curve nextCurve = curves[(index + 1) % curves.Count];
                bool nextFlipped = flipped[(index + 1) % curves.Count];
                XYZ nextStart = nextCurve.GetEndPoint(nextFlipped ? 1 : 0);

                builder.AppendLine(
                    "  edge " + index +
                    ", flipped=" + isFlipped +
                    ", start=" + FormatPoint(start) +
                    ", end=" + FormatPoint(end) +
                    ", gap-to-next=" +
                    FormatDiagnosticMillimetres(end.DistanceTo(nextStart)));
            }
        }

        private static string FormatDiagnosticMillimetres(double feet)
        {
            if (double.IsNaN(feet) || double.IsInfinity(feet))
                return feet.ToString(CultureInfo.InvariantCulture);

            return (feet * FeetToMillimetres)
                .ToString("0.#########", CultureInfo.InvariantCulture) +
                " mm";
        }

        private static string FormatPoint(XYZ point)
        {
            if (point == null)
                return "<null>";

            return "(" +
                (point.X * FeetToMillimetres).ToString("0.#########", CultureInfo.InvariantCulture) + ", " +
                (point.Y * FeetToMillimetres).ToString("0.#########", CultureInfo.InvariantCulture) + ", " +
                (point.Z * FeetToMillimetres).ToString("0.#########", CultureInfo.InvariantCulture) +
                ") mm";
        }

        private static string FormatVector(XYZ vector)
        {
            if (vector == null)
                return "<null>";

            return "(" +
                vector.X.ToString("0.############", CultureInfo.InvariantCulture) + ", " +
                vector.Y.ToString("0.############", CultureInfo.InvariantCulture) + ", " +
                vector.Z.ToString("0.############", CultureInfo.InvariantCulture) +
                ")";
        }

        private static string FormatElementId(ElementId id)
        {
            if (id == null)
                return "<null>";

#if REVIT2024_OR_GREATER
            return id.Value.ToString(CultureInfo.InvariantCulture);
#else
            return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "<not available>"
                : value;
        }

        private static string Indent(string value, string prefix)
        {
            if (string.IsNullOrEmpty(value))
                return prefix;

            return string.Join(
                Environment.NewLine,
                value.Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Select(line => prefix + line));
        }
#else
        private static bool TryRunActiveContinuousTopCapabilityDiagnostic(
            IList<IList<XYZ>> ringSamples,
            int sampleCount,
            IList<SmoothBRepPatchLayout> layouts,
            double shortCurveTolerance,
            XYZ headerAxisPoint,
            XYZ headerAxisDirection,
            double headerOutsideRadius,
            bool outletShouldChamfer,
            out string stopError)
        {
            stopError = null;
            return false;
        }

        public static bool ValidateContinuousTopDiagnosticSelection(
            Document document,
            FabricationSelection selection,
            out string error)
        {
            error =
                "The continuous-top BRep capability diagnostic requires Revit 2025 or newer.";

            return false;
        }
#endif
    }
}
