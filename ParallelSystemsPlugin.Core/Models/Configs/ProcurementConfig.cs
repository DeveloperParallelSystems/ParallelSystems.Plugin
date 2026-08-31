using System;

namespace ParallelSystemsPlugin.Models.Configs
{
    public class ProcurementConfig
    {
        public string CompanyLogoPath { get; set; } = "";
        public string ClientLogoPath { get; set; } = "";
        public string JobNumber { get; set; } = "";
        public string JobName { get; set; } = "";
        public bool AutoDetect { get; set; } = false;
        public string TargetFolder { get; set; } = "";

        public DateTime Date { get; set; } = DateTime.Today;

        public bool BomAssemblyRegister { get; set; } = true;
        public bool BomCutList { get; set; } = true;
        public bool BomFittingReport { get; set; } = true;
        public bool BomLoadingReport { get; set; } = true;
        public bool BomPipeReport { get; set; } = true;
        public bool LabelReport { get; set; } = true;
        public bool BomFieldMaterialReport { get; set; } = true;
        public bool BomAccessoryReport { get; set; } = true;
        public bool IncludeSiteMeasure { get; set; } = false;
        public bool ExportReportsToExcel { get; set; } = true;

        // ===== BOM - CUT LIST (units: mm unless otherwise specified) =====
        /// <summary>
        /// Maximum stock/material length that cut pieces can be taken from.
        /// Default: 6000
        /// </summary>
        public double CutListMaximumLength { get; set; } = 6000;

        /// <summary>
        /// Blade thickness (kerf) used in the cut list calculation.
        /// Default: 2
        /// </summary>
        public double CutListBladeThickness { get; set; } = 2;

        /// <summary>
        /// Negative allowance applied to computed lengths.
        /// Default: 0
        /// </summary>
        public double CutListNegativeAllowance { get; set; } = 0;
        public double OffcutThreshold { get; set; } = 2500;
        public string PublishSite { get; set; } = "";
        public string PublishFileName { get; set; } = "";
        public bool ExportPdf { get; set; } = false;
        public bool ExportImage { get; set; } = false;


    }
}
