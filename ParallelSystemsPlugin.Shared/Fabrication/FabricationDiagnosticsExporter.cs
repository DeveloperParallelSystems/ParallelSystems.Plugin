using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Newtonsoft.Json;
using ParallelSystemsPlugin.Compatibility;
using ParallelSystemPlugin.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ParallelSystemsPlugin.Fabrication
{
    internal sealed class FabricationDiagnosticsSelection
    {
        public IList<ElementId> RawSelectedElementIds { get; set; } =
            new List<ElementId>();

        public IList<ElementId> SourceElementIds { get; set; } =
            new List<ElementId>();

        public IList<ElementId> CalculationContextElementIds { get; set; } =
            new List<ElementId>();

        public IDictionary<ElementId, ElementId>
            ExplicitHeaderPipeIdsByBranch { get; set; } =
                new Dictionary<ElementId, ElementId>();

        public IList<ElementId> AssemblyElementIds { get; set; } =
            new List<ElementId>();
    }

    internal sealed class FabricationDiagnosticsExportResult
    {
        public string FilePath { get; set; }
        public int SelectedElementCount { get; set; }
        public int ContextElementCount { get; set; }
        public int ConnectionCount { get; set; }
        public bool ContextTruncated { get; set; }
    }

    /// <summary>
    /// Developer-only, read-only exporter for the exact Revit data required to
    /// diagnose fabrication geometry and connector rules. The exporter writes
    /// one compact JSON file and never modifies the Revit document.
    /// </summary>
    internal static class FabricationDiagnosticsExporter
    {
        private const double FeetToMillimetres = 304.8;
        private const int ConnectionContextDepth = 3;
        private const int AdditionalContextElementLimit = 2000;
        private const int MaximumGeometryRecursionDepth = 8;

        public static FabricationDiagnosticsSelection CollectSelection(
            UIDocument uiDoc)
        {
            if (uiDoc == null)
                return null;

            Document doc = uiDoc.Document;

            ICollection<ElementId> preselectedIds =
                uiDoc.Selection.GetElementIds();

            List<Element> rawElements = preselectedIds
                .Select(doc.GetElement)
                .Where(IsSelectableDiagnosticElement)
                .ToList();

            if (rawElements.Count == 0)
            {
                IList<Reference> picked = uiDoc.Selection.PickObjects(
                    ObjectType.Element,
                    new DiagnosticsSelectionFilter(),
                    "Select fabrication components or assemblies to export for developer diagnostics.");

                rawElements = picked
                    .Select(x => doc.GetElement(x.ElementId))
                    .Where(IsSelectableDiagnosticElement)
                    .ToList();
            }

            HashSet<ElementId> sourceIds = new HashSet<ElementId>();
            HashSet<ElementId> assemblyIds = new HashSet<ElementId>();

            foreach (Element element in rawElements)
            {
                AssemblyInstance assembly = element as AssemblyInstance;

                if (assembly != null)
                {
                    assemblyIds.Add(assembly.Id);

                    foreach (ElementId memberId in assembly.GetMemberIds())
                    {
                        Element member = doc.GetElement(memberId);

                        if (IsDiagnosticModelElement(member))
                            sourceIds.Add(memberId);
                    }

                    continue;
                }

                if (IsDiagnosticModelElement(element))
                    sourceIds.Add(element.Id);
            }

            if (sourceIds.Count == 0)
            {
                AppDialog.Warn(
                    "Export Fabrication Diagnostics",
                    "No model components were selected.\n\n" +
                    "Select one or more fabrication components or Revit assemblies.");

                return null;
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
                    FabricationStepService
                        .IsShapedBranchForDiagnostics(doc, x))
                .ToList();

            bool selectedScopeContainsPipe = sourceIds
                .Select(doc.GetElement)
                .Any(x => x is Pipe);

            // Mirror the STEP command's branch-only header workflow so the
            // deep diagnostic probe receives the exact same calculation
            // context as production generation. Assembly diagnostics normally
            // skip this prompt because the header pipe is already in scope.
            if (shapedBranches.Count > 0 &&
                !selectedScopeContainsPipe)
            {
                foreach (Element branch in shapedBranches)
                {
                    int choice = AppDialog.Choose(
                        "Fabrication Diagnostics - Header Pipe",
                        "A shaped branch was selected without a main/header pipe.",
                        "For an exact STEP geometry probe, choose the same header " +
                        "pipe you would use during Fabrication STEP generation. " +
                        "The pipe is read-only diagnostic context and is not " +
                        "treated as an exported source component.",
                        new List<string>
                        {
                            "Select Header Pipe",
                            "Search Automatically"
                        },
                        0);

                    if (choice < 0)
                        return null;

                    if (choice == 1)
                        continue;

                    Reference pickedHeader =
                        uiDoc.Selection.PickObject(
                            ObjectType.Element,
                            new DiagnosticsHeaderPipeSelectionFilter(),
                            "Select the main/header pipe for the shaped branch.");

                    Pipe headerPipe =
                        doc.GetElement(pickedHeader.ElementId) as Pipe;

                    if (headerPipe == null)
                        return null;

                    calculationContextIds.Add(headerPipe.Id);
                    explicitHeaderPipeIdsByBranch[branch.Id] = headerPipe.Id;
                }
            }

            return new FabricationDiagnosticsSelection
            {
                RawSelectedElementIds = rawElements
                    .Select(x => x.Id)
                    .Distinct()
                    .ToList(),
                SourceElementIds = sourceIds.ToList(),
                CalculationContextElementIds =
                    calculationContextIds.ToList(),
                ExplicitHeaderPipeIdsByBranch =
                    explicitHeaderPipeIdsByBranch,
                AssemblyElementIds = assemblyIds.ToList()
            };
        }

        public static string BuildSuggestedFileName(Document doc)
        {
            string contextName = ResolveOpenedSheetName(doc);

            if (string.IsNullOrWhiteSpace(contextName))
                contextName = doc?.ActiveView?.Name;

            if (string.IsNullOrWhiteSpace(contextName))
                contextName = doc?.Title;

            if (string.IsNullOrWhiteSpace(contextName))
                contextName = "Fabrication";

            return SanitizeFileName(
                contextName + "_FabricationDiagnostics.json");
        }

        public static FabricationDiagnosticsExportResult Export(
            UIApplication uiApp,
            FabricationDiagnosticsSelection selection,
            string outputPath)
        {
            if (uiApp == null)
                throw new ArgumentNullException(nameof(uiApp));

            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc?.Document;

            if (doc == null)
                throw new InvalidOperationException(
                    "No active Revit document is available.");

            if (selection == null ||
                selection.SourceElementIds == null ||
                selection.SourceElementIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "No fabrication diagnostic selection is available.");
            }

            string finalPath = EnsureJsonExtension(outputPath);
            string directory = Path.GetDirectoryName(finalPath);

            if (string.IsNullOrWhiteSpace(directory) ||
                !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "The selected diagnostics output folder does not exist.");
            }

            DiagnosticContextCollection context = BuildContextCollection(
                doc,
                selection.SourceElementIds,
                selection.CalculationContextElementIds);

            string pendingPath = Path.Combine(
                directory,
                "." + Path.GetFileName(finalPath) + "." +
                Guid.NewGuid().ToString("N") + ".pending");

            try
            {
                WriteDiagnosticJson(
                    uiApp,
                    selection,
                    context,
                    pendingPath);

                if (!File.Exists(pendingPath) ||
                    new FileInfo(pendingPath).Length == 0)
                {
                    throw new IOException(
                        "The diagnostics JSON file was not created or is empty.");
                }

                CommitFile(pendingPath, finalPath);
            }
            finally
            {
                TryDeleteFile(pendingPath);
            }

            return new FabricationDiagnosticsExportResult
            {
                FilePath = finalPath,
                SelectedElementCount = context.Elements.Count(x => x.IsSelected),
                ContextElementCount = context.Elements.Count(x => !x.IsSelected),
                ConnectionCount = context.Connections.Count,
                ContextTruncated = context.ContextTruncated
            };
        }

        private static DiagnosticContextCollection BuildContextCollection(
            Document doc,
            IEnumerable<ElementId> selectedIds,
            IEnumerable<ElementId> calculationContextIds)
        {
            Dictionary<long, DiagnosticElementContext> contexts =
                new Dictionary<long, DiagnosticElementContext>();

            Queue<DiagnosticElementContext> queue =
                new Queue<DiagnosticElementContext>();

            foreach (ElementId selectedId in selectedIds
                         .Where(x => x != null)
                         .Distinct())
            {
                Element element = doc.GetElement(selectedId);

                if (element == null)
                    continue;

                long value = RevitApiCompatibility.GetElementIdValue(
                    element.Id);

                if (contexts.ContainsKey(value))
                    continue;

                DiagnosticElementContext context =
                    new DiagnosticElementContext
                    {
                        Element = element,
                        IsSelected = true,
                        ConnectionDepth = 0
                    };

                contexts[value] = context;
                queue.Enqueue(context);
            }

            foreach (ElementId contextId in
                     (calculationContextIds ?? Enumerable.Empty<ElementId>())
                         .Where(x => x != null)
                         .Distinct())
            {
                Element element = doc.GetElement(contextId);
                if (element == null)
                    continue;

                long value = RevitApiCompatibility.GetElementIdValue(
                    element.Id);

                if (contexts.ContainsKey(value))
                    continue;

                DiagnosticElementContext context =
                    new DiagnosticElementContext
                    {
                        Element = element,
                        IsSelected = false,
                        ConnectionDepth = 0
                    };

                contexts[value] = context;
                queue.Enqueue(context);
            }

            int seededContextCount = contexts.Count;
            int maximumContextCount =
                seededContextCount + AdditionalContextElementLimit;

            bool truncated = false;

            while (queue.Count > 0)
            {
                DiagnosticElementContext current = queue.Dequeue();

                if (current.ConnectionDepth >= ConnectionContextDepth)
                    continue;

                foreach (Connector connector in GetConnectors(current.Element))
                {
                    foreach (Element connected in GetConnectedOwners(connector))
                    {
                        if (connected == null ||
                            connected.Document != doc ||
                            connected.Id.Equals(current.Element.Id))
                        {
                            continue;
                        }

                        long connectedValue =
                            RevitApiCompatibility.GetElementIdValue(
                                connected.Id);

                        DiagnosticElementContext existing;

                        if (contexts.TryGetValue(
                                connectedValue,
                                out existing))
                        {
                            if (!existing.IsSelected &&
                                existing.ConnectionDepth >
                                current.ConnectionDepth + 1)
                            {
                                existing.ConnectionDepth =
                                    current.ConnectionDepth + 1;
                            }

                            continue;
                        }

                        if (contexts.Count >= maximumContextCount)
                        {
                            truncated = true;
                            continue;
                        }

                        DiagnosticElementContext added =
                            new DiagnosticElementContext
                            {
                                Element = connected,
                                IsSelected = false,
                                ConnectionDepth =
                                    current.ConnectionDepth + 1
                            };

                        contexts[connectedValue] = added;
                        queue.Enqueue(added);
                    }
                }
            }

            List<DiagnosticConnection> connections =
                BuildConnections(contexts.Values);

            return new DiagnosticContextCollection
            {
                Elements = contexts.Values
                    .OrderByDescending(x => x.IsSelected)
                    .ThenBy(x => x.ConnectionDepth)
                    .ThenBy(x =>
                        RevitApiCompatibility.GetElementIdValue(
                            x.Element.Id))
                    .ToList(),
                Connections = connections,
                ContextTruncated = truncated
            };
        }

        private static List<DiagnosticConnection> BuildConnections(
            IEnumerable<DiagnosticElementContext> contexts)
        {
            Dictionary<long, DiagnosticElementContext> contextById =
                contexts.ToDictionary(
                    x => RevitApiCompatibility.GetElementIdValue(
                        x.Element.Id));

            Dictionary<string, DiagnosticConnection> unique =
                new Dictionary<string, DiagnosticConnection>(
                    StringComparer.Ordinal);

            foreach (DiagnosticElementContext context in contexts)
            {
                IList<Connector> connectors =
                    GetConnectors(context.Element);

                for (int connectorIndex = 0;
                     connectorIndex < connectors.Count;
                     connectorIndex++)
                {
                    Connector connector = connectors[connectorIndex];

                    foreach (Connector reference in GetConnectedReferences(
                                 connector))
                    {
                        Element connectedOwner = reference?.Owner;

                        if (connectedOwner == null ||
                            connectedOwner.Id.Equals(context.Element.Id))
                        {
                            continue;
                        }

                        long sourceId =
                            RevitApiCompatibility.GetElementIdValue(
                                context.Element.Id);

                        long targetId =
                            RevitApiCompatibility.GetElementIdValue(
                                connectedOwner.Id);

                        if (!contextById.ContainsKey(targetId))
                            continue;

                        int connectedIndex = FindConnectorIndex(
                            connectedOwner,
                            reference);

                        string forwardKey =
                            sourceId.ToString(CultureInfo.InvariantCulture) +
                            ":" + connectorIndex.ToString(
                                CultureInfo.InvariantCulture) + "->" +
                            targetId.ToString(CultureInfo.InvariantCulture) +
                            ":" + connectedIndex.ToString(
                                CultureInfo.InvariantCulture);

                        string reverseKey =
                            targetId.ToString(CultureInfo.InvariantCulture) +
                            ":" + connectedIndex.ToString(
                                CultureInfo.InvariantCulture) + "->" +
                            sourceId.ToString(CultureInfo.InvariantCulture) +
                            ":" + connectorIndex.ToString(
                                CultureInfo.InvariantCulture);

                        if (unique.ContainsKey(forwardKey) ||
                            unique.ContainsKey(reverseKey))
                        {
                            continue;
                        }

                        unique[forwardKey] = new DiagnosticConnection
                        {
                            SourceElementId = sourceId,
                            SourceConnectorIndex = connectorIndex,
                            TargetElementId = targetId,
                            TargetConnectorIndex = connectedIndex,
                            SourceSelected = context.IsSelected,
                            TargetSelected = contextById[targetId].IsSelected
                        };
                    }
                }
            }

            return unique.Values
                .OrderBy(x => x.SourceElementId)
                .ThenBy(x => x.SourceConnectorIndex)
                .ThenBy(x => x.TargetElementId)
                .ToList();
        }

        private static void WriteDiagnosticJson(
            UIApplication uiApp,
            FabricationDiagnosticsSelection selection,
            DiagnosticContextCollection context,
            string path)
        {
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            using (FileStream stream = new FileStream(
                       path,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            using (StreamWriter textWriter = new StreamWriter(
                       stream,
                       new UTF8Encoding(false)))
            using (JsonTextWriter writer = new JsonTextWriter(textWriter))
            {
                writer.Formatting = Formatting.None;

                writer.WriteStartObject();

                WriteProperty(writer, "schema",
                    "parallel-systems.fabrication-diagnostics");
                WriteProperty(writer, "schemaVersion", 2);
                WriteProperty(writer, "generatedAtUtc",
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                writer.WritePropertyName("plugin");
                WritePlugin(writer);

                writer.WritePropertyName("revit");
                WriteRevitApplication(writer, uiApp);

                writer.WritePropertyName("document");
                WriteDocument(writer, doc);

                writer.WritePropertyName("activeContext");
                WriteActiveContext(writer, doc);

                writer.WritePropertyName("selection");
                WriteSelection(writer, selection);

                writer.WritePropertyName("exportSettings");
                writer.WriteStartObject();
                WriteProperty(writer, "coordinateInternalUnit", "feet");
                WriteProperty(writer, "coordinateDisplayUnit", "millimetres");
                WriteProperty(writer, "geometryDetailForSelected", "full");
                WriteProperty(writer, "geometryDetailForConnectionContext", "summary");
                WriteProperty(writer, "connectionContextDepth",
                    ConnectionContextDepth);
                WriteProperty(writer, "additionalContextElementLimit",
                    AdditionalContextElementLimit);
                WriteProperty(writer, "contextTruncated",
                    context.ContextTruncated);
                writer.WriteEndObject();

                writer.WritePropertyName("elements");
                writer.WriteStartArray();

                foreach (DiagnosticElementContext item in context.Elements)
                {
                    WriteElement(
                        writer,
                        doc,
                        item.Element,
                        item.IsSelected,
                        item.ConnectionDepth);
                }

                writer.WriteEndArray();

                writer.WritePropertyName("connectionGraph");
                writer.WriteStartArray();

                foreach (DiagnosticConnection connection in
                         context.Connections)
                {
                    writer.WriteStartObject();
                    WriteProperty(writer, "sourceElementId",
                        connection.SourceElementId);
                    WriteProperty(writer, "sourceConnectorIndex",
                        connection.SourceConnectorIndex);
                    WriteProperty(writer, "sourceSelected",
                        connection.SourceSelected);
                    WriteProperty(writer, "targetElementId",
                        connection.TargetElementId);
                    WriteProperty(writer, "targetConnectorIndex",
                        connection.TargetConnectorIndex);
                    WriteProperty(writer, "targetSelected",
                        connection.TargetSelected);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WritePropertyName("stepGeometryProbe");

                try
                {
                    JsonSerializer.CreateDefault().Serialize(
                        writer,
                        FabricationStepService.BuildGeometryDiagnosticProbe(
                            doc,
                            selection.SourceElementIds,
                            selection.CalculationContextElementIds,
                            selection.ExplicitHeaderPipeIdsByBranch));
                }
                catch (Exception ex)
                {
                    // Diagnostics must remain exportable even if the deep STEP
                    // probe itself encounters an unexpected API edge case.
                    writer.WriteStartObject();
                    WriteProperty(writer, "probeVersion", "2");
                    WriteProperty(writer, "isReadOnly", true);
                    WriteProperty(writer, "probeFailed", true);
                    WriteProperty(writer, "probeError", ex.ToString());
                    writer.WriteEndObject();
                }

                writer.WritePropertyName("summary");
                writer.WriteStartObject();
                WriteProperty(writer, "selectedElementCount",
                    context.Elements.Count(x => x.IsSelected));
                WriteProperty(writer, "contextElementCount",
                    context.Elements.Count(x => !x.IsSelected));
                WriteProperty(writer, "connectionCount",
                    context.Connections.Count);
                writer.WriteEndObject();

                writer.WriteEndObject();
                writer.Flush();
            }
        }

        private static void WritePlugin(JsonTextWriter writer)
        {
            Assembly assembly = typeof(App).Assembly;
            AssemblyName name = assembly.GetName();

            writer.WriteStartObject();
            WriteProperty(writer, "assemblyName", name.Name);
            WriteProperty(writer, "assemblyVersion",
                name.Version?.ToString());

            string informationalVersion = null;

            try
            {
                AssemblyInformationalVersionAttribute attribute =
                    assembly.GetCustomAttributes(
                            typeof(AssemblyInformationalVersionAttribute),
                            false)
                        .OfType<AssemblyInformationalVersionAttribute>()
                        .FirstOrDefault();

                informationalVersion = attribute?.InformationalVersion;
            }
            catch
            {
                // Optional metadata only.
            }

            WriteProperty(writer, "informationalVersion",
                informationalVersion);
            WriteProperty(writer, "assemblyLocation",
                assembly.Location);

            try
            {
                WriteProperty(
                    writer,
                    "moduleVersionId",
                    assembly.ManifestModule.ModuleVersionId.ToString("D"));
            }
            catch
            {
                WriteProperty(writer, "moduleVersionId", null);
            }

            try
            {
                FileInfo file = new FileInfo(assembly.Location);
                WriteProperty(writer, "assemblyFileLengthBytes", file.Length);
                WriteProperty(
                    writer,
                    "assemblyFileLastWriteUtc",
                    file.LastWriteTimeUtc.ToString(
                        "o",
                        CultureInfo.InvariantCulture));
                WriteProperty(
                    writer,
                    "assemblySha256",
                    ComputeFileSha256(assembly.Location));
            }
            catch
            {
                WriteProperty(writer, "assemblyFileLengthBytes", -1L);
                WriteProperty(writer, "assemblyFileLastWriteUtc", null);
                WriteProperty(writer, "assemblySha256", null);
            }

            writer.WriteEndObject();
        }

        private static void WriteRevitApplication(
            JsonTextWriter writer,
            UIApplication uiApp)
        {
            object application = uiApp.Application;

            writer.WriteStartObject();
            WriteProperty(writer, "versionName",
                ReadPropertyAsString(application, "VersionName"));
            WriteProperty(writer, "versionNumber",
                ReadPropertyAsString(application, "VersionNumber"));
            WriteProperty(writer, "versionBuild",
                ReadPropertyAsString(application, "VersionBuild"));
            WriteProperty(writer, "subVersionNumber",
                ReadPropertyAsString(application, "SubVersionNumber"));
            WriteProperty(writer, "username",
                ReadPropertyAsString(application, "Username"));
            WriteProperty(
                writer,
                "shortCurveToleranceFeet",
                uiApp.Application.ShortCurveTolerance);
            WriteProperty(
                writer,
                "shortCurveToleranceMillimetres",
                uiApp.Application.ShortCurveTolerance *
                    FeetToMillimetres);
            writer.WriteEndObject();
        }

        private static void WriteDocument(
            JsonTextWriter writer,
            Document doc)
        {
            writer.WriteStartObject();
            WriteProperty(writer, "title", doc.Title);
            WriteProperty(writer, "pathName", doc.PathName);
            WriteProperty(writer, "isWorkshared", doc.IsWorkshared);
            WriteProperty(writer, "isReadOnly", doc.IsReadOnly);
            WriteProperty(writer, "isFamilyDocument", doc.IsFamilyDocument);
            WriteProperty(writer, "isModified", doc.IsModified);
            WriteProperty(writer, "activeViewId",
                GetIdValue(doc.ActiveView?.Id));
            WriteProperty(writer, "activeViewName",
                doc.ActiveView?.Name);
            writer.WriteEndObject();
        }

        private static void WriteActiveContext(
            JsonTextWriter writer,
            Document doc)
        {
            View activeView = doc.ActiveView;
            IList<ViewSheet> sheets = ResolveOpenedSheets(doc);

            writer.WriteStartObject();
            WriteProperty(writer, "activeViewId", GetIdValue(activeView?.Id));
            WriteProperty(writer, "activeViewName", activeView?.Name);
            WriteProperty(writer, "activeViewType",
                activeView?.ViewType.ToString());

            writer.WritePropertyName("openedSheetCandidates");
            writer.WriteStartArray();

            foreach (ViewSheet sheet in sheets)
            {
                writer.WriteStartObject();
                WriteProperty(writer, "elementId", GetIdValue(sheet.Id));
                WriteProperty(writer, "sheetNumber", sheet.SheetNumber);
                WriteProperty(writer, "sheetName", sheet.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static void WriteSelection(
            JsonTextWriter writer,
            FabricationDiagnosticsSelection selection)
        {
            writer.WriteStartObject();

            WriteElementIdArray(
                writer,
                "rawSelectedElementIds",
                selection.RawSelectedElementIds);

            WriteElementIdArray(
                writer,
                "expandedSourceElementIds",
                selection.SourceElementIds);

            WriteElementIdArray(
                writer,
                "calculationContextElementIds",
                selection.CalculationContextElementIds);

            writer.WritePropertyName("explicitHeaderPipeIdsByBranch");
            writer.WriteStartArray();

            foreach (KeyValuePair<ElementId, ElementId> pair in
                     selection.ExplicitHeaderPipeIdsByBranch ??
                         new Dictionary<ElementId, ElementId>())
            {
                writer.WriteStartObject();
                WriteProperty(writer, "branchElementId", GetIdValue(pair.Key));
                WriteProperty(writer, "headerPipeElementId", GetIdValue(pair.Value));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            WriteElementIdArray(
                writer,
                "selectedAssemblyElementIds",
                selection.AssemblyElementIds);

            writer.WriteEndObject();
        }

        private static void WriteElement(
            JsonTextWriter writer,
            Document doc,
            Element element,
            bool isSelected,
            int connectionDepth)
        {
            writer.WriteStartObject();

            WriteProperty(writer, "scope",
                isSelected ? "selected" : "connection-context");
            WriteProperty(writer, "connectionDepth", connectionDepth);
            WriteProperty(writer, "elementId", GetIdValue(element.Id));
            WriteProperty(writer, "uniqueId", element.UniqueId);
            WriteProperty(writer, "runtimeType", element.GetType().FullName);
            WriteProperty(writer, "name", element.Name);
            WriteProperty(writer, "categoryId",
                GetIdValue(element.Category?.Id));
            WriteProperty(writer, "categoryName",
                element.Category?.Name);
            WriteProperty(writer, "categoryType",
                element.Category?.CategoryType.ToString());
            WriteProperty(writer, "typeId", GetIdValue(element.GetTypeId()));
            WriteProperty(writer, "assemblyInstanceId",
                GetIdValue(element.AssemblyInstanceId));
            WriteProperty(writer, "groupId", GetIdValue(element.GroupId));
            WriteProperty(writer, "worksetId", GetWorksetIdValue(element.WorksetId));
            WriteProperty(writer, "levelId", ResolveLevelId(element));
            WriteProperty(writer, "createdPhaseId",
                GetIdValue(element.CreatedPhaseId));
            WriteProperty(writer, "demolishedPhaseId",
                GetIdValue(element.DemolishedPhaseId));
            WriteProperty(writer, "pinned", element.Pinned);

            FamilyInstance familyInstance = element as FamilyInstance;

            if (familyInstance?.Symbol != null)
            {
                WriteProperty(writer, "familyName",
                    familyInstance.Symbol.FamilyName);
                WriteProperty(writer, "typeName",
                    familyInstance.Symbol.Name);
                WriteProperty(writer, "symbolId",
                    GetIdValue(familyInstance.Symbol.Id));
                WriteProperty(writer, "superComponentId",
                    GetIdValue(familyInstance.SuperComponent?.Id));

                WriteElementIdArray(
                    writer,
                    "subComponentIds",
                    SafeGetSubComponentIds(familyInstance));
            }
            else
            {
                ElementType type = doc.GetElement(
                    element.GetTypeId()) as ElementType;

                WriteProperty(writer, "familyName",
                    ReadPropertyAsString(type, "FamilyName"));
                WriteProperty(writer, "typeName", type?.Name);
            }

            writer.WritePropertyName("worksharing");
            WriteWorksharing(writer, doc, element);

            writer.WritePropertyName("transform");
            WriteElementTransform(writer, element);

            writer.WritePropertyName("location");
            WriteLocation(writer, element.Location);

            writer.WritePropertyName("boundingBox");
            WriteBoundingBox(writer, SafeGetBoundingBox(element));

            writer.WritePropertyName("materials");
            WriteMaterials(writer, doc, element);

            writer.WritePropertyName("parameters");
            WriteParameters(writer, doc, element);

            writer.WritePropertyName("connectors");
            WriteConnectors(writer, element);

            writer.WritePropertyName("derived");
            WriteDerived(writer, doc, element);

            writer.WritePropertyName("geometry");
            WriteGeometry(writer, doc, element, isSelected);

            writer.WriteEndObject();
        }

        private static void WriteWorksharing(
            JsonTextWriter writer,
            Document doc,
            Element element)
        {
            writer.WriteStartObject();

            if (!doc.IsWorkshared)
            {
                WriteProperty(writer, "applicable", false);
                writer.WriteEndObject();
                return;
            }

            WriteProperty(writer, "applicable", true);

            try
            {
                WriteProperty(writer, "checkoutStatus",
                    WorksharingUtils.GetCheckoutStatus(
                        doc,
                        element.Id).ToString());
            }
            catch (Exception ex)
            {
                WriteProperty(writer, "checkoutStatusError", ex.Message);
            }

            try
            {
                WriteProperty(writer, "modelUpdatesStatus",
                    WorksharingUtils.GetModelUpdatesStatus(
                        doc,
                        element.Id).ToString());
            }
            catch (Exception ex)
            {
                WriteProperty(writer, "modelUpdatesStatusError", ex.Message);
            }

            try
            {
                object tooltip = WorksharingUtils.GetWorksharingTooltipInfo(
                    doc,
                    element.Id);

                WriteProperty(writer, "owner",
                    ReadPropertyAsString(tooltip, "Owner"));
                WriteProperty(writer, "creator",
                    ReadPropertyAsString(tooltip, "Creator"));
                WriteProperty(writer, "lastChangedBy",
                    ReadPropertyAsString(tooltip, "LastChangedBy"));
            }
            catch (Exception ex)
            {
                WriteProperty(writer, "tooltipInfoError", ex.Message);
            }

            writer.WriteEndObject();
        }

        private static void WriteElementTransform(
            JsonTextWriter writer,
            Element element)
        {
            Transform transform = null;

            try
            {
                FamilyInstance familyInstance = element as FamilyInstance;

                if (familyInstance != null)
                    transform = familyInstance.GetTransform();
            }
            catch
            {
                // Not every element has an accessible transform.
            }

            WriteTransform(writer, transform);
        }

        private static void WriteLocation(
            JsonTextWriter writer,
            Location location)
        {
            if (location == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            WriteProperty(writer, "runtimeType", location.GetType().FullName);

            LocationPoint point = location as LocationPoint;

            if (point != null)
            {
                writer.WritePropertyName("point");
                WritePoint(writer, point.Point);
                WriteProperty(writer, "rotationRadians", point.Rotation);
                writer.WriteEndObject();
                return;
            }

            LocationCurve curveLocation = location as LocationCurve;

            if (curveLocation != null)
            {
                writer.WritePropertyName("curve");
                WriteCurve(writer, curveLocation.Curve, true);
                writer.WriteEndObject();
                return;
            }

            writer.WriteEndObject();
        }

        private static void WriteBoundingBox(
            JsonTextWriter writer,
            BoundingBoxXYZ boundingBox)
        {
            if (boundingBox == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("min");
            WritePoint(writer, boundingBox.Min);
            writer.WritePropertyName("max");
            WritePoint(writer, boundingBox.Max);
            writer.WritePropertyName("transform");
            WriteTransform(writer, boundingBox.Transform);
            WriteProperty(writer, "enabled", boundingBox.Enabled);
            writer.WriteEndObject();
        }

        private static void WriteMaterials(
            JsonTextWriter writer,
            Document doc,
            Element element)
        {
            writer.WriteStartArray();

            HashSet<ElementId> materialIds = new HashSet<ElementId>();

            try
            {
                foreach (ElementId id in element.GetMaterialIds(false))
                    materialIds.Add(id);

                foreach (ElementId id in element.GetMaterialIds(true))
                    materialIds.Add(id);
            }
            catch
            {
                // Material data is supplementary.
            }

            foreach (ElementId materialId in materialIds)
            {
                Material material = doc.GetElement(materialId) as Material;

                writer.WriteStartObject();
                WriteProperty(writer, "elementId", GetIdValue(materialId));
                WriteProperty(writer, "name", material?.Name);

                try
                {
                    WriteProperty(writer, "areaInternalSquareFeet",
                        element.GetMaterialArea(materialId, false));
                }
                catch
                {
                    // Optional.
                }

                try
                {
                    WriteProperty(writer, "volumeInternalCubicFeet",
                        element.GetMaterialVolume(materialId));
                }
                catch
                {
                    // Optional.
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteParameters(
            JsonTextWriter writer,
            Document doc,
            Element element)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("instance");
            WriteParameterSet(writer, element.Parameters, "instance");

            ElementType type = doc.GetElement(
                element.GetTypeId()) as ElementType;

            writer.WritePropertyName("type");
            WriteParameterSet(writer, type?.Parameters, "type");

            writer.WriteEndObject();
        }

        private static void WriteParameterSet(
            JsonTextWriter writer,
            ParameterSet parameters,
            string source)
        {
            writer.WriteStartArray();

            if (parameters != null)
            {
                List<Parameter> ordered = parameters
                    .Cast<Parameter>()
                    .Where(x => x != null)
                    .OrderBy(x => x.Definition?.Name)
                    .ThenBy(x => GetIdValue(x.Id))
                    .ToList();

                foreach (Parameter parameter in ordered)
                    WriteParameter(writer, parameter, source);
            }

            writer.WriteEndArray();
        }

        private static void WriteParameter(
            JsonTextWriter writer,
            Parameter parameter,
            string source)
        {
            writer.WriteStartObject();

            string name = parameter.Definition?.Name;
            long idValue = GetIdValue(parameter.Id);

            WriteProperty(writer, "source", source);
            WriteProperty(writer, "parameterId", idValue);
            WriteProperty(writer, "builtInParameter",
                TryGetBuiltInParameterName(idValue));
            WriteProperty(writer, "name", name);
            WriteProperty(writer, "storageType",
                parameter.StorageType.ToString());
            WriteProperty(writer, "isReadOnly", parameter.IsReadOnly);
            WriteProperty(writer, "hasValue", parameter.HasValue);
            WriteProperty(writer, "isShared", parameter.IsShared);

            if (parameter.IsShared)
            {
                try
                {
                    WriteProperty(writer, "sharedGuid",
                        parameter.GUID.ToString("D"));
                }
                catch
                {
                    // Optional shared-parameter metadata.
                }
            }

            try
            {
                WriteProperty(writer, "formattedValue",
                    parameter.AsValueString());
            }
            catch
            {
                // Some parameters do not support AsValueString.
            }

            try
            {
                object unitTypeId = InvokeMethod(
                    parameter,
                    "GetUnitTypeId");

                WriteProperty(writer, "unitTypeId",
                    unitTypeId?.ToString());
            }
            catch
            {
                // Revit-version-dependent metadata.
            }

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.Double:
                        double value = parameter.AsDouble();
                        WriteProperty(writer, "rawDouble", value);

                        if (LooksLikeLengthParameter(name))
                        {
                            WriteProperty(writer, "candidateMillimetres",
                                value * FeetToMillimetres);
                        }
                        break;

                    case StorageType.Integer:
                        WriteProperty(writer, "rawInteger",
                            parameter.AsInteger());
                        break;

                    case StorageType.String:
                        WriteProperty(writer, "rawString",
                            parameter.AsString());
                        break;

                    case StorageType.ElementId:
                        WriteProperty(writer, "rawElementId",
                            GetIdValue(parameter.AsElementId()));
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteProperty(writer, "rawValueError", ex.Message);
            }

            writer.WriteEndObject();
        }

        private static void WriteConnectors(
            JsonTextWriter writer,
            Element element)
        {
            IList<Connector> connectors = GetConnectors(element);

            writer.WriteStartArray();

            for (int index = 0; index < connectors.Count; index++)
            {
                Connector connector = connectors[index];

                writer.WriteStartObject();
                WriteProperty(writer, "index", index);
                WriteProperty(writer, "domain", connector.Domain.ToString());
                WriteProperty(writer, "connectorType",
                    connector.ConnectorType.ToString());
                WriteProperty(writer, "shape", connector.Shape.ToString());
                WriteProperty(writer, "isConnected",
                    SafeGetBool(connector, "IsConnected"));

                writer.WritePropertyName("origin");
                WritePoint(writer, SafeGetConnectorOrigin(connector));

                try
                {
                    WriteProperty(writer, "radiusInternalFeet",
                        connector.Radius);
                    WriteProperty(writer, "radiusMillimetres",
                        connector.Radius * FeetToMillimetres);
                    WriteProperty(writer, "diameterMillimetres",
                        connector.Radius * 2.0 * FeetToMillimetres);
                }
                catch
                {
                    // Non-round connectors may not expose Radius.
                }

                writer.WritePropertyName("coordinateSystem");
                WriteTransform(writer, SafeGetConnectorTransform(connector));

                WriteReflectedConnectorProperties(writer, connector);

                writer.WritePropertyName("mepSystem");
                WriteMepSystem(writer, SafeGetMepSystem(connector));

                writer.WritePropertyName("connectedReferences");
                writer.WriteStartArray();

                foreach (Connector reference in
                         GetConnectedReferences(connector))
                {
                    writer.WriteStartObject();
                    WriteProperty(writer, "ownerElementId",
                        GetIdValue(reference.Owner?.Id));
                    WriteProperty(writer, "ownerUniqueId",
                        reference.Owner?.UniqueId);
                    WriteProperty(writer, "ownerRuntimeType",
                        reference.Owner?.GetType().FullName);
                    WriteProperty(writer, "connectorIndex",
                        FindConnectorIndex(reference.Owner, reference));
                    writer.WritePropertyName("origin");
                    WritePoint(writer, SafeGetConnectorOrigin(reference));
                    WriteProperty(writer, "domain",
                        reference.Domain.ToString());
                    WriteProperty(writer, "connectorType",
                        reference.ConnectorType.ToString());
                    WriteProperty(writer, "shape",
                        reference.Shape.ToString());
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteReflectedConnectorProperties(
            JsonTextWriter writer,
            Connector connector)
        {
            string[] propertyNames =
            {
                "Direction",
                "Flow",
                "AssignedFlow",
                "Coefficient",
                "Demand",
                "PressureDrop",
                "VelocityPressure",
                "PipeSystemType",
                "DuctSystemType",
                "Utility",
                "Angle"
            };

            writer.WritePropertyName("optionalProperties");
            writer.WriteStartObject();

            foreach (string propertyName in propertyNames)
            {
                object value = ReadProperty(connector, propertyName);

                if (value == null)
                    continue;

                WriteProperty(writer, propertyName, value);
            }

            writer.WriteEndObject();
        }

        private static void WriteMepSystem(
            JsonTextWriter writer,
            MEPSystem system)
        {
            if (system == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            WriteProperty(writer, "elementId", GetIdValue(system.Id));
            WriteProperty(writer, "name", system.Name);
            WriteProperty(writer, "runtimeType", system.GetType().FullName);
            WriteProperty(writer, "typeId", GetIdValue(system.GetTypeId()));
            writer.WriteEndObject();
        }

        private static void WriteDerived(
            JsonTextWriter writer,
            Document doc,
            Element element)
        {
            string classification = BuildClassificationText(doc, element);

            writer.WriteStartObject();
            WriteProperty(writer, "classificationText", classification);
            WriteProperty(writer, "likelyFabricationKind",
                ClassifyFabricationKind(classification, element));

            writer.WritePropertyName("dimensionCandidates");
            WriteDimensionCandidates(writer, doc, element);

            writer.WritePropertyName("cylindricalFaces");
            WriteCylinderFaceSummary(writer, element);

            writer.WriteEndObject();
        }

        private static void WriteDimensionCandidates(
            JsonTextWriter writer,
            Document doc,
            Element element)
        {
            List<Parameter> candidates = new List<Parameter>();
            candidates.AddRange(FindDimensionParameters(element.Parameters));

            ElementType type = doc.GetElement(
                element.GetTypeId()) as ElementType;

            if (type != null)
                candidates.AddRange(FindDimensionParameters(type.Parameters));

            writer.WriteStartArray();

            foreach (Parameter parameter in candidates
                         .GroupBy(x =>
                             (x.Definition?.Name ?? string.Empty) + "|" +
                             GetIdValue(x.Id).ToString(
                                 CultureInfo.InvariantCulture))
                         .Select(x => x.First())
                         .OrderBy(x => x.Definition?.Name))
            {
                writer.WriteStartObject();
                WriteProperty(writer, "name", parameter.Definition?.Name);
                WriteProperty(writer, "parameterId", GetIdValue(parameter.Id));
                WriteProperty(writer, "formattedValue",
                    SafeAsValueString(parameter));

                if (parameter.StorageType == StorageType.Double)
                {
                    try
                    {
                        double raw = parameter.AsDouble();
                        WriteProperty(writer, "rawDouble", raw);
                        WriteProperty(writer, "candidateMillimetres",
                            raw * FeetToMillimetres);
                    }
                    catch
                    {
                        // Candidate remains represented by formatted value.
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static IEnumerable<Parameter> FindDimensionParameters(
            ParameterSet set)
        {
            if (set == null)
                return Enumerable.Empty<Parameter>();

            string[] tokens =
            {
                "diameter",
                "nominal",
                "wall",
                "thickness",
                "radius",
                "outside",
                "inside",
                "header",
                "branch",
                "pipe size",
                "schedule",
                " od",
                " id",
                "nd",
                "wt"
            };

            return set
                .Cast<Parameter>()
                .Where(x => x?.Definition?.Name != null)
                .Where(x =>
                {
                    string name = x.Definition.Name.ToLowerInvariant();
                    return tokens.Any(name.Contains);
                })
                .ToList();
        }

        private static void WriteCylinderFaceSummary(
            JsonTextWriter writer,
            Element element)
        {
            Options options = CreateGeometryOptions(false);
            GeometryElement geometry = null;

            try
            {
                geometry = element.get_Geometry(options);
            }
            catch
            {
                // Summary remains empty.
            }

            writer.WriteStartArray();

            if (geometry != null)
            {
                int index = 0;

                foreach (CylindricalFaceData face in
                         CollectCylindricalFaces(geometry, 0))
                {
                    writer.WriteStartObject();
                    WriteProperty(writer, "index", index++);
                    WriteProperty(writer, "areaInternalSquareFeet",
                        face.Area);
                    WriteProperty(writer, "radiusInternalFeet",
                        face.Radius);
                    WriteProperty(writer, "radiusMillimetres",
                        face.Radius * FeetToMillimetres);
                    writer.WritePropertyName("origin");
                    WritePoint(writer, face.Origin);
                    writer.WritePropertyName("axis");
                    WriteVector(writer, face.Axis);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }

        private static IEnumerable<CylindricalFaceData>
            CollectCylindricalFaces(
                GeometryElement geometry,
                int depth)
        {
            if (geometry == null ||
                depth > MaximumGeometryRecursionDepth)
            {
                yield break;
            }

            foreach (GeometryObject geometryObject in geometry)
            {
                Solid solid = geometryObject as Solid;

                if (solid != null)
                {
                    foreach (Face face in solid.Faces)
                    {
                        CylindricalFace cylindrical =
                            face as CylindricalFace;

                        if (cylindrical == null)
                            continue;

                        XYZ radiusVector = null;

                        try
                        {
                            radiusVector = cylindrical.get_Radius(0);
                        }
                        catch
                        {
                            // Invalid cylindrical face.
                        }

                        if (radiusVector == null)
                            continue;

                        yield return new CylindricalFaceData
                        {
                            Area = face.Area,
                            Origin = cylindrical.Origin,
                            Axis = cylindrical.Axis,
                            Radius = radiusVector.GetLength()
                        };
                    }

                    continue;
                }

                GeometryInstance instance =
                    geometryObject as GeometryInstance;

                if (instance == null)
                    continue;

                GeometryElement instanceGeometry = null;

                try
                {
                    instanceGeometry = instance.GetInstanceGeometry();
                }
                catch
                {
                    // Continue with the remaining geometry.
                }

                foreach (CylindricalFaceData nested in
                         CollectCylindricalFaces(
                             instanceGeometry,
                             depth + 1))
                {
                    yield return nested;
                }
            }
        }

        private static void WriteGeometry(
            JsonTextWriter writer,
            Document doc,
            Element element,
            bool includeFullDetail)
        {
            Options options = CreateGeometryOptions(includeFullDetail);
            GeometryElement geometry = null;
            string error = null;

            try
            {
                geometry = element.get_Geometry(options);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            writer.WriteStartObject();
            WriteProperty(writer, "detail",
                includeFullDetail ? "full" : "summary");
            WriteProperty(writer, "includeNonVisibleObjects", true);
            WriteProperty(writer, "computeReferences", includeFullDetail);
            WriteProperty(writer, "detailLevel", "Fine");
            WriteProperty(writer, "error", error);

            writer.WritePropertyName("objects");
            writer.WriteStartArray();

            if (geometry != null)
            {
                foreach (GeometryObject geometryObject in geometry)
                {
                    WriteGeometryObject(
                        writer,
                        doc,
                        geometryObject,
                        includeFullDetail,
                        0);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static Options CreateGeometryOptions(bool computeReferences)
        {
            return new Options
            {
                ComputeReferences = computeReferences,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine
            };
        }

        private static void WriteGeometryObject(
            JsonTextWriter writer,
            Document doc,
            GeometryObject geometryObject,
            bool includeFullDetail,
            int depth)
        {
            if (geometryObject == null)
                return;

            writer.WriteStartObject();
            WriteProperty(writer, "runtimeType",
                geometryObject.GetType().FullName);
            WriteProperty(writer, "graphicsStyleId",
                GetIdValue(geometryObject.GraphicsStyleId));

            if (depth > MaximumGeometryRecursionDepth)
            {
                WriteProperty(writer, "truncated", true);
                writer.WriteEndObject();
                return;
            }

            Solid solid = geometryObject as Solid;

            if (solid != null)
            {
                WriteSolid(writer, doc, solid, includeFullDetail);
                writer.WriteEndObject();
                return;
            }

            GeometryInstance instance =
                geometryObject as GeometryInstance;

            if (instance != null)
            {
                writer.WritePropertyName("transform");
                WriteTransform(writer, instance.Transform);

                GeometryElement instanceGeometry = null;

                try
                {
                    instanceGeometry = instance.GetInstanceGeometry();
                }
                catch (Exception ex)
                {
                    WriteProperty(writer, "instanceGeometryError", ex.Message);
                }

                writer.WritePropertyName("instanceObjects");
                writer.WriteStartArray();

                if (instanceGeometry != null)
                {
                    foreach (GeometryObject nested in instanceGeometry)
                    {
                        WriteGeometryObject(
                            writer,
                            doc,
                            nested,
                            includeFullDetail,
                            depth + 1);
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                return;
            }

            Curve curve = geometryObject as Curve;

            if (curve != null)
            {
                writer.WritePropertyName("curve");
                WriteCurve(writer, curve, includeFullDetail);
                writer.WriteEndObject();
                return;
            }

            Mesh mesh = geometryObject as Mesh;

            if (mesh != null)
            {
                writer.WritePropertyName("mesh");
                WriteMesh(writer, mesh, includeFullDetail);
                writer.WriteEndObject();
                return;
            }

            writer.WriteEndObject();
        }

        private static void WriteSolid(
            JsonTextWriter writer,
            Document doc,
            Solid solid,
            bool includeFullDetail)
        {
            WriteProperty(writer, "volumeInternalCubicFeet", solid.Volume);
            WriteProperty(writer, "volumeCubicMillimetres",
                solid.Volume * Math.Pow(FeetToMillimetres, 3));
            WriteProperty(writer, "surfaceAreaInternalSquareFeet",
                solid.SurfaceArea);
            WriteProperty(writer, "surfaceAreaSquareMillimetres",
                solid.SurfaceArea * Math.Pow(FeetToMillimetres, 2));
            WriteProperty(writer, "faceCount", solid.Faces.Size);
            WriteProperty(writer, "edgeCount", solid.Edges.Size);

            writer.WritePropertyName("faces");
            writer.WriteStartArray();

            int faceIndex = 0;

            foreach (Face face in solid.Faces)
            {
                WriteFace(
                    writer,
                    doc,
                    face,
                    faceIndex++,
                    includeFullDetail);
            }

            writer.WriteEndArray();

            if (!includeFullDetail)
                return;

            writer.WritePropertyName("edges");
            writer.WriteStartArray();

            int edgeIndex = 0;

            foreach (Edge edge in solid.Edges)
            {
                WriteEdge(writer, edge, edgeIndex++);
            }

            writer.WriteEndArray();
        }

        private static void WriteFace(
            JsonTextWriter writer,
            Document doc,
            Face face,
            int faceIndex,
            bool includeFullDetail)
        {
            writer.WriteStartObject();
            WriteProperty(writer, "index", faceIndex);
            WriteProperty(writer, "runtimeType", face.GetType().FullName);
            WriteProperty(writer, "areaInternalSquareFeet", face.Area);
            WriteProperty(writer, "areaSquareMillimetres",
                face.Area * Math.Pow(FeetToMillimetres, 2));

            try
            {
                Reference reference = face.Reference;

                WriteProperty(writer, "stableReference",
                    reference == null
                        ? null
                        : reference.ConvertToStableRepresentation(doc));
            }
            catch
            {
                // Reference is optional.
            }

            BoundingBoxUV uvBounds = null;

            try
            {
                uvBounds = face.GetBoundingBox();
            }
            catch
            {
                // Optional.
            }

            if (uvBounds != null)
            {
                writer.WritePropertyName("uvBounds");
                writer.WriteStartObject();
                writer.WritePropertyName("min");
                WriteUv(writer, uvBounds.Min);
                writer.WritePropertyName("max");
                WriteUv(writer, uvBounds.Max);
                writer.WriteEndObject();

                try
                {
                    UV midpoint = new UV(
                        (uvBounds.Min.U + uvBounds.Max.U) / 2.0,
                        (uvBounds.Min.V + uvBounds.Max.V) / 2.0);

                    writer.WritePropertyName("sampleNormal");
                    WriteVector(writer, face.ComputeNormal(midpoint));
                }
                catch
                {
                    // Optional face normal.
                }
            }

            PlanarFace planar = face as PlanarFace;

            if (planar != null)
            {
                writer.WritePropertyName("origin");
                WritePoint(writer, planar.Origin);
                writer.WritePropertyName("normal");
                WriteVector(writer, planar.FaceNormal);
                writer.WritePropertyName("xVector");
                WriteVector(writer, planar.XVector);
                writer.WritePropertyName("yVector");
                WriteVector(writer, planar.YVector);
            }

            CylindricalFace cylindrical = face as CylindricalFace;

            if (cylindrical != null)
            {
                writer.WritePropertyName("origin");
                WritePoint(writer, cylindrical.Origin);
                writer.WritePropertyName("axis");
                WriteVector(writer, cylindrical.Axis);

                try
                {
                    double radius = cylindrical.get_Radius(0).GetLength();
                    WriteProperty(writer, "radiusInternalFeet", radius);
                    WriteProperty(writer, "radiusMillimetres",
                        radius * FeetToMillimetres);
                }
                catch
                {
                    // Invalid cylindrical data.
                }
            }

            WriteOptionalFaceProperties(writer, face);

            if (includeFullDetail)
            {
                writer.WritePropertyName("edgeLoops");
                writer.WriteStartArray();

                try
                {
                    int loopIndex = 0;

                    foreach (EdgeArray loop in face.EdgeLoops)
                    {
                        writer.WriteStartObject();
                        WriteProperty(writer, "index", loopIndex++);
                        writer.WritePropertyName("edges");
                        writer.WriteStartArray();

                        int edgeIndex = 0;

                        foreach (Edge edge in loop)
                            WriteEdge(writer, edge, edgeIndex++);

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                }
                catch (Exception ex)
                {
                    writer.WriteStartObject();
                    WriteProperty(writer, "error", ex.Message);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (includeFullDetail)
            {
                writer.WritePropertyName("triangulation");

                try
                {
                    WriteMesh(writer, face.Triangulate(), true);
                }
                catch (Exception ex)
                {
                    writer.WriteStartObject();
                    WriteProperty(writer, "error", ex.Message);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
        }

        private static void WriteOptionalFaceProperties(
            JsonTextWriter writer,
            Face face)
        {
            string[] propertyNames =
            {
                "Origin",
                "Axis",
                "HalfAngle",
                "Radius",
                "XVector",
                "YVector"
            };

            writer.WritePropertyName("optionalProperties");
            writer.WriteStartObject();

            foreach (string propertyName in propertyNames)
            {
                object value = ReadProperty(face, propertyName);

                if (value == null)
                    continue;

                XYZ xyz = value as XYZ;

                if (xyz != null)
                {
                    writer.WritePropertyName(propertyName);
                    WriteVector(writer, xyz);
                    continue;
                }

                WriteProperty(writer, propertyName, value);
            }

            writer.WriteEndObject();
        }

        private static void WriteEdge(
            JsonTextWriter writer,
            Edge edge,
            int index)
        {
            writer.WriteStartObject();
            WriteProperty(writer, "index", index);

            try
            {
                WriteProperty(writer, "approximateLengthInternalFeet",
                    edge.ApproximateLength);
                WriteProperty(writer, "approximateLengthMillimetres",
                    edge.ApproximateLength * FeetToMillimetres);
            }
            catch
            {
                // Optional.
            }

            try
            {
                writer.WritePropertyName("curve");
                WriteCurve(writer, edge.AsCurve(), true);
            }
            catch (Exception ex)
            {
                WriteProperty(writer, "curveError", ex.Message);
            }

            try
            {
                writer.WritePropertyName("tessellatedPointsMillimetres");
                WritePointListMillimetres(writer, edge.Tessellate());
            }
            catch
            {
                // Optional.
            }

            writer.WriteEndObject();
        }

        private static void WriteCurve(
            JsonTextWriter writer,
            Curve curve,
            bool includeTessellation)
        {
            if (curve == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            WriteProperty(writer, "runtimeType", curve.GetType().FullName);
            WriteProperty(writer, "isBound", curve.IsBound);

            try
            {
                WriteProperty(writer, "lengthInternalFeet", curve.Length);
                WriteProperty(writer, "lengthMillimetres",
                    curve.Length * FeetToMillimetres);
            }
            catch
            {
                // Unbound curves may not expose a finite length.
            }

            if (curve.IsBound)
            {
                try
                {
                    writer.WritePropertyName("start");
                    WritePoint(writer, curve.GetEndPoint(0));
                    writer.WritePropertyName("end");
                    WritePoint(writer, curve.GetEndPoint(1));
                }
                catch
                {
                    // Optional endpoints.
                }
            }

            Line line = curve as Line;

            if (line != null)
            {
                writer.WritePropertyName("origin");
                WritePoint(writer, line.Origin);
                writer.WritePropertyName("direction");
                WriteVector(writer, line.Direction);
            }

            Arc arc = curve as Arc;

            if (arc != null)
            {
                writer.WritePropertyName("center");
                WritePoint(writer, arc.Center);
                writer.WritePropertyName("normal");
                WriteVector(writer, arc.Normal);
                WriteProperty(writer, "radiusInternalFeet", arc.Radius);
                WriteProperty(writer, "radiusMillimetres",
                    arc.Radius * FeetToMillimetres);
            }

            if (includeTessellation)
            {
                try
                {
                    writer.WritePropertyName("tessellatedPointsMillimetres");
                    WritePointListMillimetres(writer, curve.Tessellate());
                }
                catch
                {
                    // Optional.
                }
            }

            writer.WriteEndObject();
        }

        private static void WriteMesh(
            JsonTextWriter writer,
            Mesh mesh,
            bool includeFullDetail)
        {
            if (mesh == null)
            {
                writer.WriteNull();
                return;
            }

            IList<XYZ> vertices = mesh.Vertices;

            int vertexCount =
                vertices == null
                    ? 0
                    : vertices.Count;

            writer.WriteStartObject();
            WriteProperty(writer, "vertexCount", vertexCount);
            WriteProperty(writer, "triangleCount", mesh.NumTriangles);

            if (includeFullDetail)
            {
                writer.WritePropertyName("verticesMillimetres");
                writer.WriteStartArray();

                for (int index = 0; index < vertexCount; index++)
                {
                    WritePointMillimetresArray(
                        writer,
                        vertices[index]);
                }

                writer.WriteEndArray();

                writer.WritePropertyName("triangles");
                writer.WriteStartArray();

                for (int index = 0; index < mesh.NumTriangles; index++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(index);
                    writer.WriteStartArray();
                    writer.WriteValue(triangle.get_Index(0));
                    writer.WriteValue(triangle.get_Index(1));
                    writer.WriteValue(triangle.get_Index(2));
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        private static IList<Connector> GetConnectors(Element element)
        {
            if (element == null)
                return new List<Connector>();

            ConnectorManager manager = null;

            try
            {
                MEPCurve curve = element as MEPCurve;

                if (curve != null)
                    manager = curve.ConnectorManager;

                FamilyInstance familyInstance = element as FamilyInstance;

                if (manager == null && familyInstance?.MEPModel != null)
                    manager = familyInstance.MEPModel.ConnectorManager;
            }
            catch
            {
                manager = null;
            }

            if (manager?.Connectors == null)
                return new List<Connector>();

            try
            {
                return manager.Connectors
                    .Cast<Connector>()
                    .Where(x => x != null)
                    .OrderBy(x =>
                    {
                        XYZ origin = SafeGetConnectorOrigin(x);
                        return origin?.X ?? 0.0;
                    })
                    .ThenBy(x =>
                    {
                        XYZ origin = SafeGetConnectorOrigin(x);
                        return origin?.Y ?? 0.0;
                    })
                    .ThenBy(x =>
                    {
                        XYZ origin = SafeGetConnectorOrigin(x);
                        return origin?.Z ?? 0.0;
                    })
                    .ToList();
            }
            catch
            {
                return new List<Connector>();
            }
        }

        private static IEnumerable<Element> GetConnectedOwners(
            Connector connector)
        {
            return GetConnectedReferences(connector)
                .Select(x => x.Owner)
                .Where(x => x != null)
                .GroupBy(x => GetIdValue(x.Id))
                .Select(x => x.First());
        }

        private static IList<Connector> GetConnectedReferences(
            Connector connector)
        {
            if (connector == null)
                return new List<Connector>();

            try
            {
                return connector.AllRefs
                    .Cast<Connector>()
                    .Where(x => x != null)
                    .Where(x =>
                        x.Owner != null &&
                        connector.Owner != null &&
                        !x.Owner.Id.Equals(connector.Owner.Id))
                    .ToList();
            }
            catch
            {
                return new List<Connector>();
            }
        }

        private static int FindConnectorIndex(
            Element owner,
            Connector connector)
        {
            if (owner == null || connector == null)
                return -1;

            IList<Connector> connectors = GetConnectors(owner);

            for (int index = 0; index < connectors.Count; index++)
            {
                Connector candidate = connectors[index];

                if (ReferenceEquals(candidate, connector))
                    return index;

                XYZ first = SafeGetConnectorOrigin(candidate);
                XYZ second = SafeGetConnectorOrigin(connector);

                if (first != null && second != null &&
                    first.DistanceTo(second) <= 1.0e-8 &&
                    candidate.Domain == connector.Domain &&
                    candidate.ConnectorType == connector.ConnectorType)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string BuildClassificationText(
            Document doc,
            Element element)
        {
            List<string> values = new List<string>
            {
                element.Category?.Name,
                element.Name,
                element.GetType().Name
            };

            FamilyInstance familyInstance = element as FamilyInstance;

            if (familyInstance?.Symbol != null)
            {
                values.Add(familyInstance.Symbol.FamilyName);
                values.Add(familyInstance.Symbol.Name);
            }

            ElementType type = doc.GetElement(
                element.GetTypeId()) as ElementType;

            if (type != null)
            {
                values.Add(type.Name);
                values.Add(ReadPropertyAsString(type, "FamilyName"));
            }

            foreach (Parameter parameter in FindDimensionParameters(
                         element.Parameters))
            {
                values.Add(parameter.Definition?.Name);
                values.Add(SafeAsValueString(parameter));
            }

            return string.Join(
                " | ",
                values
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string ClassifyFabricationKind(
            string classification,
            Element element)
        {
            string value = (classification ?? string.Empty)
                .ToUpperInvariant();

            if (element is Pipe)
                return "pipe";
            if (value.Contains("SHAPED BRANCH"))
                return "shaped-branch";
            if (value.Contains("REDUCER"))
                return "reducer";
            if (value.Contains("FLANGE"))
                return "flange";
            if (value.Contains("COUPLING"))
                return "coupling";
            if (value.Contains("WELD"))
                return "weld-helper-or-weld-fitting";
            if (value.Contains("ELBOW"))
                return "elbow";
            if (value.Contains("TEE"))
                return "tee";

            return "other";
        }

        private static IList<ViewSheet> ResolveOpenedSheets(Document doc)
        {
            List<ViewSheet> result = new List<ViewSheet>();
            View activeView = doc?.ActiveView;

            ViewSheet activeSheet = activeView as ViewSheet;

            if (activeSheet != null)
            {
                result.Add(activeSheet);
                return result;
            }

            if (activeView == null)
                return result;

            try
            {
                result.AddRange(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .Where(x => x.ViewId.Equals(activeView.Id))
                        .Select(x => doc.GetElement(x.SheetId) as ViewSheet)
                        .Where(x => x != null)
                        .GroupBy(x => GetIdValue(x.Id))
                        .Select(x => x.First())
                        .OrderBy(x => x.SheetNumber)
                        .ThenBy(x => x.Name));
            }
            catch
            {
                // An unplaced active view simply has no sheet context.
            }

            return result;
        }

        private static string ResolveOpenedSheetName(Document doc)
        {
            ViewSheet sheet = ResolveOpenedSheets(doc).FirstOrDefault();

            if (sheet == null)
                return null;

            return string.Join(
                "_",
                new[] { sheet.SheetNumber, sheet.Name }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static bool IsSelectableDiagnosticElement(Element element)
        {
            return element is AssemblyInstance ||
                   IsDiagnosticModelElement(element);
        }

        private static bool IsDiagnosticModelElement(Element element)
        {
            return element != null &&
                   !(element is ElementType) &&
                   !(element is View) &&
                   element.Category != null &&
                   element.Category.CategoryType == CategoryType.Model;
        }

        private static long ResolveLevelId(Element element)
        {
            try
            {
                PropertyInfo property = element.GetType().GetProperty(
                    "LevelId",
                    BindingFlags.Instance | BindingFlags.Public);

                return GetIdValue(property?.GetValue(element) as ElementId);
            }
            catch
            {
                return -1L;
            }
        }

        private static ICollection<ElementId> SafeGetSubComponentIds(
            FamilyInstance familyInstance)
        {
            try
            {
                return familyInstance.GetSubComponentIds();
            }
            catch
            {
                return new List<ElementId>();
            }
        }

        private static BoundingBoxXYZ SafeGetBoundingBox(
            Element element)
        {
            try
            {
                return element?.get_BoundingBox(null);
            }
            catch
            {
                return null;
            }
        }

        private static XYZ SafeGetConnectorOrigin(Connector connector)
        {
            try
            {
                return connector?.Origin;
            }
            catch
            {
                return null;
            }
        }

        private static Transform SafeGetConnectorTransform(
            Connector connector)
        {
            try
            {
                return connector?.CoordinateSystem;
            }
            catch
            {
                return null;
            }
        }

        private static MEPSystem SafeGetMepSystem(Connector connector)
        {
            try
            {
                return connector?.MEPSystem;
            }
            catch
            {
                return null;
            }
        }

        private static bool SafeGetBool(object value, string propertyName)
        {
            object result = ReadProperty(value, propertyName);
            return result is bool && (bool)result;
        }

        private static object ReadProperty(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return null;

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public);

                return property?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadPropertyAsString(
            object instance,
            string name)
        {
            object value = ReadProperty(instance, name);
            return value?.ToString();
        }

        private static object InvokeMethod(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return null;

            MethodInfo method = instance.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);

            return method?.Invoke(instance, null);
        }

        private static string SafeAsValueString(Parameter parameter)
        {
            try
            {
                return parameter?.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeLengthParameter(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string value = name.ToLowerInvariant();

            string[] tokens =
            {
                "length",
                "diameter",
                "radius",
                "thickness",
                "offset",
                "elevation",
                "width",
                "height",
                "size",
                "wall",
                "inside",
                "outside",
                "nominal",
                " od",
                " id",
                "nd",
                "wt"
            };

            return tokens.Any(value.Contains);
        }

        private static string TryGetBuiltInParameterName(long value)
        {
            if (value >= 0 || value < int.MinValue || value > int.MaxValue)
                return null;

            try
            {
                return ((BuiltInParameter)(int)value).ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void WriteTransform(
            JsonTextWriter writer,
            Transform transform)
        {
            if (transform == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("origin");
            WritePoint(writer, transform.Origin);
            writer.WritePropertyName("basisX");
            WriteVector(writer, transform.BasisX);
            writer.WritePropertyName("basisY");
            WriteVector(writer, transform.BasisY);
            writer.WritePropertyName("basisZ");
            WriteVector(writer, transform.BasisZ);
            try
            {
                WriteProperty(writer, "determinant", transform.Determinant);
                WriteProperty(writer, "hasReflection", transform.HasReflection);
                WriteProperty(writer, "isConformal", transform.IsConformal);

                if (transform.IsConformal)
                    WriteProperty(writer, "scale", transform.Scale);
            }
            catch (Exception ex)
            {
                WriteProperty(writer, "transformMetadataError", ex.Message);
            }

            writer.WriteEndObject();
        }

        private static void WritePoint(
            JsonTextWriter writer,
            XYZ point)
        {
            if (point == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("feet");
            WriteXyzArray(writer, point, 1.0);
            writer.WritePropertyName("millimetres");
            WriteXyzArray(writer, point, FeetToMillimetres);
            writer.WriteEndObject();
        }

        private static void WriteVector(
            JsonTextWriter writer,
            XYZ vector)
        {
            if (vector == null)
            {
                writer.WriteNull();
                return;
            }

            WriteXyzArray(writer, vector, 1.0);
        }

        private static void WriteXyzArray(
            JsonTextWriter writer,
            XYZ value,
            double multiplier)
        {
            writer.WriteStartArray();
            writer.WriteValue(value.X * multiplier);
            writer.WriteValue(value.Y * multiplier);
            writer.WriteValue(value.Z * multiplier);
            writer.WriteEndArray();
        }

        private static void WriteUv(JsonTextWriter writer, UV uv)
        {
            if (uv == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartArray();
            writer.WriteValue(uv.U);
            writer.WriteValue(uv.V);
            writer.WriteEndArray();
        }

        private static void WritePointListMillimetres(
            JsonTextWriter writer,
            IEnumerable<XYZ> points)
        {
            writer.WriteStartArray();

            if (points != null)
            {
                foreach (XYZ point in points)
                    WritePointMillimetresArray(writer, point);
            }

            writer.WriteEndArray();
        }

        private static void WritePointMillimetresArray(
            JsonTextWriter writer,
            XYZ point)
        {
            if (point == null)
            {
                writer.WriteNull();
                return;
            }

            WriteXyzArray(writer, point, FeetToMillimetres);
        }

        private static void WriteElementIdArray(
            JsonTextWriter writer,
            string name,
            IEnumerable<ElementId> ids)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();

            if (ids != null)
            {
                foreach (ElementId id in ids)
                    writer.WriteValue(GetIdValue(id));
            }

            writer.WriteEndArray();
        }

        private static void WriteProperty(
            JsonTextWriter writer,
            string name,
            object value)
        {
            writer.WritePropertyName(name);

            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            Type type = value.GetType();

            if (type.IsEnum)
            {
                writer.WriteValue(value.ToString());
                return;
            }

            writer.WriteValue(value);
        }

        private static long GetIdValue(ElementId id)
        {
            return RevitApiCompatibility.GetElementIdValue(id);
        }

        private static int GetWorksetIdValue(WorksetId id)
        {
            if (id == null)
                return -1;

            try
            {
                return id.IntegerValue;
            }
            catch
            {
                return -1;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            using (FileStream stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter
                    .ToString(hash)
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string EnsureJsonExtension(string path)
        {
            if (string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return Path.ChangeExtension(path, ".json");
        }

        private static string SanitizeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value)
                ? "FabricationDiagnostics.json"
                : value.Trim();

            foreach (char invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');

            return result;
        }

        private static void CommitFile(
            string pendingPath,
            string finalPath)
        {
            if (File.Exists(finalPath))
            {
                string backup = pendingPath + ".backup";

                try
                {
                    File.Replace(pendingPath, finalPath, backup, true);
                }
                finally
                {
                    TryDeleteFile(backup);
                }
            }
            else
            {
                File.Move(pendingPath, finalPath);
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort temporary-file cleanup.
            }
        }

        private sealed class DiagnosticsHeaderPipeSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element is Pipe;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class DiagnosticsSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return IsSelectableDiagnosticElement(element);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class DiagnosticElementContext
        {
            public Element Element { get; set; }
            public bool IsSelected { get; set; }
            public int ConnectionDepth { get; set; }
        }

        private sealed class DiagnosticConnection
        {
            public long SourceElementId { get; set; }
            public int SourceConnectorIndex { get; set; }
            public long TargetElementId { get; set; }
            public int TargetConnectorIndex { get; set; }
            public bool SourceSelected { get; set; }
            public bool TargetSelected { get; set; }
        }

        private sealed class DiagnosticContextCollection
        {
            public IList<DiagnosticElementContext> Elements { get; set; } =
                new List<DiagnosticElementContext>();

            public IList<DiagnosticConnection> Connections { get; set; } =
                new List<DiagnosticConnection>();

            public bool ContextTruncated { get; set; }
        }

        private sealed class CylindricalFaceData
        {
            public double Area { get; set; }
            public XYZ Origin { get; set; }
            public XYZ Axis { get; set; }
            public double Radius { get; set; }
        }
    }
}
