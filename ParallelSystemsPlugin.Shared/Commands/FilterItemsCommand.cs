using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.Compatibility;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class FilterItemsCommand : IExternalCommand
    {
        private static FilterItemsExternalEventHandler _handler;
        private static ExternalEvent _externalEvent;
        private static FilterItemsDialog _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            try
            {
                if (!App.IsUserAuthorized)
                {
                    AppDialog.Warn(
                        "Access Denied",
                        "Your account is not authorized to use this function.");

                    return Result.Cancelled;
                }

                UIApplication uiapp = commandData.Application;
                UIDocument uidoc = uiapp.ActiveUIDocument;
                Document doc = uidoc != null ? uidoc.Document : null;
                View view = uidoc != null ? uidoc.ActiveView : null;

                if (doc == null || view == null)
                {
                    AppDialog.Warn(uiapp, "Filter Items", "No active document or view.");
                    return Result.Cancelled;
                }

                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return Result.Succeeded;
                }

                // The tool owns Temporary Hide/Isolate while it is being used.
                // Starting from a pre-existing temporary state can make the quantities incomplete,
                // so reset only after explicit user confirmation.
                if (view.IsTemporaryHideIsolateActive())
                {
                    bool reset = AppDialog.Confirm(
                        uiapp,
                        "Filter Items",
                        "Temporary Hide/Isolate is already active in this view.\n\n" +
                        "Filter Items needs the full active-view component set so its quantities are reliable.\n\n" +
                        "Reset the current Temporary Hide/Isolate state and continue?",
                        true);

                    if (!reset)
                        return Result.Cancelled;

                    using (Transaction tx = new Transaction(doc, "Reset Temporary Hide/Isolate"))
                    {
                        tx.Start();
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        tx.Commit();
                    }
                }

                List<FilterItemGroupModel> groups = CollectItems(doc, view);
                int totalQuantity = groups.Sum(x => x.Quantity);

                if (totalQuantity == 0)
                {
                    AppDialog.Info(
                        uiapp,
                        "Filter Items",
                        "No BOM components were found in the active view.\n\n" +
                        "Filter Items currently tracks Pipes, Pipe Fittings, and Pipe Accessories because those are the component categories used by the Procurement BOM reports.");
                    return Result.Succeeded;
                }

                if (_handler == null)
                    _handler = new FilterItemsExternalEventHandler();

                if (_externalEvent == null)
                    _externalEvent = ExternalEvent.Create(_handler);

                _handler.Configure(doc, view.Id);

                _window = new FilterItemsDialog(
                    view.Name,
                    groups,
                    _handler,
                    _externalEvent);

                _window.Closed += delegate { _window = null; };
                _window.ShowModeless(uiapp.MainWindowHandle);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }

        private static List<FilterItemGroupModel> CollectItems(Document doc, View view)
        {
            var elements = new List<Element>();

            AddCategoryElements(doc, view, BuiltInCategory.OST_PipeCurves, elements);
            AddCategoryElements(doc, view, BuiltInCategory.OST_PipeFitting, elements);
            AddCategoryElements(doc, view, BuiltInCategory.OST_PipeAccessory, elements);

            var records = new List<FilterItemRecord>();

            foreach (Element element in elements)
            {
                if (element == null || element.GetTypeId() == ElementId.InvalidElementId)
                    continue;

                string familyName = GetFamilyName(element);
                string typeName = GetTypeName(element);
                string description = GetDescription(element);
                string size = GetSizeText(element);
                string groupName = GetGroupName(element, familyName, typeName, description);
                long typeId = RevitApiCompatibility.GetElementIdValue(element.GetTypeId());

                records.Add(new FilterItemRecord
                {
                    ElementId = element.Id,
                    GroupName = groupName,
                    TypeId = typeId,
                    FamilyName = familyName,
                    TypeName = typeName,
                    Size = size,
                    Description = description
                });
            }

            var itemModels = records
                .GroupBy(x => new
                {
                    x.GroupName,
                    x.TypeId,
                    Size = NormalizeKey(x.Size),
                    Description = NormalizeKey(x.Description)
                })
                .Select(g =>
                {
                    FilterItemRecord first = g.First();
                    List<ElementId> ids = g.Select(x => x.ElementId).Distinct().ToList();

                    return new FilterItemModel(
                        first.GroupName,
                        BuildDisplayName(first),
                        ids);
                })
                .OrderBy(x => GetGroupSortOrder(x.GroupName))
                .ThenBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return itemModels
                .GroupBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new FilterItemGroupModel(g.Key, g.ToList()))
                .OrderBy(x => GetGroupSortOrder(x.Name))
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddCategoryElements(
            Document doc,
            View view,
            BuiltInCategory category,
            List<Element> target)
        {
            IList<Element> found = new FilteredElementCollector(doc, view.Id)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToElements();

            target.AddRange(found);
        }

        private static string GetFamilyName(Element element)
        {
            FamilyInstance fi = element as FamilyInstance;
            if (fi != null && fi.Symbol != null)
            {
                string family = fi.Symbol.FamilyName;
                if (!string.IsNullOrWhiteSpace(family))
                    return family.Trim();
            }

            Element type = element.Document.GetElement(element.GetTypeId());
            if (type != null)
            {
                Parameter familyParam = type.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME);
                string family = ReadParameter(familyParam);
                if (!string.IsNullOrWhiteSpace(family))
                    return family.Trim();
            }

            if (element.Category != null && !string.IsNullOrWhiteSpace(element.Category.Name))
                return element.Category.Name.Trim();

            return "Component";
        }

        private static string GetTypeName(Element element)
        {
            FamilyInstance fi = element as FamilyInstance;
            if (fi != null && fi.Symbol != null && !string.IsNullOrWhiteSpace(fi.Symbol.Name))
                return fi.Symbol.Name.Trim();

            Element type = element.Document.GetElement(element.GetTypeId());
            if (type != null && !string.IsNullOrWhiteSpace(type.Name))
                return type.Name.Trim();

            string builtInType = ReadParameter(
                element.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME));

            if (!string.IsNullOrWhiteSpace(builtInType))
                return builtInType.Trim();

            return element.Name ?? "";
        }

        private static string GetDescription(Element element)
        {
            string[] names =
            {
                "BOM Description",
                "Procurement Description",
                "Description"
            };

            foreach (string name in names)
            {
                string value = ReadNamedParameterInstanceOrType(element, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private static string GetSizeText(Element element)
        {
            string value = ReadParameter(
                element.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE));

            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

            Pipe pipe = element as Pipe;
            if (pipe != null)
            {
                value = ReadParameter(
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM));

                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            string[] names =
            {
                "Size",
                "Nominal Diameter",
                "Diameter",
                "DN"
            };

            foreach (string name in names)
            {
                value = ReadNamedParameterInstanceOrType(element, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private static string GetGroupName(
            Element element,
            string familyName,
            string typeName,
            string description)
        {
            if (IsCategory(element, BuiltInCategory.OST_PipeCurves))
            {
                return "PIPE";
            }

            string key = string.Join(
                " ",
                new[]
                {
                    familyName ?? "",
                    typeName ?? "",
                    description ?? ""
                }).ToUpperInvariant();

            if (IsCategory(element, BuiltInCategory.OST_PipeFitting))
            {
                if (ContainsAny(key, "SHAPED BRANCH", "WELDOLET", "OLET"))
                    return "SHAPED BRANCH";

                if (ContainsAny(key, "WELD"))
                    return "WELD";

                if (ContainsAny(key, "ELBOW"))
                    return "ELBOW";

                if (ContainsAny(key, "END CAP", "ENDCAP", "CAP"))
                    return "END CAP";

                if (ContainsAny(key, "FLANGE"))
                    return "FLANGE";

                if (ContainsAny(key, "REDUCER", "REDUCTION"))
                    return "REDUCER";

                if (ContainsAny(key, "COUPLING", "SOCKET"))
                    return "COUPLING";

                if (ContainsAny(key, "TEE", "T-E"))
                    return "TEE";

                if (ContainsAny(key, "BRANCH"))
                    return "SHAPED BRANCH";

                return "OTHER FITTING";
            }

            if (ContainsAny(key, "FLOW METER", "FLOWMETER", "7ME6580"))
                return "FLOW METER";

            if (ContainsAny(key, "VALVE", "BUTTERFLY"))
                return "VALVE";

            if (ContainsAny(key, "STRAINER"))
                return "STRAINER";

            if (ContainsAny(key, "GAUGE"))
                return "GAUGE";

            if (ContainsAny(key, "INSTRUMENT"))
                return "INSTRUMENT";

            if (ContainsAny(key, "FLANGE", "TRI-CLAMP", "TRICLAMP", "TRI CLAMP", "TRI-CLOVER", "TRICLOVER"))
                return "PIPE ACCESSORY - FLANGE / CLAMP";

            return "PIPE ACCESSORY";
        }

        private static bool IsCategory(Element element, BuiltInCategory category)
        {
            if (element == null || element.Category == null)
                return false;

            return RevitApiCompatibility.GetElementIdValue(element.Category.Id) == (long)category;
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            foreach (string value in values)
            {
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static int GetGroupSortOrder(string group)
        {
            switch ((group ?? "").ToUpperInvariant())
            {
                case "PIPE": return 0;
                case "ELBOW": return 10;
                case "COUPLING": return 20;
                case "TEE": return 30;
                case "REDUCER": return 40;
                case "END CAP": return 50;
                case "FLANGE": return 60;
                case "SHAPED BRANCH": return 70;
                case "WELD": return 80;
                case "VALVE": return 90;
                case "FLOW METER": return 100;
                case "STRAINER": return 110;
                case "GAUGE": return 120;
                case "INSTRUMENT": return 130;
                case "PIPE ACCESSORY - FLANGE / CLAMP": return 140;
                case "PIPE ACCESSORY": return 150;
                case "OTHER FITTING": return 160;
                default: return 999;
            }
        }

        private static string BuildDisplayName(FilterItemRecord record)
        {
            var parts = new List<string>();

            string identity = record.FamilyName;
            if (!string.IsNullOrWhiteSpace(record.TypeName) &&
                !string.Equals(record.FamilyName, record.TypeName, StringComparison.OrdinalIgnoreCase))
            {
                identity = string.IsNullOrWhiteSpace(identity)
                    ? record.TypeName
                    : identity + " - " + record.TypeName;
            }

            if (!string.IsNullOrWhiteSpace(identity))
                parts.Add(identity);

            if (!string.IsNullOrWhiteSpace(record.Size))
                parts.Add("Size: " + record.Size);

            if (!string.IsNullOrWhiteSpace(record.Description) &&
                !ContainsIgnoreCase(identity, record.Description) &&
                !ContainsIgnoreCase(record.Description, record.TypeName))
            {
                parts.Add(record.Description);
            }

            return parts.Count > 0
                ? string.Join("  |  ", parts)
                : "Component";
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(value))
                return false;

            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadNamedParameterInstanceOrType(Element element, string name)
        {
            if (element == null || string.IsNullOrWhiteSpace(name))
                return "";

            string value = ReadParameter(element.LookupParameter(name));
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            ElementId typeId = element.GetTypeId();
            if (typeId == ElementId.InvalidElementId)
                return "";

            Element type = element.Document.GetElement(typeId);
            return type != null ? ReadParameter(type.LookupParameter(name)) : "";
        }

        private static string ReadParameter(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
                return "";

            if (parameter.StorageType == StorageType.String)
                return parameter.AsString() ?? "";

            string formatted = parameter.AsValueString();
            if (!string.IsNullOrWhiteSpace(formatted))
                return formatted;

            if (parameter.StorageType == StorageType.Integer)
                return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);

            if (parameter.StorageType == StorageType.Double)
                return parameter.AsDouble().ToString("0.###", CultureInfo.InvariantCulture);

            if (parameter.StorageType == StorageType.ElementId)
                return RevitApiCompatibility.GetElementIdValue(parameter.AsElementId())
                    .ToString(CultureInfo.InvariantCulture);

            return "";
        }

        private static string NormalizeKey(string value)
        {
            return (value ?? "").Trim().ToUpperInvariant();
        }

        private sealed class FilterItemRecord
        {
            public ElementId ElementId { get; set; }
            public string GroupName { get; set; }
            public long TypeId { get; set; }
            public string FamilyName { get; set; }
            public string TypeName { get; set; }
            public string Size { get; set; }
            public string Description { get; set; }
        }
    }

    internal sealed class FilterItemsExternalEventHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private Document _document;
        private ElementId _viewId = ElementId.InvalidElementId;
        private List<ElementId> _hiddenIds = new List<ElementId>();

        public void Configure(Document document, ElementId viewId)
        {
            lock (_sync)
            {
                _document = document;
                _viewId = viewId;
                _hiddenIds = new List<ElementId>();
            }
        }

        public void SetHiddenIds(IEnumerable<ElementId> hiddenIds)
        {
            lock (_sync)
            {
                _hiddenIds = hiddenIds == null
                    ? new List<ElementId>()
                    : hiddenIds.Distinct().ToList();
            }
        }

        public void Execute(UIApplication app)
        {
            Document doc;
            ElementId viewId;
            List<ElementId> hiddenIds;

            lock (_sync)
            {
                doc = _document;
                viewId = _viewId;
                hiddenIds = new List<ElementId>(_hiddenIds);
            }

            if (doc == null || viewId == ElementId.InvalidElementId)
                return;

            try
            {
                View view = doc.GetElement(viewId) as View;
                if (view == null)
                    return;

                using (Transaction tx = new Transaction(doc, "Filter BOM Items"))
                {
                    tx.Start();

                    // Rebuild the tool-owned temporary hidden set from scratch.
                    // Revit does not provide a reliable per-item temporary-unhide workflow,
                    // so this keeps checkbox state deterministic.
                    if (view.IsTemporaryHideIsolateActive())
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

                    if (hiddenIds.Count > 0)
                        view.HideElementsTemporary(hiddenIds);

                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                AppDialog.Error(app, "Filter Items", "Unable to update view visibility.\n\n" + ex.Message);
            }
        }

        public string GetName()
        {
            return "Parallel Systems - Filter BOM Items";
        }
    }
}
