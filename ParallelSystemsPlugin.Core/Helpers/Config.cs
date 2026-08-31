using Newtonsoft.Json;
using ParallelSystemsPlugin.Models;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Helpers
{
    public static class Config
    {
        public static string GetConfigPath()
        {
            string revitYear = Configs.RevitConfig.RevitYear;

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk",
                "Revit",
                "Addins",
                revitYear,
                "ParallelSystemPlugin",
                "Config");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            return Path.Combine(folder, "Configuration.json");
        }

        public static ApplicationConfig Load(bool startup = false)
        {
            var defaultConfig = new ApplicationConfig();

            try
            {
                string path = GetConfigPath();

                if (!File.Exists(path))
                {
                    defaultConfig = GetDefaultConfig();
                    Save(defaultConfig);
                    return defaultConfig;
                }

                // Deserialize using Newtonsoft.Json
                string json = File.ReadAllText(path, Encoding.UTF8);

                var config = JsonConvert.DeserializeObject<ApplicationConfig>(json);

                if (config != null && config.HasNullProperty())
                    ApplyDefaults(config, GetDefaultConfig());

                if (startup)
                    Configs.AppConfig.CurrentConfig = config ?? defaultConfig;

                return config ?? defaultConfig;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return defaultConfig;
        }

        public static void Save(ApplicationConfig config)
        {
            string path = GetConfigPath();

            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(path, json, Encoding.UTF8);

            Configs.AppConfig.CurrentConfig = config;
        }

        public static void ApplyDefaults(ApplicationConfig current, ApplicationConfig defaults)
        {
            var properties = typeof(ApplicationConfig)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in properties)
            {
                var currentValue = prop.GetValue(current);

                if (currentValue == null)
                {
                    var defaultValue = prop.GetValue(defaults);
                    prop.SetValue(current, defaultValue);
                }
            }
        }

        public static ApplicationConfig GetDefaultConfig()
        {
            var defaultConfig = new ApplicationConfig();

            try
            {
                // Pipe End Prep
                // Map Parameters
                defaultConfig.PipeMapParameters = new MapParameters();
                defaultConfig.PipeMapParameters.End1 = "C1";
                defaultConfig.PipeMapParameters.End2 = "C2";
                defaultConfig.PipeMapParameters.EndPrep = "Pipe End Prep";
                defaultConfig.PipeMapParameters.Unconnected = "Unconnected";
                defaultConfig.PipeMapParameters.EnableMapping = false;

                // Ignore Components
                defaultConfig.PipeIgnoreComponents = new List<IgnoreComponent>
                {
                    new IgnoreComponent { NameContains = "weld" },
                    new IgnoreComponent { NameContains = "insulations" },
                    new IgnoreComponent { NameContains = "non-connector" }
                };

                // Pipe End Prep
                defaultConfig.PipeEndPreps = new List<EndPrep>
                {
                    new EndPrep { NameContains = "elbow",    Value = "BE" },
                    new EndPrep { NameContains = "tee",      Value = "BE" },
                    new EndPrep { NameContains = "cross",    Value = "BE" },
                    new EndPrep { NameContains = "reducer",  Value = "BE" },
                    new EndPrep { NameContains = "lateral",  Value = "BE" },
                    new EndPrep { NameContains = "wye",      Value = "BE" },
                    new EndPrep { NameContains = "stub end", Value = "BE" },
                    new EndPrep { NameContains = "end",      Value = "BE" },
                    new EndPrep { NameContains = "flange",   Value = "PE" },
                    new EndPrep { NameContains = "coupling", Value = "RG" },
                    new EndPrep { NameContains = "branch",   Value = "SC" }
                };

                //Fittings End Prep
                // Map Parameters
                defaultConfig.FittingsMapParameters = new MapParameters();
                defaultConfig.FittingsMapParameters.End1 = "C1";
                defaultConfig.FittingsMapParameters.End2 = "C2";
                defaultConfig.FittingsMapParameters.EndPrep = "Pipe End Prep";
                defaultConfig.FittingsMapParameters.Unconnected = "Unconnected";
                defaultConfig.FittingsMapParameters.EnableMapping = false;
                defaultConfig.FittingsMapParameters.HeaderND = "Header ND";

                // Ignore Components
                defaultConfig.FittingsIgnoreComponents = new List<IgnoreComponent>
                {
                    new IgnoreComponent { NameContains = "weld" },
                    new IgnoreComponent { NameContains = "insulations" },
                    new IgnoreComponent { NameContains = "non-connector" }
                };

                // Fittings End Prep
                defaultConfig.FittingsEndPreps = new List<EndPrep>
                {
                    new EndPrep { NameContains = "SCH5",     Value = "SC" },
                    new EndPrep { NameContains = "SCH10",    Value = "SC" },
                    new EndPrep { NameContains = "SCH40",    Value = "SC" },
                    new EndPrep { NameContains = "TUBE",     Value = "SC" },
                    new EndPrep { NameContains = "branch",   Value = "SC" },
                    new EndPrep { NameContains = "nipple",   Value = "THR" }
                };

                // Allow Map Fittings To
                defaultConfig.AllowedMapFittingsElements = new List<AllowedMapFittingsElement>
                {
                    new AllowedMapFittingsElement {NameContains = "nipple" },
                    new AllowedMapFittingsElement {NameContains = "shaped branch" }
                };

                // Pipe Weight
                defaultConfig.PipeWeightMapParameters = new MapParameters();
                defaultConfig.PipeWeightMapParameters.DryWeight = "Dry Weight";
                defaultConfig.PipeWeightMapParameters.WetWeight = "Wet Weight";
                defaultConfig.PipeWeightMapParameters.CladdingWeight = "Cladding Weight";
                defaultConfig.PipeWeightMapParameters.FluidWeight = "Fluid Weight";
                defaultConfig.PipeWeightMapParameters.InsulationWeight = "Insulation Weight";
                defaultConfig.PipeWeightMapParameters.TotalWeight = "Total Weight";
                defaultConfig.PipeWeightMapParameters.ComputedOverallSize = "Computed Overall Size";

                defaultConfig.PipeWeightMaterialProperties = new MaterialProperties();
                defaultConfig.PipeWeightMaterialProperties.CladdingThickness = 0.0006;
                defaultConfig.PipeWeightMaterialProperties.CladdingDensity = 2740;
                defaultConfig.PipeWeightMaterialProperties.InsulationDensity = 32;

                defaultConfig.ElementsWeight = new List<ElementsWeight>
                {
                    new ElementsWeight { Size = 15, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 1.0m, WetWeight = 1.22m },
                    new ElementsWeight { Size = 20, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 1.28m, WetWeight = 1.67m },
                    new ElementsWeight { Size = 25, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 2.09m, WetWeight = 2.69m },
                    new ElementsWeight { Size = 32, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 2.69m, WetWeight = 3.74m },
                    new ElementsWeight { Size = 40, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 3.11m, WetWeight = 4.54m },
                    new ElementsWeight { Size = 50, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 3.93m, WetWeight = 6.28m },
                    new ElementsWeight { Size = 65, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 5.26m, WetWeight = 8.77m },
                    new ElementsWeight { Size = 80, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 6.46m, WetWeight = 11.84m },
                    new ElementsWeight { Size = 100, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 8.37m, WetWeight = 17.56m },
                    new ElementsWeight { Size = 125, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 11.56m, WetWeight = 25.76m },
                    new ElementsWeight { Size = 150, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 13.83m, WetWeight = 34.3m },
                    new ElementsWeight { Size = 200, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 19.97m, WetWeight = 55.11m },
                    new ElementsWeight { Size = 250, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 27.79m, WetWeight = 82.8m },
                    new ElementsWeight { Size = 300, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 35.99m, WetWeight = 113.76m },
                    new ElementsWeight { Size = 350, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 41.36m, WetWeight = 135.35m },
                    new ElementsWeight { Size = 400, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 47.34m, WetWeight = 170.96m },
                    new ElementsWeight { Size = 450, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 53.31m, WetWeight = 210.46m },
                    new ElementsWeight { Size = 500, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 68.65m, WetWeight = 262.48m },
                    new ElementsWeight { Size = 600, PipeType = "STAINLESS STEEL - SCH10", DryWeight = 94.53m, WetWeight = 374.59m },

                    new ElementsWeight { Size = 15, PipeType = "CARBON STEEL - STD", DryWeight = 1.27m, WetWeight = 1.46m },
                    new ElementsWeight { Size = 20, PipeType = "CARBON STEEL - STD", DryWeight = 1.69m, WetWeight = 2.03m },
                    new ElementsWeight { Size = 25, PipeType = "CARBON STEEL - STD", DryWeight = 2.5m, WetWeight = 3.05m },
                    new ElementsWeight { Size = 32, PipeType = "CARBON STEEL - STD", DryWeight = 3.39m, WetWeight = 4.35m },
                    new ElementsWeight { Size = 40, PipeType = "CARBON STEEL - STD", DryWeight = 4.05m, WetWeight = 5.36m },
                    new ElementsWeight { Size = 50, PipeType = "CARBON STEEL - STD", DryWeight = 5.44m, WetWeight = 7.6m },
                    new ElementsWeight { Size = 65, PipeType = "CARBON STEEL - STD", DryWeight = 8.63m, WetWeight = 11.71m },
                    new ElementsWeight { Size = 80, PipeType = "CARBON STEEL - STD", DryWeight = 11.29m, WetWeight = 16.05m },
                    new ElementsWeight { Size = 100, PipeType = "CARBON STEEL - STD", DryWeight = 16.08m, WetWeight = 24.28m },
                    new ElementsWeight { Size = 125, PipeType = "CARBON STEEL - STD", DryWeight = 21.77m, WetWeight = 34.67m },
                    new ElementsWeight { Size = 150, PipeType = "CARBON STEEL - STD", DryWeight = 28.26m, WetWeight = 46.89m },
                    new ElementsWeight { Size = 200, PipeType = "CARBON STEEL - STD", DryWeight = 42.55m, WetWeight = 74.81m },
                    new ElementsWeight { Size = 250, PipeType = "CARBON STEEL - STD", DryWeight = 60.29m, WetWeight = 111.11m },
                    new ElementsWeight { Size = 300, PipeType = "CARBON STEEL - STD", DryWeight = 73.86m, WetWeight = 146.76m },
                    new ElementsWeight { Size = 350, PipeType = "CARBON STEEL - STD", DryWeight = 81.33m, WetWeight = 170.23m },
                    new ElementsWeight { Size = 400, PipeType = "CARBON STEEL - STD", DryWeight = 93.27m, WetWeight = 211.04m },
                    new ElementsWeight { Size = 450, PipeType = "CARBON STEEL - STD", DryWeight = 105.17m, WetWeight = 255.72m },
                    new ElementsWeight { Size = 500, PipeType = "CARBON STEEL - STD", DryWeight = 117.15m, WetWeight = 304.81m },
                    new ElementsWeight { Size = 600, PipeType = "CARBON STEEL - STD", DryWeight = 141.12m, WetWeight = 415.24m },

                    new ElementsWeight { Size = 15, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 0.8m, WetWeight = 1.05m },
                    new ElementsWeight { Size = 20, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 1.02m, WetWeight = 1.44m },
                    new ElementsWeight { Size = 25, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 1.29m, WetWeight = 2.0m },
                    new ElementsWeight { Size = 32, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 1.65m, WetWeight = 2.83m },
                    new ElementsWeight { Size = 40, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 1.9m, WetWeight = 3.48m },
                    new ElementsWeight { Size = 50, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 2.39m, WetWeight = 4.94m },
                    new ElementsWeight { Size = 65, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 3.69m, WetWeight = 7.4m },
                    new ElementsWeight { Size = 80, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 4.52m, WetWeight = 10.14m },
                    new ElementsWeight { Size = 100, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 5.84m, WetWeight = 15.35m },
                    new ElementsWeight { Size = 125, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 9.46m, WetWeight = 23.92m },
                    new ElementsWeight { Size = 150, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 11.31m, WetWeight = 32.1m },
                    new ElementsWeight { Size = 200, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 14.78m, WetWeight = 50.58m },
                    new ElementsWeight { Size = 250, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 22.61m, WetWeight = 78.27m },
                    new ElementsWeight { Size = 300, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 31.25m, WetWeight = 109.62m },
                    new ElementsWeight { Size = 350, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 34.34m, WetWeight = 129.23m },
                    new ElementsWeight { Size = 400, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 41.56m, WetWeight = 165.91m },
                    new ElementsWeight { Size = 450, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 46.79m, WetWeight = 204.77m },
                    new ElementsWeight { Size = 500, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 59.32m, WetWeight = 254.34m },
                    new ElementsWeight { Size = 600, PipeType = "STAINLESS STEEL - SCH5 - 316L - ERW", DryWeight = 82.58m, WetWeight = 364.16m }
                };

                defaultConfig.SystemAbbreviations = new List<SystemAbbreviation>
                {
                    new SystemAbbreviation { AbbreviationContains = "glyufhs", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "glyufhr", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "chwf", Density = 999.94 },
                    new SystemAbbreviation { AbbreviationContains = "chwr", Density = 999.94 },
                    new SystemAbbreviation { AbbreviationContains = "pllt", Density = 677 },
                    new SystemAbbreviation { AbbreviationContains = "plht", Density = 650.6 },
                    new SystemAbbreviation { AbbreviationContains = "wrht", Density = 9.8196 },
                    new SystemAbbreviation { AbbreviationContains = "wrlt", Density = 1.038 },
                    new SystemAbbreviation { AbbreviationContains = "-53ds", Density = 1.038 },
                    new SystemAbbreviation { AbbreviationContains = "-45ds", Density = 1.038 },
                    new SystemAbbreviation { AbbreviationContains = "glys", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "glyr", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "scl", Density = 669.8 },
                    new SystemAbbreviation { AbbreviationContains = "hg", Density = 10.5 },
                    new SystemAbbreviation { AbbreviationContains = "hhwf", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "hhwr", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "ccwr", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "ccwf", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "ccwbl", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "ccwbp", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "chwbp", Density = 1000 },
                    new SystemAbbreviation { AbbreviationContains = "hhwbp", Density = 1000 }
                };

                defaultConfig.Procurement = new ProcurementConfig
                {
                    CompanyLogoPath = "",
                    ClientLogoPath = "",
                    JobNumber = "",
                    JobName = "",
                    TargetFolder = "",
                    Date = DateTime.Today,

                    BomAssemblyRegister = true,
                    BomCutList = true,
                    BomFittingReport = true,
                    BomLoadingReport = true,
                    BomPipeReport = true,
                    LabelReport = true,
                    BomFieldMaterialReport = true,
                    BomAccessoryReport = true,
                    IncludeSiteMeasure = false,
                    ExportReportsToExcel = true,

                    CutListMaximumLength = 6000,
                    CutListBladeThickness = 2,
                    CutListNegativeAllowance = 0,
                    OffcutThreshold = 2500,
                };

                defaultConfig.ToolsConfig = new ToolsConfig
                {
                    PipeSlopeConfig = new PipeSlopeConfig
                    {
                        AllowedAngles = new List<double> { 0.0, 45.0, 90.0 }
                        ,
                        AcceptedTolerance = 0.0
                    },

                    RenamingConfig = new RenamingConfig
                    {
                        CsvPath = ""
                    },

                    PipeFilterConfig = new PipeFilterConfig()
                    
                };

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return defaultConfig;
        }
        public static string BuildMapParametersConfig(MapParameters mapParameters, bool withQuote = true, bool withPeriodOnLast = true)
        {

            string end1 = mapParameters.End1;
            string end2 = mapParameters.End2;
            string endPrep = mapParameters.EndPrep;
            string headerND = mapParameters.HeaderND;
            string dryParam = mapParameters.DryWeight;
            string wetParam = mapParameters.WetWeight;
            string claddingWeightParam = mapParameters.CladdingWeight;
            string insulationWeightParam = mapParameters.InsulationWeight;
            string fluidWeightParam = mapParameters.FluidWeight;
            string totalWeightParam = mapParameters.TotalWeight;
            string computedOverallSizeParam = mapParameters.ComputedOverallSize;

            List<string> parameters = new List<string>();

            if (!string.IsNullOrEmpty(end1))
                parameters.Add(end1);
            if (!string.IsNullOrEmpty(end2))
                parameters.Add(end2);
            if (!string.IsNullOrEmpty(endPrep))
                parameters.Add(endPrep);
            if (!string.IsNullOrEmpty(headerND))
                parameters.Add(headerND);
            if (!string.IsNullOrEmpty(dryParam))
                parameters.Add(dryParam);
            if (!string.IsNullOrEmpty(wetParam))
                parameters.Add(wetParam);
            if (!string.IsNullOrEmpty(claddingWeightParam))
                parameters.Add(claddingWeightParam);
            if (!string.IsNullOrEmpty(insulationWeightParam))
                parameters.Add(insulationWeightParam);
            if (!string.IsNullOrEmpty(fluidWeightParam))
                parameters.Add(fluidWeightParam);
            if (!string.IsNullOrEmpty(totalWeightParam))
                parameters.Add(totalWeightParam);
            if (!string.IsNullOrEmpty(computedOverallSizeParam))
                parameters.Add(computedOverallSizeParam);

            string message = "";
            int count = 0;
            bool isFirst = true;

            foreach (string param in parameters)
            {
                count++;

                if (parameters.Count == count && !isFirst)
                {
                    message += $" and \"{param}\"";

                    if (withPeriodOnLast)
                        message += ".";
                }
                else
                {
                    if (!isFirst)
                        message += " ";

                    message += $"\"{param}\"";

                    if (parameters.Count > 2)
                        message += ",";
                }


                isFirst = false;
            }

            if (!withQuote)
                message = message.Replace("\"", "");

            return message;
        }

        public static string BuildFittingsAllowedMapping(bool withQuote = true)
        {
            if (ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.AllowedMapFittingsElements == null)
                return "threaded nipple, shaped-branch fittings";

            List<string> parameters = new List<string>();

            var allowedMappingsConfig = ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.AllowedMapFittingsElements.Select(x => x.NameContains);

            string fittings = string.Join(", ", allowedMappingsConfig);

            return fittings;
        }

        #region ApiConfig
        public static string GetApiConfigPath()
        {
            string revitYear = Configs.RevitConfig.RevitYear;

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk",
                "Revit",
                "Addins",
                revitYear,
                "ParallelSystemPlugin",
                "Config");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            return Path.Combine(folder, "ApiSettings.json");
        }

        public static ApiSettings LoadApiSettings()
        {
            string path = GetApiConfigPath();

            ApiSettings apiSettings = new ApiSettings();

            if (!File.Exists(path))
            {
                string defaultJson = JsonConvert.SerializeObject(
                    apiSettings,
                    Formatting.Indented
                );

                File.WriteAllText(path, defaultJson, Encoding.UTF8);
            }

            string json = File.ReadAllText(path, Encoding.UTF8);

            var config = JsonConvert.DeserializeObject<ApiSettings>(json);

            return config ?? apiSettings;
        }
        #endregion
    }
}
