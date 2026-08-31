using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static class FabricationPreflightService
    {
        public static FabricationPreflightResult Check(
            Document doc,
            FabricationSelection selection)
        {
            FabricationPreflightResult result =
                new FabricationPreflightResult();

            if (doc == null)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        Message = "No active Revit project is open."
                    });

                return result;
            }

            if (doc.IsReadOnly)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        Message =
                            "The active Revit document is read-only. " +
                            "Fabrication STEP requires a writable project " +
                            "while it creates its temporary export model."
                    });
            }

            if (selection == null ||
                selection.SourceElementIds == null ||
                selection.SourceElementIds.Count == 0)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        Message =
                            "No fabrication source elements were selected."
                    });

                return result;
            }

            if (!doc.IsWorkshared)
                return result;

            HashSet<ElementId> sourceIds =
                new HashSet<ElementId>(
                    selection.SourceElementIds.Distinct());

            foreach (ElementId elementId in sourceIds)
            {
                Element element = doc.GetElement(elementId);

                if (element == null)
                {
                    result.BlockingIssues.Add(
                        new FabricationPreflightIssue
                        {
                            Message =
                                "A selected source element no longer exists " +
                                "in the active document."
                        });

                    continue;
                }

                string elementName =
                    GetElementDisplayName(element);

                CheckCentralUpdateStatus(
                    doc,
                    element,
                    elementName,
                    result);

                CheckOwnership(
                    doc,
                    element,
                    elementName,
                    result);
            }

            // Calculation-context elements are read only and never checked
            // out by fabrication. Their central-model freshness still matters
            // because a stale header pipe would produce a stale branch saddle.
            foreach (ElementId contextId in
                     (selection.CalculationContextElementIds ??
                      new List<ElementId>())
                     .Where(x => x != null)
                     .Distinct()
                     .Where(x => !sourceIds.Contains(x)))
            {
                Element contextElement =
                    doc.GetElement(contextId);

                if (contextElement == null)
                {
                    result.BlockingIssues.Add(
                        new FabricationPreflightIssue
                        {
                            Message =
                                "A fabrication calculation-context element no " +
                                "longer exists in the active document."
                        });

                    continue;
                }

                CheckCentralUpdateStatus(
                    doc,
                    contextElement,
                    GetElementDisplayName(contextElement),
                    result);
            }

            return result;
        }

        public static FabricationPreflightResult
            CheckViewForTemporaryIsolation(
                Document doc,
                View view)
        {
            FabricationPreflightResult result =
                new FabricationPreflightResult();

            if (doc == null || view == null)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        Message =
                            "No active graphical view is available."
                    });

                return result;
            }

            if (!doc.IsWorkshared)
                return result;

            string viewName =
                string.IsNullOrWhiteSpace(view.Name)
                    ? "Active view"
                    : view.Name;

            try
            {
                ModelUpdatesStatus updateStatus =
                    WorksharingUtils.GetModelUpdatesStatus(
                        doc,
                        view.Id);

                if (updateStatus ==
                        ModelUpdatesStatus.UpdatedInCentral ||
                    updateStatus ==
                        ModelUpdatesStatus.DeletedInCentral)
                {
                    result.BlockingIssues.Add(
                        new FabricationPreflightIssue
                        {
                            ElementName = viewName,
                            Message =
                                "The active view is not current with " +
                                "the central model. Run Reload Latest " +
                                "before applying temporary isolation."
                        });
                }
            }
            catch (Exception ex)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        ElementName = viewName,
                        Message =
                            "The plugin could not verify the active " +
                            "view against the central model: " +
                            ex.Message
                    });
            }

            try
            {
                CheckoutStatus checkoutStatus =
                    WorksharingUtils.GetCheckoutStatus(
                        doc,
                        view.Id);

                if (checkoutStatus ==
                    CheckoutStatus.OwnedByOtherUser)
                {
                    string owner = string.Empty;

                    try
                    {
                        WorksharingTooltipInfo information =
                            WorksharingUtils
                                .GetWorksharingTooltipInfo(
                                    doc,
                                    view.Id);

                        owner = information?.Owner ?? string.Empty;
                    }
                    catch
                    {
                        // The checkout result is the authoritative check.
                    }

                    result.BlockingIssues.Add(
                        new FabricationPreflightIssue
                        {
                            ElementName = viewName,
                            Message =
                                "Temporary isolation cannot be applied " +
                                "because this view is owned by another " +
                                "user" +
                                (string.IsNullOrWhiteSpace(owner)
                                    ? "."
                                    : ": " + owner + ".")
                        });
                }
            }
            catch (Exception ex)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        ElementName = viewName,
                        Message =
                            "The plugin could not verify ownership of " +
                            "the active view: " + ex.Message
                    });
            }

            return result;
        }

        private static void CheckCentralUpdateStatus(
            Document doc,
            Element element,
            string elementName,
            FabricationPreflightResult result)
        {
            try
            {
                ModelUpdatesStatus updateStatus =
                    WorksharingUtils.GetModelUpdatesStatus(
                        doc,
                        element.Id);

                if (updateStatus ==
                    ModelUpdatesStatus.UpdatedInCentral)
                {
                    result.BlockingIssues.Add(
                        new FabricationPreflightIssue
                        {
                            ElementName = elementName,
                            Message =
                                "This element has newer user changes in " +
                                "the central model. Run Reload Latest or " +
                                "Synchronize with Central before exporting."
                        });
                }
                else if (updateStatus ==
                         ModelUpdatesStatus.DeletedInCentral)
                {
                    result.BlockingIssues.Add(
                        new FabricationPreflightIssue
                        {
                            ElementName = elementName,
                            Message =
                                "This element was deleted in the central " +
                                "model. Reload Latest before exporting."
                        });
                }
            }
            catch (Exception ex)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        ElementName = elementName,
                        Message =
                            "The plugin could not verify whether this " +
                            "element is current with the central model: " +
                            ex.Message
                    });
            }
        }

        private static void CheckOwnership(
            Document doc,
            Element element,
            string elementName,
            FabricationPreflightResult result)
        {
            try
            {
                CheckoutStatus checkoutStatus =
                    WorksharingUtils.GetCheckoutStatus(
                        doc,
                        element.Id);

                if (checkoutStatus !=
                    CheckoutStatus.OwnedByOtherUser)
                {
                    return;
                }

                string owner = string.Empty;
                string lastChangedBy = string.Empty;

                try
                {
                    WorksharingTooltipInfo information =
                        WorksharingUtils.GetWorksharingTooltipInfo(
                            doc,
                            element.Id);

                    owner = information?.Owner ?? string.Empty;
                    lastChangedBy =
                        information?.LastChangedBy ?? string.Empty;
                }
                catch
                {
                    // Checkout status is sufficient. Tooltip information is
                    // only used to make the custom warning more useful.
                }

                string ownerText =
                    !string.IsNullOrWhiteSpace(owner)
                        ? "Owner: " + owner + "."
                        : "The current owner could not be resolved.";

                string lastChangedText =
                    !string.IsNullOrWhiteSpace(lastChangedBy)
                        ? " Last changed by: " + lastChangedBy + "."
                        : string.Empty;

                result.Warnings.Add(
                    new FabricationPreflightIssue
                    {
                        ElementName = elementName,
                        Message =
                            ownerText + lastChangedText +
                            " Continue only when exporting your current " +
                            "local snapshot is acceptable."
                    });
            }
            catch (Exception ex)
            {
                result.BlockingIssues.Add(
                    new FabricationPreflightIssue
                    {
                        ElementName = elementName,
                        Message =
                            "The plugin could not verify this element's " +
                            "worksharing ownership: " + ex.Message
                    });
            }
        }

        private static string GetElementDisplayName(Element element)
        {
            if (element == null)
                return string.Empty;

            FamilyInstance familyInstance =
                element as FamilyInstance;

            if (familyInstance?.Symbol != null)
            {
                string familyName =
                    familyInstance.Symbol.FamilyName;

                string typeName =
                    familyInstance.Symbol.Name;

                return string.Join(
                    " / ",
                    new[] { familyName, typeName }
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x)));
            }

            return string.IsNullOrWhiteSpace(element.Name)
                ? element.GetType().Name
                : element.Name;
        }
    }
}
