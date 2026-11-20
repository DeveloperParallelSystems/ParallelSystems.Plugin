using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using ParallelSystemsPlugin.Compatibility;

namespace ParallelSystemsPlugin.LiveSession
{
    // Called exclusively inside IExternalEventHandler.Execute on Revit's UI thread.
    internal sealed class LiveSessionCommands
    {
        private readonly DynamoLiveSession _dynamo = new DynamoLiveSession();
        private readonly LiveDocumentRegistry<Document> _documents = new LiveDocumentRegistry<Document>(doc => doc.IsValidObject);
        private string DocumentId(Document doc) => _documents.GetId(doc);

        internal JObject Execute(UIApplication app, JObject request)
        {
            string command = (string)request["command"];
            var args = request["args"] as JObject ?? new JObject();
            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc?.Document;
            object result;
            switch (command)
            {
                case "session.info":
                    result = new
                    {
                        protocolVersion = 1,
                        revitVersion = app.Application.VersionNumber,
                        activeDocument = doc == null ? null : DescribeDocument(doc),
                        commands = new[] { "session.info", "documents.list", "views.list", "selection.get",
                            "elements.query", "elements.get", "selection.set", "elements.focus", "view.activate", "parameter.setText",
                            "dynamo.status", "dynamo.inspect", "dynamo.open", "dynamo.workspace", "dynamo.run", "dynamo.result" }
                    };
                    break;
                case "documents.list":
                    result = app.Application.Documents.Cast<Document>().Select(DescribeDocument).ToArray();
                    break;
                case "dynamo.status":
                    result = _dynamo.Status();
                    break;
                case "dynamo.inspect":
                    result = DynamoGraphFile.Read((string)args["path"]).Describe();
                    break;
                case "dynamo.result":
                    result = _dynamo.Result((string)args["runId"]);
                    break;
                default:
                    if (doc == null) throw new InvalidOperationException("No document is active in Revit.");
                    if (!string.Equals((string)args["documentId"], DocumentId(doc), StringComparison.Ordinal))
                        throw new InvalidOperationException("The active document changed or documentId is missing. Query session.info again.");
                    result = ExecuteDocument(uiDoc, command, args);
                    break;
            }
            return new JObject { ["ok"] = true, ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result) };
        }

        private object DescribeDocument(Document doc) => new
        {
            documentId = DocumentId(doc), title = doc.Title, isFamilyDocument = doc.IsFamilyDocument,
            isReadOnly = doc.IsReadOnly, isLinked = doc.IsLinked,
            activeView = doc.ActiveView == null ? null : DescribeElement(doc.ActiveView)
        };

        private object ExecuteDocument(UIDocument uiDoc, string command, JObject args)
        {
            var doc = uiDoc.Document;
            switch (command)
            {
                case "dynamo.open":
                    return _dynamo.Open(args);
                case "dynamo.workspace":
                    return _dynamo.Workspace();
                case "dynamo.run":
                    if (doc.IsReadOnly || doc.IsModifiable)
                        throw new InvalidOperationException("Document is not available for Dynamo-owned transactions.");
                    DynamoReflection.RequireDocument(doc);
                    return _dynamo.Run(args, DocumentId(doc), RevitApiCompatibility.GetElementIdValue(doc.ActiveView.Id).ToString());
                case "views.list":
                    using (var collector = new FilteredElementCollector(doc).OfClass(typeof(View)))
                        return Page(collector.Cast<View>().Where(v => !v.IsTemplate).Cast<Element>(), args);
                case "selection.get":
                    return uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null)
                        .Take(200).Select(DescribeElement).ToArray();
                case "elements.query":
                    using (var collector = new FilteredElementCollector(doc))
                    {
                        collector.WhereElementIsNotElementType();
                        string categoryName = (string)args["category"];
                        if (!string.IsNullOrEmpty(categoryName))
                        {
                            BuiltInCategory category;
                            if (!Enum.TryParse(categoryName, out category) || !Enum.IsDefined(typeof(BuiltInCategory), category))
                                throw new ArgumentException("category must be a BuiltInCategory name, for example OST_Walls.");
                            collector.OfCategory(category);
                        }
                        string name = (string)args["nameContains"];
                        return Page(collector.Where(e => string.IsNullOrEmpty(name) ||
                            (e.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0), args);
                    }
                case "elements.get":
                    var element = RequireElement(doc, args["elementId"]);
                    return new
                    {
                        element = DescribeElement(element),
                        parameters = element.Parameters.Cast<Parameter>().Take(300).Select(p => new
                        {
                            name = p.Definition.Name, storageType = p.StorageType.ToString(),
                            isReadOnly = p.IsReadOnly, value = ParameterValue(p), displayValue = p.AsValueString()
                        }).ToArray()
                    };
                case "selection.set":
                case "elements.focus":
                    var input = args["elementIds"] as JArray;
                    if (input == null || input.Count > 200) throw new ArgumentException("elementIds must contain at most 200 IDs.");
                    var ids = input.Select(id => RequireElement(doc, id).Id).Distinct().ToList();
                    if (command == "elements.focus" && ids.Count == 0) throw new ArgumentException("Provide at least one element to focus.");
                    uiDoc.Selection.SetElementIds(ids);
                    if (command == "elements.focus") uiDoc.ShowElements(ids);
                    return new { count = ids.Count };
                case "view.activate":
                    var view = RequireElement(doc, args["viewId"]) as View;
                    if (view == null || view.IsTemplate) throw new ArgumentException("viewId must identify a non-template view.");
                    uiDoc.ActiveView = view;
                    return DescribeElement(view);
                case "parameter.setText":
                    return SetText(doc, args);
                default:
                    throw new ArgumentException("Unknown command. Query session.info for supported commands.");
            }
        }

        private static object Page(System.Collections.Generic.IEnumerable<Element> elements, JObject args)
        {
            int offset = (int?)args["offset"] ?? 0;
            int limit = (int?)args["limit"] ?? 100;
            if (offset < 0 || offset > 1000000 || limit < 1 || limit > 200)
                throw new ArgumentException("offset must be 0–1000000; limit must be 1–200.");
            var page = elements.Skip(offset).Take(limit + 1).ToArray();
            return new { items = page.Take(limit).Select(DescribeElement).ToArray(),
                nextOffset = page.Length > limit ? (int?)(offset + limit) : null };
        }

        private static object DescribeElement(Element element) => new
        {
            elementId = RevitApiCompatibility.GetElementIdValue(element.Id).ToString(System.Globalization.CultureInfo.InvariantCulture),
            uniqueId = element.UniqueId, name = element.Name,
            category = element.Category?.Name, type = element.GetType().Name
        };

        private static Element RequireElement(Document doc, JToken value)
        {
            long id;
            if (value == null || !long.TryParse(value.ToString(), out id) || id <= 0)
                throw new ArgumentException("A positive element ID is required.");
#if REVIT2024_OR_GREATER
            var elementId = new ElementId(id);
#else
            var elementId = new ElementId(checked((int)id));
#endif
            return doc.GetElement(elementId) ?? throw new ArgumentException("Element does not exist in the active document.");
        }

        private static object ParameterValue(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String: return parameter.AsString();
                case StorageType.Integer: return parameter.AsInteger();
                case StorageType.Double: return parameter.AsDouble(); // Revit internal units.
                case StorageType.ElementId: return RevitApiCompatibility.GetElementIdValue(parameter.AsElementId()).ToString();
                default: return null;
            }
        }

        private static object SetText(Document doc, JObject args)
        {
            if (doc.IsReadOnly || doc.IsModifiable) throw new InvalidOperationException("Document is not available for a new transaction.");
            var element = RequireElement(doc, args["elementId"]);
            string name = (string)args["parameterName"];
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("parameterName is required.");
            var matches = element.GetParameters(name);
            if (matches.Count != 1) throw new ArgumentException("Parameter name must identify exactly one parameter.");
            var parameter = matches[0];
            if (parameter.StorageType != StorageType.String || parameter.IsReadOnly)
                throw new ArgumentException("Only writable text parameters are supported.");
            var expected = args.Property("expectedValue");
            if (expected == null || (expected.Value.Type != JTokenType.String && expected.Value.Type != JTokenType.Null))
                throw new ArgumentException("expectedValue must be the current text (or null), obtained with elements.get.");
            if (!string.Equals(parameter.AsString(), (string)expected.Value, StringComparison.Ordinal))
                throw new InvalidOperationException("Parameter changed since it was read. Read the element again.");
            if (args["value"]?.Type != JTokenType.String) throw new ArgumentException("value must be text.");
            string value = (string)args["value"];
            using (var transaction = new Transaction(doc, "ParallelSystems Live Session: set text"))
            {
                transaction.Start();
                var options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(new RollBackFailures());
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);
                if (!parameter.Set(value)) throw new InvalidOperationException("Revit rejected the parameter value.");
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit rolled back the edit because of model failures.");
            }
            return new { elementId = (string)args["elementId"], parameterName = name, value };
        }

        private sealed class RollBackFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor) =>
                failuresAccessor.GetFailureMessages().Count > 0
                    ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
