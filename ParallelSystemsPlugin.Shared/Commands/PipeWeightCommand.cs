using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI; // AppDialog, ProgressWindow
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Compatibility;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class PipeWeightCommand : IExternalCommand
    {
        private const string DRY_PARAM = "Vic_Weight";
        private const string WET_PARAM = "Wet Weight";
        private const double FT_TO_M = 0.3048;
        private const double PI = 3.14;

        private static readonly Regex NonAlnum = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Digits = new Regex(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ===== Library CSV (verbatim from your Python) =====
        private static readonly string LIB_CSV =
        @"Size,Pipe Type,Dry WeightKg/m,Wet WeightKg/m
        15,STAINLESS STEEL - SCH10,1,1.22
        20,STAINLESS STEEL - SCH10,1.28,1.67
        25,STAINLESS STEEL - SCH10,2.09,2.69
        32,STAINLESS STEEL - SCH10,2.69,3.74
        40,STAINLESS STEEL - SCH10,3.11,4.54
        50,STAINLESS STEEL - SCH10,3.93,6.28
        65,STAINLESS STEEL - SCH10,5.26,8.77
        80,STAINLESS STEEL - SCH10,6.46,11.84
        100,STAINLESS STEEL - SCH10,8.37,17.56
        125,STAINLESS STEEL - SCH10,11.56,25.76
        150,STAINLESS STEEL - SCH10,13.83,34.3
        200,STAINLESS STEEL - SCH10,19.97,55.11
        250,STAINLESS STEEL - SCH10,27.79,82.8
        300,STAINLESS STEEL - SCH10,35.99,113.76
        350,STAINLESS STEEL - SCH10,41.36,135.35
        400,STAINLESS STEEL - SCH10,47.34,170.96
        450,STAINLESS STEEL - SCH10,53.31,210.46
        500,STAINLESS STEEL - SCH10,68.65,262.48
        600,STAINLESS STEEL - SCH10,94.53,374.59
        15,CARBON STEEL - STD,1.27,1.46
        20,CARBON STEEL - STD,1.69,2.03
        25,CARBON STEEL - STD,2.5,3.05
        32,CARBON STEEL - STD,3.39,4.35
        40,CARBON STEEL - STD,4.05,5.36
        50,CARBON STEEL - STD,5.44,7.6
        65,CARBON STEEL - STD,8.63,11.71
        80,CARBON STEEL - STD,11.29,16.05
        100,CARBON STEEL - STD,16.08,24.28
        125,CARBON STEEL - STD,21.77,34.67
        150,CARBON STEEL - STD,28.26,46.89
        200,CARBON STEEL - STD,42.55,74.81
        250,CARBON STEEL - STD,60.29,111.11
        300,CARBON STEEL - STD,73.86,146.76
        350,CARBON STEEL - STD,81.33,170.23
        400,CARBON STEEL - STD,93.27,211.04
        450,CARBON STEEL - STD,105.17,255.72
        500,CARBON STEEL - STD,117.15,304.81
        600,CARBON STEEL - STD,141.12,415.24
        15,STAINLESS STEEL - SCH5 - 316L - ERW,0.8,1.05
        20,STAINLESS STEEL - SCH5 - 316L - ERW,1.02,1.44
        25,STAINLESS STEEL - SCH5 - 316L - ERW,1.29,2
        32,STAINLESS STEEL - SCH5 - 316L - ERW,1.65,2.83
        40,STAINLESS STEEL - SCH5 - 316L - ERW,1.9,3.48
        50,STAINLESS STEEL - SCH5 - 316L - ERW,2.39,4.94
        65,STAINLESS STEEL - SCH5 - 316L - ERW,3.69,7.4
        80,STAINLESS STEEL - SCH5 - 316L - ERW,4.52,10.14
        100,STAINLESS STEEL - SCH5 - 316L - ERW,5.84,15.35
        125,STAINLESS STEEL - SCH5 - 316L - ERW,9.46,23.92
        150,STAINLESS STEEL - SCH5 - 316L - ERW,11.31,32.1
        200,STAINLESS STEEL - SCH5 - 316L - ERW,14.78,50.58
        250,STAINLESS STEEL - SCH5 - 316L - ERW,22.61,78.27
        300,STAINLESS STEEL - SCH5 - 316L - ERW,31.25,109.62
        350,STAINLESS STEEL - SCH5 - 316L - ERW,34.34,129.23
        400,STAINLESS STEEL - SCH5 - 316L - ERW,41.56,165.91
        450,STAINLESS STEEL - SCH5 - 316L - ERW,46.79,204.77
        500,STAINLESS STEEL - SCH5 - 316L - ERW,59.32,254.34
        600,STAINLESS STEEL - SCH5 - 316L - ERW,82.58,364.16";

        // Parsed library
        private static  Dictionary<string, Dictionary<int, WeightRow>> LIB;
        private static  List<(string key, HashSet<string> toks, int len)> LIB_KEYS;
        private static  HashSet<int> LIB_SIZES;
        private static double _totalWeight = 0;

        static PipeWeightCommand()
        {
            
        }

        private static void LoadElemets()
        {
            LIB = new Dictionary<string, Dictionary<int, WeightRow>>(StringComparer.Ordinal);
            LIB_KEYS = new List<(string, HashSet<string>, int)>();
            LIB_SIZES = new HashSet<int>();
            LIB.Clear();
            LIB_KEYS.Clear();
            LIB_SIZES.Clear();

            ParseLibrary(LIB_CSV, LIB, LIB_KEYS, LIB_SIZES);
        }

        private static List<Element> CollectTargetFittings(Document doc, ElementId viewId)
        {
            List<Element> targets = new List<Element>();

            try
            {
                // Fast filter: family+type string contains "nipple" OR "shaped branch" (case-insensitive)
                ParameterValueProvider pvp =
                    new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM));
                FilterStringRuleEvaluator contains = new FilterStringContains();
                FilterStringRule ruleBranch = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "branch");
                ElementParameterFilter f2 = new ElementParameterFilter(ruleBranch);
                LogicalOrFilter orFilter = new LogicalOrFilter(new List<ElementFilter> { f2 });

                targets = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .WherePasses(orFilter)
                    .ToElements()
                    .ToList();
            }
            catch
            {
            }

            return targets;
        }

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
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = uidoc?.ActiveView;

            if (doc == null || view == null)
            {
                AppDialog.Info(uiapp, "Pipe Weight", "No active document/view.");
                return Result.Cancelled;
            }

            LoadElemets();

            var fittings = CollectTargetFittings(doc, view.Id);

            var pipes = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();

            var pipeAndFittings = fittings.Concat(pipes).ToList();

            if (pipeAndFittings.Count == 0)
            {
                AppDialog.Info(uiapp,"Pipe Weight", "No pipes/fittings found in the active view.");
                return Result.Succeeded;
            }

            // Mirror your progress signature requirement//
            //var fittings = pipes; // alias so we can call UpdateSmart(touched, fittings.Count, ...)
          
            string strDryParam = AppConfig.CurrentConfig.PipeWeightMapParameters.DryWeight;
            string strWetParam = AppConfig.CurrentConfig.PipeWeightMapParameters.WetWeight;
            string strCladdingWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.CladdingWeight;
            string strFluidWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.FluidWeight;
            string strInsulationWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.InsulationWeight;
            string strOverallSizeParam = AppConfig.CurrentConfig.PipeWeightMapParameters.ComputedOverallSize;
            string strTotalWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.TotalWeight;

            int numDecimals = AppConfig.CurrentConfig.PipeWeightMapParameters.NumOfDecimals;

            //bool haveDry = pipeAndFittings.Any(e => Elements.HasParam(e,strDryParam));
            //bool haveWet = pipeAndFittings.Any(e => Elements.HasParam(e,strWetParam));

            bool paramsExist = pipeAndFittings.Any(p =>
                Elements.HasParam(p, strDryParam) &&
                Elements.HasParam(p, strWetParam) &&
                Elements.HasParam(p, strCladdingWeightParam) &&
                Elements.HasParam(p, strFluidWeightParam) &&
                Elements.HasParam(p, strInsulationWeightParam) &&
                Elements.HasParam(p, strOverallSizeParam) &&
                Elements.HasParam(p, strTotalWeightParam)//
                );

            if (!paramsExist)//
            {
                AppDialog.Info(uiapp, "Pipe Weight",
                    $"Parameters not found. Pipes must have text parameters: {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.PipeWeightMapParameters)}");
                return Result.Succeeded;
            }

            int matched = 0, written = 0, skippedNoType = 0, skippedNoSize = 0, skippedNoRow = 0, touched = 0;

            var typeTokensCache = new Dictionary<long, HashSet<string>>();
            var libKeyCache = new Dictionary<long, string>();

            var win = new ProgressWindow();

            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.Initialize(pipeAndFittings.Count, "Mapping Pipe's Weight…", "Procesing…");
            win.Show();

            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            try
            {
                using (var tx = new Transaction(doc, "Pipe Weight"))
                {
                    tx.Start();
                    foreach (var e in pipeAndFittings)
                    {

                        Debug.WriteLineIf(Debugger.IsAttached, $"Start===========");

                        Parameter dryParam = e.LookupParameter(strDryParam);
                        Parameter wetParam = e.LookupParameter(strWetParam);
                        Parameter claddingWeightParam = e.LookupParameter(strCladdingWeightParam);
                        Parameter fluidWeightParam = e.LookupParameter(strFluidWeightParam);
                        Parameter insulationWeightParam = e.LookupParameter(strInsulationWeightParam);
                        Parameter overallSizeParam = e.LookupParameter(strOverallSizeParam);
                        Parameter totalWeightParam = e.LookupParameter(strTotalWeightParam);

                        PipeWeightParams pipeWeightParams = new PipeWeightParams
                        {
                            HaveDry = dryParam != null,
                            HaveWet = wetParam != null,
                            HaveCladding = claddingWeightParam != null,
                            HaveFluidWeight = fluidWeightParam != null,
                            HaveInsulationWeight = insulationWeightParam != null,
                            HaveOverallSize = overallSizeParam != null,
                            HaveTotalWeight = totalWeightParam != null,
                            NumDecimals = numDecimals
                        };

                        long tid = RevitApiCompatibility.GetElementIdValue(e.GetTypeId());

                        if (!typeTokensCache.TryGetValue(tid, out var toks))
                        {
                            string tt = GetTypeText(doc, e);
                            toks = ToTokens(tt);
                            typeTokensCache[tid] = toks;
                        }

                        if (!libKeyCache.TryGetValue(tid, out var key))
                        {
                            key = MatchLibKey(toks);
                            libKeyCache[tid] = key;
                        }

                        if (string.IsNullOrEmpty(key))
                        {
                            skippedNoType++;
                            Elements.PipeWeight.AssignDefault(e, pipeWeightParams);
                            continue;
                        }

                        string sizeRaw = GetSizeRaw(doc, e);
                        
                        int? sizeMm = PickSizeMm(sizeRaw);
                        
                        if (!sizeMm.HasValue)
                        {
                            skippedNoSize++;
                            Elements.PipeWeight.AssignDefault(e, pipeWeightParams);
                            continue;
                        }

                        if (!LIB.TryGetValue(key, out var bySize) || !bySize.TryGetValue(sizeMm.Value, out var row))
                        {
                            skippedNoRow++;
                            Elements.PipeWeight.AssignDefault(e, pipeWeightParams);
                            continue;
                        }

                        double lengthM = GetLengthM(e);
                        Debug.WriteLineIf(Debugger.IsAttached, $"Length: {lengthM}");
                       
                        double dryDouble = (row.DryKgPerM * lengthM);  
                        double wetDouble = (row.WetKgPerM * lengthM);

                        if (dryDouble < 0.5)
                            dryDouble = ForceWhole(dryDouble);

                        if (wetDouble < 0.5)
                            wetDouble = ForceWhole(wetDouble);

                        matched++;

                        bool didWrite = false;
                        bool ok = true;

                        if (pipeWeightParams.HaveDry)
                        {
                            if (dryParam != null && !dryParam.IsReadOnly)
                            {
                                try
                                {
                                    switch (dryParam.StorageType)
                                    {
                                        case StorageType.Double:
                                            {
                                                double current;
                                                bool has = TryGetDouble(dryParam, out current);
                                                if (!has || Math.Abs(current - dryDouble) > 1e-9)
                                                {
                                                    dryParam.Set((double)dryDouble);
                                                }

                                                didWrite = true;
                                                break;
                                            }
                                        case StorageType.Integer:
                                            {
                                                int current;
                                                bool has = TryGetInt(dryParam, out current);
                                                if (!has || current != dryDouble)
                                                {
                                                    dryParam.Set(dryDouble);
                                                }

                                                didWrite = true;
                                                break;
                                            }
                                        case StorageType.String:
                                            {
                                                string nv = dryDouble.ToString($"F{numDecimals}");
                                                string cur = TryGetString(dryParam);
                                                if (cur != nv)
                                                {
                                                    dryParam.Set(nv);
                                                }

                                                _totalWeight += nv.ToDouble();

                                                didWrite = true;
                                                break;
                                            }
                                        default:
                                            {
                                                string nv = dryDouble.ToString(CultureInfo.InvariantCulture);
                                                try { dryParam.SetValueString(nv); didWrite = true; }
                                                catch { ok = false; }
                                                break;
                                            }
                                    }
                                }
                                catch { ok = false; }
                            }
                            else ok = false;
                        }

                        if (ok && pipeWeightParams.HaveWet)
                        {
                            if (wetParam != null && !wetParam.IsReadOnly)
                            {
                                try
                                {
                                    string wetStr = wetDouble.ToString($"F{numDecimals}");
                                    if (wetParam.StorageType == StorageType.String)
                                    {
                                        string cur = TryGetString(wetParam);
                                        if (cur != wetStr)
                                        {
                                            wetParam.Set(wetStr);
                                            
                                        }
                                        _totalWeight += wetStr.ToDouble();

                                        didWrite = true;
                                    }
                                    else
                                    {
                                        bool setOk = false;
                                        try
                                        {
                                            string curVs = null;
                                            try { curVs = wetParam.AsValueString(); } catch { }
                                            if (curVs == null || !StringEqualsNumeric(curVs, wetStr))
                                            {
                                                wetParam.SetValueString(wetStr);
                                                setOk = true;
                                                didWrite = true;
                                            }
                                        }
                                        catch
                                        {
                                            // fall back to numeric
                                        }

                                        if (!setOk)
                                        {
                                            try { wetParam.Set((double)wetDouble); didWrite = true; }
                                            catch { ok = false; }
                                        }
                                    }
                                }
                                catch { ok = false; }
                            }
                            else ok = false;
                        }

                        if(pipeWeightParams.HaveCladding)
                            AssignCladdingWeight(claddingWeightParam, e);
                        
                        if(pipeWeightParams.HaveFluidWeight) 
                            AssignFluidWeight(fluidWeightParam, e);
                        
                        if(pipeWeightParams.HaveInsulationWeight)
                            AssignInsulationWeight(insulationWeightParam, e);
                        
                        if(pipeWeightParams.HaveOverallSize)
                            AssignOverallSize(overallSizeParam, e);

                        if (pipeWeightParams.HaveTotalWeight)
                            AssignTotalWeight(totalWeightParam);//

                        if (ok && didWrite)
                        {
                            written++;
                            touched++;
                        }
                       
                        win.UpdateSmart(touched, pipeAndFittings.Count, $"Mapping… {touched} / {fittings.Count}");
                        _totalWeight = 0;
                        
                        Debug.WriteLineIf(Debugger.IsAttached, $"End ===========");
                    }
                    tx.Commit();
                    win.UpdateSmart(touched, pipeAndFittings.Count, "Finalizing…", force: true);                    
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Mapping Cancelled", touched, "Mapping pipe's weight has been cancelled");
                    return Result.Cancelled;
                }

                win.Done($"Successfully updated {touched} pipe's weight.", pipeAndFittings.Count, "Mapping Pipe's Weight Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void AssignOverallSize(Parameter param, Element element)
        {
            var decimalPlaces = AppConfig.CurrentConfig.PipeWeightMapParameters.NumOfDecimals;

            string overallSize = (element.GetOverallSizeM().ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture).ToDouble() * 1000).ToString("G15", CultureInfo.InvariantCulture);
           
            Debug.WriteLineIf(Debugger.IsAttached, $"Overall Size: {overallSize}");
            
            if (param.StorageType == StorageType.String)
                param.Set(overallSize);
        }

        private void AssignTotalWeight(Parameter param)
        {
            param.Set(_totalWeight.ToString("G15", CultureInfo.InvariantCulture));
        }

        private void AssignCladdingWeight(Parameter param, Element element)
        {
            try
            {
                var pipeWeightConfig = AppConfig.CurrentConfig.PipeWeightMaterialProperties;
                var decimalPlaces = AppConfig.CurrentConfig.PipeWeightMapParameters.NumOfDecimals;
                
                string strCladdingWeight = 0.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
                
                if (!param.IsReadOnly)
                {
                    double claddingThicknessM = pipeWeightConfig.CladdingThickness;
                    double overallSizeM = 0;
                    double length = GetLengthM(element);
                    double claddingDensity = pipeWeightConfig.CladdingDensity;

                    overallSizeM = element.GetOverallSizeM().ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture).ToDouble();//

                    if(overallSizeM > 0)
                    {
                        double claddingWeight = PI * overallSizeM * claddingThicknessM * length * claddingDensity;

                        strCladdingWeight = claddingWeight.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
                    }

                    if(param.StorageType == StorageType.String)
                        param.Set(strCladdingWeight);
                    
                    _totalWeight += strCladdingWeight.ToDouble();
//                  
                }
            }
            catch (Exception)
            {

            }
        }

        private void AssignFluidWeight(Parameter param, Element element)
        {
            try
            {
                var decimalPlaces = AppConfig.CurrentConfig.PipeWeightMapParameters.NumOfDecimals;
               
                double abbreviationDensity = 0;
                double insideDiameter = 0;
               
                if (!param.IsReadOnly)
                {
                    var length = GetLengthM(element);

                    string strFluidWeight = 0.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);

                    Parameter systemAbParam = element.LookupParameter("System Abbreviation");

                    string systemAbbreviation = systemAbParam.AsValueString();

                    Debug.WriteLineIf(Debugger.IsAttached, $"System Abbreviation: {systemAbbreviation}");

                    abbreviationDensity = Elements.PipeWeight.GetSystemAbbreviationDensity(systemAbbreviation);

                    Parameter insideDiameterParam = element.LookupParameter("Inside Diameter");

                    if (insideDiameterParam != null)
                        insideDiameter = insideDiameterParam.AsDouble().FeetToM();

                    Debug.WriteLineIf(Debugger.IsAttached, $"Inside Diameter: {insideDiameter}");

                    strFluidWeight = (abbreviationDensity * PI * (Math.Pow((insideDiameter / 2), 2)) * length).ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);

                    if(param.StorageType == StorageType.String)
                        param.Set(strFluidWeight);
                    
                    _totalWeight += strFluidWeight.ToDouble();
                }
            }

            catch (Exception)
            {

            }
        }

        private void AssignInsulationWeight(Parameter param, Element element)
        {
            try
            {
                var decimalPlaces = AppConfig.CurrentConfig.PipeWeightMapParameters.NumOfDecimals;
                double outsideDiameter = 0;

                if (!param.IsReadOnly)
                {
                    var length = GetLengthM(element);

                    string strInsulationWeight = 0.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);

                    double overallSizeM = element.GetOverallSizeM().ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture).ToDouble();

                    Parameter outsideDiameterParameter = element.LookupParameter("Outside Diameter");

                    if (outsideDiameterParameter != null)
                        outsideDiameter = outsideDiameterParameter.AsDouble().FeetToM();

                    Debug.WriteLineIf(Debugger.IsAttached, $"Outside Diameter: {outsideDiameter}");

                    strInsulationWeight = (PI * length * (Math.Pow((overallSizeM / 2), 2) - Math.Pow((outsideDiameter / 2), 2)) * 32).ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);

                    if(param.StorageType == StorageType.String)
                        param.Set(strInsulationWeight);
                    
                    _totalWeight += strInsulationWeight.ToDouble();
                }
            }

            catch (Exception)
            {

            }
        }

        // ========= Helpers (ported from Python) =========

        private static int ForceWhole(double n) => (n < 0.5) ? 1 : RoundHalfUp(n);

        private static int RoundHalfUp(double n) => n >= 0 ? (int)Math.Floor(n + 0.5) : -(int)Math.Floor(-n + 0.5);

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.ToLowerInvariant();
            s = NonAlnum.Replace(s, " ").Trim();
            return s;
        }

        private static HashSet<string> ToTokens(string s)
        {
            var norm = Norm(s);
            if (string.IsNullOrEmpty(norm)) return new HashSet<string>();
            return new HashSet<string>(norm.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string MatchLibKey(HashSet<string> typeTokens)
        {
            foreach (var (key, toks, _) in LIB_KEYS)
            {
                if (IsSubset(toks, typeTokens)) return key;
            }
            return null;
        }

        private static bool IsSubset(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0) return true;
            foreach (var t in a) if (!b.Contains(t)) return false;
            return true;
        }

        private static string GetTypeText(Document doc, Element e)
        {
            var tid = e.GetTypeId();
            var type = tid != ElementId.InvalidElementId ? doc.GetElement(tid) : null;

            if (type != null)
            {
                var pDesc = type.LookupParameter("Description");
                if (pDesc != null && pDesc.HasValue)
                {
                    var s = pDesc.AsString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }

                var nameProp = type.GetType().GetProperty("Name");
                if (nameProp != null)
                {
                    var n = nameProp.GetValue(type) as string;
                    if (!string.IsNullOrWhiteSpace(n)) return n;
                }
            }

            var pDesc2 = e.LookupParameter("Description");
            if (pDesc2 != null && pDesc2.HasValue)
            {
                var s2 = pDesc2.AsString();
                if (!string.IsNullOrWhiteSpace(s2)) return s2;
            }

            return string.Empty;
        }

        private static string GetSizeRaw(Document doc, Element e)
        {
            foreach (var host in new Element[] { e, doc.GetElement(e.GetTypeId()) })
            {
                if (host == null) continue;
                var p = host.LookupParameter("Size");
                if (p != null && p.HasValue)
                {
                    try
                    {
                        var s = p.AsString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                    catch { }

                    try
                    {
                        double d = p.AsDouble(); // feet?
                        if (d > 0)
                        {
                            int mm = (int)Math.Round(d * 304.8);
                            return mm.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    catch { }
                }
            }

            var pd = e.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) ?? e.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

            if (pd != null && pd.HasValue)
            {
                try
                {
                    double ft = pd.AsDouble();
                    int mm = (int)Math.Round(ft * 304.8);
                    return mm.ToString(CultureInfo.InvariantCulture);
                }
                catch { }
            }

            return string.Empty;
        }

        private static int? PickSizeMm(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var matches = Digits.Matches(s.ToLowerInvariant());
            if (matches.Count == 0) return null;

            int? best = null;
            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Value, out int v)) continue;
                if (!LIB_SIZES.Contains(v)) continue;
                if (!best.HasValue || v > best.Value) best = v;
            }
            return best;
        }

        private static double GetLengthM(Element e)
        {
            var loc = e.Location as LocationCurve;
            double lengthFt;
            if (loc != null && loc.Curve != null)
            {
                lengthFt = loc.Curve.Length;
            }
            else
            {
                var p = e.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                lengthFt = (p != null && p.HasValue) ? p.AsDouble() : 0.0;
            }
            return lengthFt * FT_TO_M;
        }

        private static bool TryGetDouble(Parameter p, out double value)
        {
            value = 0;
            try { if (p.StorageType == StorageType.Double) { value = p.AsDouble(); return true; } }
            catch { }
            return false;
        }

        private static bool TryGetInt(Parameter p, out int value)
        {
            value = 0;
            try { if (p.StorageType == StorageType.Integer) { value = p.AsInteger(); return true; } }
            catch { }
            return false;
        }

        private static string TryGetString(Parameter p)
        {
            try
            {
                if (p.StorageType == StorageType.String) return p.AsString();
                return p.AsValueString();
            }
            catch { return null; }
        }

        private static bool StringEqualsNumeric(string a, string b)
        {
            if (double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var da) &&
                double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var db))
            {
                return Math.Abs(da - db) <= 1e-9;
            }
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static void ParseLibrary(
            string csv,
            Dictionary<string, Dictionary<int, WeightRow>> lib,
            List<(string key, HashSet<string> toks, int len)> keys,
            HashSet<int> sizesSet)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;

            var lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim())
                           .ToList();

            var elementsWeight = AppConfig.CurrentConfig.ElementsWeight;

            foreach (var item in elementsWeight)
            {
                try
                {
                    int sz = (int)Math.Round(double.Parse(item.Size.ToString(), CultureInfo.InvariantCulture));
                    string pt = item.PipeType;
                    double dry = double.Parse(item.DryWeight.ToString(), CultureInfo.InvariantCulture);
                    double wet = double.Parse(item.WetWeight.ToString(), CultureInfo.InvariantCulture);

                    string k = Norm(pt);
                    if (!lib.TryGetValue(k, out var bySize))
                    {
                        bySize = new Dictionary<int, WeightRow>();
                        lib[k] = bySize;
                    }
                    bySize[sz] = new WeightRow(dry, wet);
                    sizesSet.Add(sz);
                }
                catch { /* ignore */ }
            }

            // skip header
            //for (int i = 1; i < lines.Count; i++)
            //{
            //    var parts = lines[i].Split(',');
            //    if (parts.Length < 4) continue;

            //    try
            //    {
            //        int sz = (int)Math.Round(double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture));
            //        string pt = parts[1].Trim();
            //        double dry = double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
            //        double wet = double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture);

            //        string k = Norm(pt);
            //        if (!lib.TryGetValue(k, out var bySize))
            //        {
            //            bySize = new Dictionary<int, WeightRow>();
            //            lib[k] = bySize;
            //        }
            //        bySize[sz] = new WeightRow(dry, wet);
            //        sizesSet.Add(sz);
            //    }
            //    catch { /* ignore */ }
            //}

            foreach (var k in lib.Keys)
                keys.Add((k, ToTokens(k), k.Length));

            keys.Sort((a, b) => b.len.CompareTo(a.len)); // longest first
        }

        private readonly struct WeightRow
        {
            public readonly double DryKgPerM;
            public readonly double WetKgPerM;
            public WeightRow(double dry, double wet) { DryKgPerM = dry; WetKgPerM = wet; }
        }
    }
}
