using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI; // <-- for ProgressWindow
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Compatibility;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Xml.Linq;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class MapPipesCommand : IExternalCommand
    {
        private static readonly HashSet<string> RG_LIST =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "coupling" };
        private static readonly HashSet<string> SC_LIST =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "branch" };
        private static readonly Regex _reStubEnd =
            new Regex(@"\bstub end\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _reEnd =
            new Regex(@"\bend\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);


        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {

            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            UIApplication uiapp = data.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = uidoc.ActiveView;

            var pipes = new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType()
                .ToElements();

            if (pipes.Count == 0)
            {
                AppDialog.Info(uiapp,"Pipe End Prep", "No pipes found in the active view.");
                return Result.Succeeded;
            }
            var pipeConfig = AppConfig.CurrentConfig.PipeMapParameters ?? new MapParameters();

            string end1 = (pipeConfig.End1 ?? string.Empty).Trim();
            string end2 = (pipeConfig.End2 ?? string.Empty).Trim();
            string endPrep = (pipeConfig.EndPrep ?? string.Empty).Trim();

            // End 1, End 2, and End Prep are independent output mappings.
            // Blank configuration fields are intentionally ignored.
            var configuredParameters = new List<(string Label, string Name)>();
            AddConfiguredParameter(configuredParameters, "End 1", end1);
            AddConfiguredParameter(configuredParameters, "End 2", end2);
            AddConfiguredParameter(configuredParameters, "End Prep", endPrep);

            if (configuredParameters.Count == 0)
            {
                // Nothing was mapped in Configuration. This is a valid no-op.
                return Result.Succeeded;
            }

            // Blank configuration fields are ignored. Every non-blank mapping is intentional.
            // If an intentional mapping does not exist on Pipes, give the user a safe choice:
            // create/bind it as a writable Text instance parameter, or cancel and fix Configuration.
            var preflight = AnalyzeConfiguredParameters(doc, pipes, configuredParameters);

            var blockingProblems = preflight
                .Where(x => x.State == MappingParameterState.Partial
                         || x.State == MappingParameterState.NonText
                         || x.State == MappingParameterState.ReadOnly
                         || x.State == MappingParameterState.TypeBound)
                .ToList();

            if (blockingProblems.Count > 0)
            {
                string blockingDetails = string.Join(
                    "\n",
                    blockingProblems.Select(FormatPreflightProblem));

                AppDialog.ShowDetailed(
                    uiapp,
                    "Pipe End Prep",
                    "One or more configured mappings cannot be used safely.",
                    "The mapping was cancelled. Adjust the parameter or the Configuration, then run Map Pipe End Prep again.",
                    blockingDetails,
                    MessageDialogIcon.Warning);

                return Result.Cancelled;
            }

            var missingParameters = preflight
                .Where(x => x.State == MappingParameterState.Missing
                         || x.State == MappingParameterState.NeedsPipeBinding)
                .ToList();

            if (missingParameters.Count > 0)
            {
                string missingList = string.Join(
                    "\n",
                    missingParameters.Select(x =>
                        x.State == MappingParameterState.NeedsPipeBinding
                            ? $"• \"{x.Name}\" ({x.Label}) exists in the project but is not assigned to Pipes."
                            : $"• \"{x.Name}\" ({x.Label}) cannot be found on Pipes."));

                int choice = AppDialog.Choose(
                    uiapp,
                    "Pipe End Prep",
                    "Missing mapped parameter" + (missingParameters.Count == 1 ? string.Empty : "s"),
                    "You are trying to map configured parameter" +
                    (missingParameters.Count == 1 ? " that is" : "s that are") +
                    " not available on Pipes.\n\n" +
                    missingList +
                    "\n\nYou can create/assign the missing Text instance parameter" +
                    (missingParameters.Count == 1 ? string.Empty : "s") +
                    " to the Pipes category and continue, or cancel and adjust Configuration.",
                    new[]
                    {
                        "Create / assign missing parameter" +
                        (missingParameters.Count == 1 ? string.Empty : "s") +
                        " and continue"
                    },
                    0);

                if (choice != 0)
                    return Result.Cancelled;

                List<string> created;
                List<string> rebound;
                string createError;

                if (!TryEnsureMissingPipeMappingParameters(
                        uiapp,
                        doc,
                        missingParameters,
                        out created,
                        out rebound,
                        out createError))
                {
                    AppDialog.ShowDetailed(
                        uiapp,
                        "Pipe End Prep",
                        "The missing mapping parameters could not be created or assigned.",
                        "No pipe end-prep values were changed.",
                        createError,
                        MessageDialogIcon.Error);

                    return Result.Failed;
                }

                // Recollect the pipes after the binding transaction so the newly-added
                // project parameters are resolved from fresh Revit element wrappers.
                pipes = new FilteredElementCollector(doc, activeView.Id)
                    .OfCategory(BuiltInCategory.OST_PipeCurves)
                    .WhereElementIsNotElementType()
                    .ToElements();

                // Never trust the create/bind step blindly. Re-run the exact same preflight
                // before mapping so a failed or incompatible binding cannot cause a partial update.
                preflight = AnalyzeConfiguredParameters(doc, pipes, configuredParameters);
                var remainingProblems = preflight
                    .Where(x => x.State != MappingParameterState.Usable)
                    .ToList();

                if (remainingProblems.Count > 0)
                {
                    AppDialog.ShowDetailed(
                        uiapp,
                        "Pipe End Prep",
                        "The mapping parameters are still not ready.",
                        "No pipe end-prep values were changed.",
                        string.Join("\n", remainingProblems.Select(FormatPreflightProblem)),
                        MessageDialogIcon.Error);

                    return Result.Failed;
                }
            }

            int updated = 0;
            var nameCache = new Dictionary<ElementId, string>();
            var cmCache = new Dictionary<ElementId, ConnectorManager>();

            var win = new ProgressWindow();
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                new WindowInteropHelper(win).Owner = hwnd;

                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(pipes.Count, "Mapping Pipe End Prep…", "Procesing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);


                using (var tx = new Transaction(doc, "Map Pipe End Prep"))
                {
                    tx.Start();

                    int index = 0;
                    foreach (var pipe in pipes)
                    {
                        if (win.IsCanceled) break;

                        // Only look up outputs that are actually configured.
                        // End Prep must continue to work even when End 1 and/or End 2 are blank.
                        var c1Param = GetConfiguredTextParameter(pipe, end1);
                        var c2Param = GetConfiguredTextParameter(pipe, end2);
                        var pepParam = GetConfiguredTextParameter(pipe, endPrep);

                        // A pipe can legitimately omit one optional output. Process whichever
                        // configured/usable outputs exist instead of making them interdependent.
                        if (c1Param == null && c2Param == null && pepParam == null)
                        {
                            index++;
                            if ((index & 31) == 0) win.Update(index);
                            continue;
                        }

                        var pair = GetConnectedNames(doc, pipe, nameCache, cmCache);
                        var ordered = AlphabeticalReorder(pair.c1Name, pair.c2Name, pair.c1Element, pair.c2Element);

                        string defaultUnconnectedValue = "Unconnected";
                        string strUnconnected = pipeConfig.EnableMapping ? pipeConfig.Unconnected : defaultUnconnectedValue;

                        if (string.IsNullOrWhiteSpace(strUnconnected))
                            strUnconnected = defaultUnconnectedValue;

                        bool pipeUpdated = false;

                        if (c1Param != null && !c1Param.IsReadOnly)
                        {
                            c1Param.Set(ordered.c1Out ?? strUnconnected);
                            pipeUpdated = true;
                        }

                        if (c2Param != null && !c2Param.IsReadOnly)
                        {
                            c2Param.Set(ordered.c2Out ?? strUnconnected);
                            pipeUpdated = true;
                        }

                        if (pepParam != null && !pepParam.IsReadOnly)
                        {
                            pepParam.Set(ordered.prepOut ?? string.Empty);
                            pipeUpdated = true;
                        }

                        if (pipeUpdated)
                            updated++;
                        index++;
                        win.UpdateSmart(index, pipes.Count, $"Mapping… {index} / {pipes.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(pipes.Count, pipes.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    // optional: call tx.RollBack() instead of Commit above if you want all-or-nothing
                    win.Canceled("Mapping Cancelled", updated, "Mapping pipe end prep has been cancelled");
                    return Result.Cancelled;
                }

              
                win.Done($"Successfully updated {updated} pipe end prep", pipes.Count, "Mapping Pipe End Prep Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
            // no finally auto-close; user will click "Complete" or "Close" on the window
        }

        private static void AddConfiguredParameter(
            IList<(string Label, string Name)> parameters,
            string label,
            string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                parameters.Add((label, name.Trim()));
        }

        private static Parameter GetConfiguredTextParameter(Element element, string parameterName)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
                return null;

            var parameter = element.LookupParameter(parameterName.Trim());
            if (parameter == null || parameter.StorageType != StorageType.String)
                return null;

            return parameter;
        }


        private enum MappingParameterState
        {
            Usable,
            Missing,
            NeedsPipeBinding,
            TypeBound,
            Partial,
            NonText,
            ReadOnly
        }

        private sealed class MappingParameterCheck
        {
            public string Label { get; set; }
            public string Name { get; set; }
            public MappingParameterState State { get; set; }
            public string Detail { get; set; }
        }

        private static List<MappingParameterCheck> AnalyzeConfiguredParameters(
            Document doc,
            IList<Element> pipes,
            IList<(string Label, string Name)> configuredParameters)
        {
            var results = new List<MappingParameterCheck>();

            foreach (var configured in configuredParameters)
            {
                var matchingParameters = pipes
                    .Select(p => p.LookupParameter(configured.Name))
                    .ToList();

                int foundCount = matchingParameters.Count(p => p != null);

                if (foundCount == 0)
                {
                    Definition existingDefinition;
                    Binding existingBinding;

                    if (TryFindParameterBinding(
                            doc,
                            configured.Name,
                            out existingDefinition,
                            out existingBinding))
                    {
                        if (!IsTextDefinition(existingDefinition))
                        {
                            results.Add(new MappingParameterCheck
                            {
                                Label = configured.Label,
                                Name = configured.Name,
                                State = MappingParameterState.NonText,
                                Detail = "A project parameter with this name already exists, but it is not a Text parameter."
                            });
                            continue;
                        }

                        if (!(existingBinding is InstanceBinding))
                        {
                            results.Add(new MappingParameterCheck
                            {
                                Label = configured.Label,
                                Name = configured.Name,
                                State = MappingParameterState.TypeBound,
                                Detail = "A project parameter with this name already exists as a Type parameter. Pipe mapping requires an Instance parameter."
                            });
                            continue;
                        }

                        results.Add(new MappingParameterCheck
                        {
                            Label = configured.Label,
                            Name = configured.Name,
                            State = MappingParameterState.NeedsPipeBinding,
                            Detail = "The Text instance parameter exists in the project, but the Pipes category is not included in its binding."
                        });
                        continue;
                    }

                    results.Add(new MappingParameterCheck
                    {
                        Label = configured.Label,
                        Name = configured.Name,
                        State = MappingParameterState.Missing,
                        Detail = "The parameter does not exist on Pipes or in the project parameter bindings."
                    });
                    continue;
                }

                if (foundCount != pipes.Count)
                {
                    results.Add(new MappingParameterCheck
                    {
                        Label = configured.Label,
                        Name = configured.Name,
                        State = MappingParameterState.Partial,
                        Detail = $"The parameter exists on only {foundCount} of {pipes.Count} pipes in the active view. Automatic creation is blocked to avoid duplicate same-name parameters."
                    });
                    continue;
                }

                if (matchingParameters.Any(p => p.StorageType != StorageType.String))
                {
                    results.Add(new MappingParameterCheck
                    {
                        Label = configured.Label,
                        Name = configured.Name,
                        State = MappingParameterState.NonText,
                        Detail = "The parameter exists, but it is not a Text parameter."
                    });
                    continue;
                }

                if (matchingParameters.Any(p => p.IsReadOnly))
                {
                    results.Add(new MappingParameterCheck
                    {
                        Label = configured.Label,
                        Name = configured.Name,
                        State = MappingParameterState.ReadOnly,
                        Detail = "The parameter exists, but it is read-only on at least one pipe."
                    });
                    continue;
                }

                results.Add(new MappingParameterCheck
                {
                    Label = configured.Label,
                    Name = configured.Name,
                    State = MappingParameterState.Usable,
                    Detail = "Writable Text parameter found on all pipes in the active view."
                });
            }

            return results;
        }

        private static string FormatPreflightProblem(MappingParameterCheck item)
        {
            return $"• \"{item.Name}\" ({item.Label}): {item.Detail}";
        }

        private static bool TryEnsureMissingPipeMappingParameters(
            UIApplication uiapp,
            Document doc,
            IList<MappingParameterCheck> missingParameters,
            out List<string> created,
            out List<string> rebound,
            out string error)
        {
            created = new List<string>();
            rebound = new List<string>();
            error = string.Empty;

            if (uiapp == null || doc == null)
            {
                error = "Revit application or document is not available.";
                return false;
            }

            var requested = missingParameters
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (requested.Count == 0)
                return true;

            var revitApp = uiapp.Application;
            Category pipeCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_PipeCurves);

            if (pipeCategory == null || !pipeCategory.AllowsBoundParameters)
            {
                error = "The Revit Pipes category does not allow bound project parameters in this document.";
                return false;
            }

            string previousSharedParameterFile = revitApp.SharedParametersFilename;
            string tempSharedParameterFile = null;

            try
            {
                using (var tx = new Transaction(doc, "Create Pipe Mapping Parameters"))
                {
                    tx.Start();

                    DefinitionFile temporaryDefinitionFile = null;
                    DefinitionGroup temporaryGroup = null;

                    foreach (var item in requested)
                    {
                        string parameterName = item.Name.Trim();

                        Definition existingDefinition;
                        Binding existingBinding;

                        if (TryFindParameterBinding(
                                doc,
                                parameterName,
                                out existingDefinition,
                                out existingBinding))
                        {
                            if (!IsTextDefinition(existingDefinition))
                                throw new InvalidOperationException(
                                    $"\"{parameterName}\" already exists in the project, but it is not a Text parameter.");

                            var instanceBinding = existingBinding as InstanceBinding;
                            if (instanceBinding == null)
                                throw new InvalidOperationException(
                                    $"\"{parameterName}\" already exists as a Type parameter. " +
                                    "Pipe mapping requires a Text instance parameter, so the plugin will not create a duplicate parameter with the same name.");

                            if (!BindingContainsCategory(instanceBinding, pipeCategory))
                            {
                                CategorySet categories = revitApp.Create.NewCategorySet();

                                foreach (Category category in instanceBinding.Categories)
                                {
                                    if (category != null && !CategorySetContains(categories, category))
                                        categories.Insert(category);
                                }

                                if (!CategorySetContains(categories, pipeCategory))
                                    categories.Insert(pipeCategory);

                                InstanceBinding newBinding = revitApp.Create.NewInstanceBinding(categories);

                                if (!ReInsertParameterBinding(
                                        doc,
                                        existingDefinition,
                                        newBinding))
                                {
                                    throw new InvalidOperationException(
                                        $"Revit could not add the Pipes category to the existing parameter \"{parameterName}\".");
                                }

                                rebound.Add(parameterName);
                            }

                            continue;
                        }

                        if (temporaryDefinitionFile == null)
                        {
                            tempSharedParameterFile = CreateTemporarySharedParameterFile();
                            revitApp.SharedParametersFilename = tempSharedParameterFile;
                            temporaryDefinitionFile = revitApp.OpenSharedParameterFile();

                            if (temporaryDefinitionFile == null)
                                throw new InvalidOperationException(
                                    "Revit could not open the temporary shared parameter file used to create the mapping parameters.");

                            temporaryGroup = temporaryDefinitionFile.Groups.get_Item("Parallel Systems Pipe Mapping")
                                             ?? temporaryDefinitionFile.Groups.Create("Parallel Systems Pipe Mapping");
                        }

                        ExternalDefinition definition = CreateTextExternalDefinition(
                            temporaryGroup,
                            parameterName);

                        CategorySet pipeCategories = revitApp.Create.NewCategorySet();
                        pipeCategories.Insert(pipeCategory);
                        InstanceBinding pipeBinding = revitApp.Create.NewInstanceBinding(pipeCategories);

                        if (!InsertParameterBinding(doc, definition, pipeBinding))
                        {
                            throw new InvalidOperationException(
                                $"Revit could not create the Text instance parameter \"{parameterName}\" for Pipes.");
                        }

                        created.Add(parameterName);
                    }

                    tx.Commit();
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                bool sharedParameterPathRestored = false;

                try
                {
                    revitApp.SharedParametersFilename = previousSharedParameterFile ?? string.Empty;
                    sharedParameterPathRestored = true;
                }
                catch
                {
                    // If Revit refuses to restore the prior path, keep the temporary file
                    // rather than leave Revit pointing at a path that has already been deleted.
                }

                if (sharedParameterPathRestored &&
                    !string.IsNullOrWhiteSpace(tempSharedParameterFile))
                {
                    try
                    {
                        if (File.Exists(tempSharedParameterFile))
                            File.Delete(tempSharedParameterFile);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool TryFindParameterBinding(
            Document doc,
            string parameterName,
            out Definition definition,
            out Binding binding)
        {
            definition = null;
            binding = null;

            if (doc == null || string.IsNullOrWhiteSpace(parameterName))
                return false;

            DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();

            while (iterator.MoveNext())
            {
                Definition candidate = iterator.Key;
                if (candidate == null ||
                    !string.Equals(candidate.Name, parameterName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                definition = candidate;
                binding = iterator.Current as Binding;
                return true;
            }

            return false;
        }

        private static bool BindingContainsCategory(InstanceBinding binding, Category category)
        {
            if (binding == null || category == null)
                return false;

            foreach (Category current in binding.Categories)
            {
                if (current != null && SameCategory(current, category))
                    return true;
            }

            return false;
        }

        private static bool CategorySetContains(CategorySet categories, Category category)
        {
            if (categories == null || category == null)
                return false;

            foreach (Category current in categories)
            {
                if (current != null && SameCategory(current, category))
                    return true;
            }

            return false;
        }

        private static bool SameCategory(Category left, Category right)
        {
            if (left == null || right == null)
                return false;

            return RevitApiCompatibility.GetElementIdValue(left.Id) ==
                   RevitApiCompatibility.GetElementIdValue(right.Id);
        }

        private static bool IsTextDefinition(Definition definition)
        {
            if (definition == null)
                return false;

#if REVIT2021
            return definition.ParameterType == ParameterType.Text;
#else
            ForgeTypeId dataType = definition.GetDataType();
            return dataType != null && dataType.Equals(SpecTypeId.String.Text);
#endif
        }

        private static ExternalDefinition CreateTextExternalDefinition(
            DefinitionGroup group,
            string parameterName)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

#if REVIT2021
            var options = new ExternalDefinitionCreationOptions(
                parameterName,
                ParameterType.Text);
#else
            var options = new ExternalDefinitionCreationOptions(
                parameterName,
                SpecTypeId.String.Text);
#endif

            options.GUID = GetStablePipeMappingParameterGuid(parameterName);
            options.Visible = true;
            options.UserModifiable = true;
            options.Description = "Parallel Systems pipe mapping parameter.";

            ExternalDefinition definition = group.Definitions.Create(options) as ExternalDefinition;
            if (definition == null)
                throw new InvalidOperationException(
                    $"Revit could not create the shared definition for \"{parameterName}\".");

            return definition;
        }

        private static bool InsertParameterBinding(
            Document doc,
            Definition definition,
            InstanceBinding binding)
        {
#if REVIT2024_OR_GREATER
            return doc.ParameterBindings.Insert(definition, binding, GroupTypeId.Data);
#else
            return doc.ParameterBindings.Insert(definition, binding, BuiltInParameterGroup.PG_DATA);
#endif
        }

        private static bool ReInsertParameterBinding(
            Document doc,
            Definition definition,
            InstanceBinding binding)
        {
#if REVIT2024_OR_GREATER
            return doc.ParameterBindings.ReInsert(
                definition,
                binding,
                definition.GetGroupTypeId());
#else
            return doc.ParameterBindings.ReInsert(
                definition,
                binding,
                definition.ParameterGroup);
#endif
        }

        private static string CreateTemporarySharedParameterFile()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "ParallelSystemsPlugin");

            Directory.CreateDirectory(directory);

            string path = Path.Combine(
                directory,
                "PipeMappingParameters_" + Guid.NewGuid().ToString("N") + ".txt");

            string content =
                "# This is a Revit shared parameter file.\r\n" +
                "# Do not edit manually.\r\n" +
                "*META\tVERSION\tMINVERSION\r\n" +
                "META\t2\t1\r\n" +
                "*GROUP\tID\tNAME\r\n" +
                "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n";

            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private static Guid GetStablePipeMappingParameterGuid(string parameterName)
        {
            string seed =
                "ParallelSystemsPlugin|PipeMapping|" +
                (parameterName ?? string.Empty).Trim().ToUpperInvariant();

            byte[] hash;
            using (SHA256 sha = SHA256.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));

            byte[] guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, guidBytes.Length);

            // Mark the generated value as a name-based UUID-like identifier while keeping
            // it deterministic across projects and Revit versions for the same parameter name.
            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

            return new Guid(guidBytes);
        }

        // ===== Mapping helpers (same logic as your Python) =====

        private static (string c1Name, string c2Name, Element c1Element, Element c2Element) GetConnectedNames(
            Document doc,
            Element pipe,
            Dictionary<ElementId, string> nameCache,
            Dictionary<ElementId, ConnectorManager> cmCache)
        {
            string startName = "Unconnected";
            string endName = "Unconnected";

            Element c1Element = null;
            Element c2Element = null;

            var lc = pipe.Location as LocationCurve;
            if (lc == null || lc.Curve == null)
                return (startName, endName, c1Element, c2Element);

            XYZ startPt = lc.Curve.GetEndPoint(0);
            XYZ endPt = lc.Curve.GetEndPoint(1);

            var cm = GetConnectorManager(pipe, cmCache);
            if (cm == null) return (startName, endName, c1Element, c2Element);

            foreach (Connector conn in cm.Connectors)
            {
                foreach (Connector aref in conn.AllRefs)
                {
                    Element owner = aref.Owner;
                    if (owner == null || owner.Id == pipe.Id) continue;


                    if (owner.Category != null &&
                        owner.Category.Name?.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    var finalOwner = GetValidConnectedElement(doc, owner, pipe, nameCache, cmCache, new HashSet<long>());
                    if (finalOwner == null) continue;

                    string compName = Elements.GetBestName(doc, finalOwner, nameCache);

                    if (conn.Origin.IsAlmostEqualTo(startPt)) 
                    {
                        c1Element = finalOwner;
                        startName = compName; 
                    }
                    else if (conn.Origin.IsAlmostEqualTo(endPt)) 
                    {
                        c2Element = finalOwner;
                        endName = compName; 
                    }
                }
            }

            return (startName, endName, c1Element, c2Element);
        }

        private static Element GetValidConnectedElement(
            Document doc,
            Element elem,
            Element comingFrom,
            Dictionary<ElementId, string> nameCache,
            Dictionary<ElementId, ConnectorManager> cmCache,
            HashSet<long> visited)
        {
            if (elem == null) return null;
            long id = RevitApiCompatibility.GetElementIdValue(elem.Id);
            if (visited.Contains(id)) return null;
            visited.Add(id);


            
            if (!(Elements.Pipes.IsIgnoreComponents(elem, nameCache)
                || Elements.Pipes.IsIgnoreComponentsByCat(elem) 
                || Elements.Pipes.IsIgnoreComponentsByFamName(elem)
                ))

                return elem;

            //if (!(IsWeld(elem, nameCache) || IsInsulation(elem) || IsNonConnector(elem)))
            //    return elem;

            var cm = GetConnectorManager(elem, cmCache);
            if (cm == null) return null;

            foreach (Connector c in cm.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    var owner = r.Owner;
                    if (owner == null || owner.Id == elem.Id || owner.Id == comingFrom.Id)
                        continue;

                    var result = GetValidConnectedElement(doc, owner, elem, nameCache, cmCache, visited);
                    if (result != null) return result;
                }
            }
            return null;
        }

        private static ConnectorManager GetConnectorManager(Element e, Dictionary<ElementId, ConnectorManager> cmCache)
        {
            if (e == null) return null;
            if (cmCache.TryGetValue(e.Id, out var cm)) return cm;

            ConnectorManager outCm = null;

            if (e is FamilyInstance fi)
                outCm = fi.MEPModel?.ConnectorManager;

            if (outCm == null && e is MEPCurve mc)
                outCm = mc.ConnectorManager;

            cmCache[e.Id] = outCm;
            return outCm;
        }

        private static string GetBestName(Document doc, Element e, Dictionary<ElementId, string> cache)
        {
            if (e == null) return "Unnamed";
            if (cache.TryGetValue(e.Id, out var cached)) return cached;

            string result = "Unnamed";

            try
            {
                var p = e.LookupParameter("Description BOM");
                if (p != null && p.HasValue)
                {
                    var s = p.AsString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        result = s;
                        cache[e.Id] = result;
                        return result;
                    }
                }
            }
            catch { }

            try
            {
                var typeElem = doc.GetElement(e.GetTypeId());
                if (typeElem != null)
                {
                    var nameParam = typeElem.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);
                    if (nameParam != null && nameParam.HasValue)
                    {
                        string tn = nameParam.AsString();
                        if (!string.IsNullOrWhiteSpace(tn))
                        {
                            result = tn;
                            cache[e.Id] = result;
                            return result;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(typeElem.Name))
                    {
                        result = typeElem.Name;
                        cache[e.Id] = result;
                        return result;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(e.Name))
                result = e.Name;

            cache[e.Id] = result;
            return result;
        }

        private static bool IsWeld(Element e, Dictionary<ElementId, string> nameCache)
        {
            string nm = Elements.GetBestName(e.Document, e, nameCache).ToLowerInvariant();
            return nm.Contains("weld");
        }

        private static bool IsInsulation(Element e)
        {
            try
            {
                var cat = e.Category;
                return cat != null && cat.Name != null &&
                       cat.Name.IndexOf("insulations", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool IsNonConnector(Element e)
        {
            try
            {
                if (e is FamilyInstance fi && fi.Symbol?.Family != null)
                {
                    string fam = fi.Symbol.Family.Name ?? string.Empty;
                    return fam.IndexOf("non-connector", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            return false;
        }

        private static string MapToEndPrep(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "Unconnected")
                return null;

            string lower = name.ToLowerInvariant();

            return Elements.Pipes.GetElementValue(lower);

            //if (_reStubEnd.IsMatch(lower) || _reEnd.IsMatch(lower))
            //    return "BE";

            //if (lower.Contains("elbow") || lower.Contains("tee") || lower.Contains("cross") ||
            //    lower.Contains("reducer") || lower.Contains("lateral") || lower.Contains("wye"))
            //    return "BE";

            //if (lower.Contains("flange"))
            //    return "PE";

            //if (RG_LIST.Any(k => lower.Contains(k)))
            //    return "RG";

            //if (SC_LIST.Any(k => lower.Contains(k)))
            //    return "SC";

            //return null;
        }

        private static (string c1Out, string c2Out, string prepOut) AlphabeticalReorder(string c1Name, string c2Name, Element c1Element, Element c2Element)
        {
            var items = new[]
            {
                new
                {
                    Code = MapToEndPrep(c1Name),
                    Name = c1Name,
                    IsPipe = Elements.IsPipe(c1Element)
                },
                new
                {
                    Code = MapToEndPrep(c2Name),
                    Name = c2Name,
                    IsPipe = Elements.IsPipe(c2Element)
                }
            };

            var pipeConfigs = AppConfig.CurrentConfig.PipeMapParameters;

            var pairs = new List<(string code, string name)>();

            foreach (var item in items)
            {
                // Add if code exists OR element is NOT a pipe
                if (!string.IsNullOrEmpty(item.Code) || !item.IsPipe)
                {
                    pairs.Add((item.Code, item.Name));
                }
            }

            pairs.Sort((a, b) => string.CompareOrdinal(a.code, b.code));

            while (pairs.Count < 2)
            {
                if (pipeConfigs.EnableMapping && pipeConfigs.Unconnected != string.Empty)
                    pairs.Add((pipeConfigs.Unconnected, "Unconnected"));
                else
                    pairs.Add((null, "Unconnected"));
            }

            string prep = string.Join("-", pairs.Where(p => !string.IsNullOrEmpty(p.code)).Select(p => p.code));

            return (pairs[0].name, pairs[1].name, prep);
        }

        private static (string c1Out, string c2Out, string prepOut) AlphabeticalReorder(string c1Name, string c2Name)
        {
            var codes = new[] { MapToEndPrep(c1Name), MapToEndPrep(c2Name) };
            var names = new[] { c1Name, c2Name };

            var pipeConfigs = AppConfig.CurrentConfig.PipeMapParameters;
            
            var pairs = new List<(string code, string name)>();
            
            for (int i = 0; i < 2; i++)
            {
                if (!string.IsNullOrEmpty(codes[i]))
                    pairs.Add((codes[i], names[i]));
            }

            pairs.Sort((a, b) => string.CompareOrdinal(a.code, b.code));

            while (pairs.Count < 2)
            {
                if (pipeConfigs.EnableMapping && pipeConfigs.Unconnected != string.Empty)
                    pairs.Add((pipeConfigs.Unconnected, "Unconnected"));
                else 
                    pairs.Add((null, "Unconnected"));  
            }

            string prep = string.Join("-", pairs.Where(p => !string.IsNullOrEmpty(p.code)).Select(p => p.code));

            return (pairs[0].name, pairs[1].name, prep);
        }
    }
}
