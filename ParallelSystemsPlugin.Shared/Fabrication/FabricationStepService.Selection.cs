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
        public static FabricationSelection CollectSelection(
            UIDocument uiDoc)
        {
            if (uiDoc == null)
                return null;

            Document doc = uiDoc.Document;
            ICollection<ElementId> selectedIds =
                uiDoc.Selection.GetElementIds();

            List<Element> selectedElements = selectedIds
                .Select(doc.GetElement)
                .Where(x => x != null)
                .ToList();

            if (selectedElements.Count == 0)
            {
                IList<Reference> picked = uiDoc.Selection.PickObjects(
                    ObjectType.Element,
                    new FabricationSelectionFilter(),
                    "Select one assembly, or select the pipes and fittings to export.");

                selectedElements = picked
                    .Select(x => doc.GetElement(x.ElementId))
                    .Where(x => x != null)
                    .ToList();
            }

            List<AssemblyInstance> selectedAssemblies = selectedElements
                .OfType<AssemblyInstance>()
                .ToList();

            if (selectedAssemblies.Count > 1)
            {
                AppDialog.Warn(
                    "Fabrication STEP",
                    "Select only one Revit assembly per STEP export. " +
                    "This prevents elements from different spools from being combined accidentally.");

                return null;
            }

            HashSet<ElementId> sourceIds = new HashSet<ElementId>();
            string suggestedName = null;

            foreach (Element element in selectedElements)
            {
                AssemblyInstance assembly = element as AssemblyInstance;
                if (assembly != null)
                {
                    if (string.IsNullOrWhiteSpace(suggestedName))
                        suggestedName = assembly.Name;

                    foreach (ElementId memberId in assembly.GetMemberIds())
                    {
                        Element member = doc.GetElement(memberId);
                        if (IsSupportedSourceElement(member))
                            sourceIds.Add(memberId);
                    }

                    continue;
                }

                if (IsSupportedSourceElement(element))
                    sourceIds.Add(element.Id);
            }

            if (sourceIds.Count == 0)
            {
                AppDialog.Warn(
                    "Fabrication STEP",
                    "No supported elements were selected.\n\n" +
                    "Select a Revit assembly, pipe, pipe fitting, or pipe accessory.");

                return null;
            }

            if (string.IsNullOrWhiteSpace(suggestedName))
            {
                suggestedName = doc.ActiveView?.Name;

                if (string.IsNullOrWhiteSpace(suggestedName))
                    suggestedName = doc.Title;
            }

            HashSet<ElementId> calculationContextIds =
                new HashSet<ElementId>(sourceIds);

            Dictionary<ElementId, ElementId>
                explicitHeaderPipeIdsByBranch =
                    new Dictionary<ElementId, ElementId>();

            List<Element> shapedBranches = sourceIds
                .Select(doc.GetElement)
                .Where(x =>
                    x != null &&
                    IsShapedBranchLike(doc, x))
                .ToList();

            bool selectedScopeContainsPipe = sourceIds
                .Select(doc.GetElement)
                .Any(x => x is Pipe);

            // The fastest and safest branch-only workflow is to let the user
            // identify the header directly. The picked pipe becomes read-only
            // calculation context and is not added to the STEP export scope.
            // Normal assembly fabrication does not show this dialog because the
            // selected scope already contains one or more pipes.
            if (shapedBranches.Count > 0 &&
                !selectedScopeContainsPipe)
            {
                for (int branchIndex = 0;
                     branchIndex < shapedBranches.Count;
                     branchIndex++)
                {
                    Element branch = shapedBranches[branchIndex];
                    bool branchChoiceCompleted = false;

                    while (!branchChoiceCompleted)
                    {
                        string branchPosition = shapedBranches.Count > 1
                            ? "\n\nBranch " +
                              (branchIndex + 1).ToString(
                                  CultureInfo.InvariantCulture) +
                              " of " +
                              shapedBranches.Count.ToString(
                                  CultureInfo.InvariantCulture) +
                              ": " +
                              GetElementDisplayName(branch)
                            : string.Empty;

                        int choice = AppDialog.Choose(
                            "Fabrication STEP - Header Pipe Required",
                            "A shaped branch was selected without its " +
                            "main/header pipe.",
                            "The header pipe is required to calculate the " +
                            "branch saddle, straight-through bore, 1 mm weld " +
                            "land, and external 30 degree bevel.\n\n" +
                            "A selected header is used only as read-only " +
                            "calculation context and will not be included in " +
                            "the STEP file." +
                            branchPosition,
                            new List<string>
                            {
                                "Select Header Pipe",
                                "Search Automatically"
                            },
                            0);

                        if (choice < 0)
                            return null;

                        if (choice == 1)
                        {
                            branchChoiceCompleted = true;
                            continue;
                        }

                        try
                        {
                            Reference pickedHeader =
                                uiDoc.Selection.PickObject(
                                    ObjectType.Element,
                                    new FabricationHeaderPipeSelectionFilter(),
                                    "Select the main/header pipe for the shaped " +
                                    "branch. Press Esc to return to the choice " +
                                    "dialog.");

                            Pipe headerPipe =
                                doc.GetElement(
                                    pickedHeader.ElementId) as Pipe;

                            if (headerPipe == null)
                                continue;

                            calculationContextIds.Add(
                                headerPipe.Id);

                            explicitHeaderPipeIdsByBranch[
                                branch.Id] = headerPipe.Id;

                            branchChoiceCompleted = true;
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            // Return to the explicit choice dialog. Cancellation
                            // is not silently converted into automatic search.
                        }
                    }
                }
            }

            return new FabricationSelection
            {
                SourceElementIds = sourceIds.ToList(),
                CalculationContextElementIds =
                    calculationContextIds.ToList(),
                ExplicitHeaderPipeIdsByBranch =
                    explicitHeaderPipeIdsByBranch,
                SuggestedFileName = SanitizeFileName(suggestedName)
            };
        }

        private static bool IsSupportedSourceElement(Element element)
        {
            return IsPipingNetworkElement(element) &&
                   !IsIgnoredConnectionElement(
                       element.Document,
                       element);
        }

        private static bool IsPipingNetworkElement(Element element)
        {
            if (element == null || element.Category == null)
                return false;

            if (element is Pipe)
                return true;

            int categoryValue = GetCategoryValue(element.Category.Id);

            return categoryValue ==
                       (int)BuiltInCategory.OST_PipeFitting ||
                   categoryValue ==
                       (int)BuiltInCategory.OST_PipeAccessory;
        }

        private static int GetCategoryValue(ElementId categoryId)
        {
#if REVIT2024_OR_GREATER
            return checked((int)categoryId.Value);
#else
            return categoryId.IntegerValue;
#endif
        }

        private static string GetElementDisplayName(Element element)
        {
            if (element == null)
                return string.Empty;

            string cachedName;

            if (TryGetCachedDisplayName(
                    element,
                    out cachedName))
            {
                return cachedName;
            }

            string familyName = null;
            string typeName = null;

            FamilyInstance familyInstance = element as FamilyInstance;
            if (familyInstance?.Symbol != null)
            {
                familyName = familyInstance.Symbol.FamilyName;
                typeName = familyInstance.Symbol.Name;
            }

            string displayName;

            if (!string.IsNullOrWhiteSpace(familyName) ||
                !string.IsNullOrWhiteSpace(typeName))
            {
                displayName = string.Join(
                    " / ",
                    new[] { familyName, typeName }
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            else
            {
                displayName = string.IsNullOrWhiteSpace(element.Name)
                    ? element.GetType().Name
                    : element.Name;
            }

            CacheDisplayName(
                element,
                displayName);

            return displayName;
        }

        private static string SanitizeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value)
                ? "Fabrication-Spool"
                : value.Trim();

            foreach (char invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(result)
                ? "Fabrication-Spool"
                : result;
        }

        private sealed class FabricationSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element is AssemblyInstance ||
                       IsSupportedSourceElement(element);
            }

            public bool AllowReference(
                Reference reference,
                XYZ position)
            {
                return false;
            }
        }

        private sealed class FabricationHeaderPipeSelectionFilter :
            ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element is Pipe;
            }

            public bool AllowReference(
                Reference reference,
                XYZ position)
            {
                return false;
            }
        }
    }
}
