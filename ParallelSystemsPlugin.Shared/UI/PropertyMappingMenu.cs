using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin.Configs;

namespace ParallelSystemPlugin.UI
{
    public class PropertyMappingMenu
    {
        public static PropertyMappingMenu Instance { get; private set; }

        public SplitButton PipeSplit { get; private set; }
        public PushButton PipeMapButton { get; private set; }

        public SplitButton FitSplit { get; private set; }
        public PushButton FitMapButton { get; private set; }

        public SplitButton HeaderSplit { get; private set; }
        public PushButton HeaderMapButton { get; private set; }

        public SplitButton WeightSplit { get; private set; }
        public PushButton WeightMapButton { get; private set; }

        private readonly List<(SplitButton split, PushButton top)> _splits = new List<(SplitButton, PushButton)>();

        private enum IconSize { Small16, Large32, Both }

        public static void Build(RibbonPanel panel)
        {
            Instance = new PropertyMappingMenu();
            Instance.BuildInternal(panel);
        }

        private void BuildInternal(RibbonPanel panel)
        {
            if (panel == null) return;

            string asmPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            if (string.IsNullOrEmpty(asmPath)) return;

            string asmDir = Path.GetDirectoryName(asmPath);
            var pipeConfigs = ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.PipeMapParameters;
            var fittingsConfigs = ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.FittingsMapParameters;
            // ===== PIPE =====
            {
                string mapPipeToolTip = $"Set {pipeConfigs.End1}, {pipeConfigs.End2}, and {pipeConfigs.EndPrep} on all pipes in the active view.";
                var map = MakeButton<Commands.MapPipesCommand>(
                    "PS_MapPipes", "Map Pipe End Prep",
                    mapPipeToolTip,
                    longDesc: null, iconBase: "MapPipes", asmPath, asmDir);

                string clearPipeToolTip = $"Clear {pipeConfigs.End1}, {pipeConfigs.End2}, and {pipeConfigs.EndPrep} on all pipes in the active view.";
                var clr = MakeButton<Commands.ClearPipesCommand>(
                    "PS_ClearPipes", "Clear Pipe End Prep",
                    clearPipeToolTip,
                    longDesc: null, iconBase: "ClearPipes", asmPath, asmDir);

                (PipeSplit, PipeMapButton) = AddSplit(panel, "PS_PipeEndPrepSplit", "Map Pipe End Prep", map, clr);
                _splits.Add((PipeSplit, PipeMapButton));
            }

            // ===== FITTINGS =====
            {
                string mapFittingsToolTip = $"Set {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.FittingsMapParameters,false, false)} for {ParallelSystemsPlugin.Helpers.Config.BuildFittingsAllowedMapping(false)}.";
                var map = MakeButton<Commands.MapFittingsCommand>(
                    "PS_MapFittings", "Map Fittings End Prep",
                    mapFittingsToolTip,
                    null, "MapFittings", asmPath, asmDir);

                string clearFittingsToolTIp = $"Clear {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.FittingsMapParameters,false, false)} for {ParallelSystemsPlugin.Helpers.Config.BuildFittingsAllowedMapping(false)}.";
                var clr = MakeButton<Commands.ClearFittingsCommand>(
                    "PS_ClearFittings", "Clear Fittings End Prep",
                    clearFittingsToolTIp,
                    null, "ClearFittings", asmPath, asmDir);

                (FitSplit, FitMapButton) = AddSplit(panel, "PS_FittingsEndPrepSplit", "Map Fittings End Prep", map, clr);
                _splits.Add((FitSplit, FitMapButton));
            }

            // ===== HEADER ND =====
            //{
            //    var map = MakeButton<Commands.MapHeaderNDCommand>(
            //        "PS_MapHeaderND", "Map Header ND",
            //        "Set Header ND on threaded nipple, shaped-branch, and BSP socket fittings to the largest connected pipe size.",
            //        null, "MapHeaderND", asmPath, asmDir);

            //    var clr = MakeButton<Commands.ClearHeaderNDCommand>(
            //        "PS_ClearHeaderND", "Clear Header ND",
            //        "Clear Header ND on nipple, shaped-branch, and BSP socket fittings.",
            //        null, "ClearHeaderND", asmPath, asmDir);

            //    (HeaderSplit, HeaderMapButton) = AddSplit(panel, "PS_HeaderNDSplit", "Map Header ND", map, clr);
            //    _splits.Add((HeaderSplit, HeaderMapButton));
            //}

            // ===== PIPE WEIGHT =====
            {
                var map = MakeButton<Commands.PipeWeightCommand>(
                    "PS_PipeWeight", "Pipe Weight",
                    $"Compute and write {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.PipeWeightMapParameters,false, false)} for pipes in the active view using the size/type configurations.",
                    $"Matches pipe type/size against the configurations and writes totals based on element length to {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.PipeWeightMapParameters, true, false)}.",
                    "PipeWeight", asmPath, asmDir);

                var clr = MakeButton<Commands.ClearPipeWeightCommand>(
                    "PS_ClearPipeWeight", "Clear Pipe Weight",
                    $"Clear the {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.PipeWeightMapParameters, false, false)}  parameters on pipes in the active view.",
                    null, "ClearPipeWeight", asmPath, asmDir);

                (WeightSplit, WeightMapButton) = AddSplit(panel, "PS_PipeWeightSplit", "Pipe Weight", map, clr);
                _splits.Add((WeightSplit, WeightMapButton));
            }
        }

        public static void ResetDefaults()
        {
            if (Instance == null) return;
            foreach (var (split, top) in Instance._splits)
            {
                if (split != null && top != null) split.CurrentButton = top;
            }
        }

        // -------- Helpers --------

        private static (SplitButton split, PushButton top) AddSplit(
            RibbonPanel panel,
            string splitId,
            string splitText,
            PushButtonData topButton,
            params PushButtonData[] dropdownButtons)
        {
            var split = panel.AddItem(new SplitButtonData(splitId, splitText)) as SplitButton;
            var top = split?.AddPushButton(topButton) as PushButton;

            if (dropdownButtons != null)
            {
                foreach (var pbd in dropdownButtons)
                {
                    split?.AddPushButton(pbd);
                }
            }

            if (split != null && top != null) split.CurrentButton = top;
            return (split, top);
        }

        private static PushButtonData MakeButton<TCommand>(
            string id,
            string text,
            string tooltip,
            string longDesc,
            string iconBase,
            string asmPath,
            string asmDir,
            IconSize iconSize = IconSize.Large32)
        {
            var pbd = new PushButtonData(id, text, asmPath, typeof(TCommand).FullName)
            {
                ToolTip = tooltip,
                LongDescription = longDesc
            };
            SetIcon(pbd, asmDir, iconBase, iconSize);
            return pbd;
        }

        private static void SetIcon(PushButtonData pbd, string asmDir, string baseName, IconSize size)
        {
            if (string.IsNullOrWhiteSpace(baseName)) return;

            string i16 = Path.Combine(asmDir, "Icons", $"{baseName}16.ico");
            string i32 = Path.Combine(asmDir, "Icons", $"{baseName}32.ico");

            if ((size == IconSize.Small16 || size == IconSize.Both) && File.Exists(i16))
                pbd.Image = new BitmapImage(new Uri(i16));

            if ((size == IconSize.Large32 || size == IconSize.Both) && File.Exists(i32))
                pbd.LargeImage = new BitmapImage(new Uri(i32));
        }
    }
}
