using RevitDoc =  Autodesk.Revit.DB.Document;
using RevitBuiltInParameter = Autodesk.Revit.DB.BuiltInParameter;
using RevitProjectInfo = Autodesk.Revit.DB.ProjectInfo;
using RevitParameter = Autodesk.Revit.DB.Parameter;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using ParallelSystemPlugin.UI;          // AppDialog
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ParallelSystemsPlugin.UI.Dialogs
{
    /// <summary>
    /// Interaction logic for Configurations.xaml
    /// </summary>
    public partial class Configurations : Window
    {
        #region Properties / Fields

        public ObservableCollection<EndPrep> PipeEndPreps { get; set; }
        public ObservableCollection<IgnoreComponent> PipeIgnoreComponents { get; set; }

        public ObservableCollection<EndPrep> FittingsEndPreps { get; set; }
        public ObservableCollection<IgnoreComponent> FittingsIgnoredComponents { get; set; }

        public ObservableCollection<AllowedMapFittingsElement> AllowedMappingElements { get; set; }
        public ObservableCollection<ElementsWeight> ElementsWeight { get; set; }
        public ObservableCollection<SystemAbbreviation> SystemAbbreviations { get; set; }
        public ObservableCollection<EndPrepFilterConfig> EndPrepFilters { get; set; }

        public List<double> AllowedAngles { get; private set; }
        public double Tolerance { get; private set; }

        private string _companyLogoPath = "";
        private string _clientLogoPath = "";

        // Remember owner handle so AppDialog can be modal to Revit
        private IntPtr _ownerHwnd = default(IntPtr);
        private RevitDoc _doc;

        #endregion

        #region Constructors

        public Configurations(RevitDoc doc)
        {
            InitializeComponent();
            Icon = AppDialog.LoadWindowIcon();
            _doc = doc;
            LoadConfigurationData(Configs.AppConfig.CurrentConfig);

            if (string.IsNullOrWhiteSpace(ProcJobNumberTextBox.Text) &&
                string.IsNullOrWhiteSpace(ProcJobNameTextBox.Text))
            {
                AutoDetectProjectDetails();
            }
        }

        #endregion

        private static string GetProjectInfoValue(RevitDoc doc, RevitBuiltInParameter builtInParameter, params string[] fallbackParamNames)
        {
            if (doc == null || doc.ProjectInformation == null)
                return "";

            RevitProjectInfo pi = doc.ProjectInformation;

            RevitParameter p = pi.get_Parameter(builtInParameter);
            string value = p == null ? "" : (p.AsString() ?? p.AsValueString() ?? "");

            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

            foreach (string name in fallbackParamNames ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                RevitParameter fallback = pi.LookupParameter(name);
                string fallbackValue = fallback == null ? "" : (fallback.AsString() ?? fallback.AsValueString() ?? "");

                if (!string.IsNullOrWhiteSpace(fallbackValue))
                    return fallbackValue.Trim();
            }

            return "";
        }

        private static string GetProjectNumber(RevitDoc doc)
        {
            return GetProjectInfoValue(
                doc,
                RevitBuiltInParameter.PROJECT_NUMBER,
                "Project Number",
                "Project No",
                "Job Number",
                "Job No");
        }

        private static string GetProjectName(RevitDoc doc)
        {
            return GetProjectInfoValue(
                doc,
                RevitBuiltInParameter.PROJECT_NAME,
                "Project Name",
                "Job Name");
        }
        #region Public

        public void ShowModal(IntPtr owner)
        {
            _ownerHwnd = owner;
            try { new WindowInteropHelper(this) { Owner = owner }; } catch { }
            ShowDialog();
        }

        #endregion

        #region Load/Refresh UI

        private void LoadConfigurationData(ApplicationConfig config)
        {
            // ===== Pipe End Prep =====
            PipeEndPreps = new ObservableCollection<EndPrep>(config.PipeEndPreps ?? new List<EndPrep>());
            PipeEndPrepGrid.ItemsSource = PipeEndPreps;

            PipeIgnoreComponents = new ObservableCollection<IgnoreComponent>(config.PipeIgnoreComponents ?? new List<IgnoreComponent>());
            PipeIgnoreComponentsGrid.ItemsSource = PipeIgnoreComponents;

            PipeEnd1TextBox.Text = config.PipeMapParameters?.End1 ?? "";
            PipeEnd2TextBox.Text = config.PipeMapParameters?.End2 ?? "";
            PipeEndPrepTextBox.Text = config.PipeMapParameters?.EndPrep ?? "";

            PipeUnconnectedTextBox.Text = config.PipeMapParameters?.Unconnected ?? "";
            PipeEnableUnconnectedMappingCheckbox.IsChecked = config.PipeMapParameters?.EnableMapping ?? false;

            // ===== Fittings End Prep =====
            FittingsEndPreps = new ObservableCollection<EndPrep>(config.FittingsEndPreps ?? new List<EndPrep>());
            FittingsEndPrepGrid.ItemsSource = FittingsEndPreps;

            FittingsIgnoredComponents = new ObservableCollection<IgnoreComponent>(config.FittingsIgnoreComponents ?? new List<IgnoreComponent>());
            FittingsIgnoredComponentsGrid.ItemsSource = FittingsIgnoredComponents;

            FittingsEnd1TextBox.Text = config.FittingsMapParameters?.End1 ?? "";
            FittingsEnd2TextBox.Text = config.FittingsMapParameters?.End2 ?? "";
            FittingsEndPrepTextBox.Text = config.FittingsMapParameters?.EndPrep ?? "";
            FittingsHeaderNDTextBox.Text = config.FittingsMapParameters?.HeaderND ?? "";

            FittingsUnconnectedTextBox.Text = config.FittingsMapParameters?.Unconnected ?? "";
            FittingsEnableUnconnectedMappingCheckbox.IsChecked = config.FittingsMapParameters?.EnableMapping ?? false;

            AllowedMappingElements = new ObservableCollection<AllowedMapFittingsElement>(config.AllowedMapFittingsElements ?? new List<AllowedMapFittingsElement>());
            AllowdMapFittingsElementsGrid.ItemsSource = AllowedMappingElements;

            // ===== Pipe Weight =====
            DryParameterTextBox.Text = config.PipeWeightMapParameters?.DryWeight ?? "";
            WetParameterTextBox.Text = config.PipeWeightMapParameters?.WetWeight ?? "";
            CladdingWeightTextBox.Text = config.PipeWeightMapParameters?.CladdingWeight ?? "";
            InsulationWeightTextBox.Text = config.PipeWeightMapParameters?.InsulationWeight ?? "";
            FluidWeightTextBox.Text = config.PipeWeightMapParameters?.FluidWeight ?? "";
            TotalWeightTextBox.Text = config.PipeWeightMapParameters?.TotalWeight ?? "";
            ComputedOverallSizeTextBox.Text = config.PipeWeightMapParameters?.ComputedOverallSize ?? "";
            PipeWeightNumberDecimalsTextBox.Text = (config.PipeWeightMapParameters?.NumOfDecimals ?? 0).ToString(CultureInfo.InvariantCulture);

            PipeWeightCladdingThicknessTextBox.Text = (config.PipeWeightMaterialProperties?.CladdingThickness ?? 0).ToString(CultureInfo.InvariantCulture);
            PipeWeightCladdingDensityTextBox.Text = (config.PipeWeightMaterialProperties?.CladdingDensity ?? 0).ToString(CultureInfo.InvariantCulture);
            PipeWeightInsulationDensityTextBox.Text = (config.PipeWeightMaterialProperties?.InsulationDensity ?? 0).ToString(CultureInfo.InvariantCulture);

            ElementsWeight = new ObservableCollection<ElementsWeight>(config.ElementsWeight ?? new List<ElementsWeight>());
            ElementsGrid.ItemsSource = ElementsWeight;

            SystemAbbreviations = new ObservableCollection<SystemAbbreviation>(config.SystemAbbreviations ?? new List<SystemAbbreviation>());
            SystemAbbreviationGrid.ItemsSource = SystemAbbreviations;

            // ===== Procurement =====
            if (config.Procurement == null)
                config.Procurement = new ProcurementConfig();

            _companyLogoPath = config.Procurement.CompanyLogoPath ?? "";
            _clientLogoPath = config.Procurement.ClientLogoPath ?? "";

            ProcJobNumberTextBox.Text = config.Procurement.JobNumber ?? "";
            ProcJobNameTextBox.Text = config.Procurement.JobName ?? "";
           // ChkAutoDetect.IsChecked = config.Procurement.AutoDetect;
            ProcTargetFolderTextBox.Text = config.Procurement.TargetFolder ?? "";

            ProcPublishSiteTextBox.Text = config.Procurement.PublishSite ?? "";
            ProcPublishFileNameTextBox.Text = config.Procurement.PublishFileName ?? "";

            ProcCompanyLogoImage.Source = File.Exists(_companyLogoPath) ? new BitmapImage(new Uri(_companyLogoPath)) : null;
            ProcClientLogoImage.Source = File.Exists(_clientLogoPath) ? new BitmapImage(new Uri(_clientLogoPath)) : null;

            // Date
            var date = config.Procurement.Date == default(DateTime) ? DateTime.Today : config.Procurement.Date;
            ProcDatePicker.SelectedDate = date;
            ProcDateTextBox.Text = FormatProcDate(date);

            // Report checkboxes
            ChkBomAssemblyRegister.IsChecked = config.Procurement.BomAssemblyRegister;
            ChkBomCutList.IsChecked = config.Procurement.BomCutList;
            ChkBomFittingReport.IsChecked = config.Procurement.BomFittingReport;
            ChkIncludeWeldInFittingReport.IsChecked = config.Procurement.IncludeWeldInFittingReport;
            ChkBomLoadingReport.IsChecked = config.Procurement.BomLoadingReport;
            ChkBomPipeReport.IsChecked = config.Procurement.BomPipeReport;
            ChkLabelReport.IsChecked = config.Procurement.LabelReport;
            ChkBomFieldMaterialReport.IsChecked = config.Procurement.BomFieldMaterialReport;
            ChkBomAccessoryReport.IsChecked = config.Procurement.BomAccessoryReport;
            ChkIncludeSiteMeasure.IsChecked = config.Procurement.IncludeSiteMeasure;
            RdoExportExcel.IsChecked = config.Procurement.ExportReportsToExcel;
            RdoExportPdf.IsChecked = !config.Procurement.ExportReportsToExcel;

            // Cut list parameters
            ProcCutListMaximumLengthTextBox.Text = config.Procurement.CutListMaximumLength.ToString(CultureInfo.InvariantCulture);
            ProcCutListBladeThicknessTextBox.Text = config.Procurement.CutListBladeThickness.ToString(CultureInfo.InvariantCulture);
            ProcCutListNegativeAllowanceTextBox.Text = config.Procurement.CutListNegativeAllowance.ToString(CultureInfo.InvariantCulture);
            ProcOffcutThresholdTextBox.Text = config.Procurement.OffcutThreshold.ToString(CultureInfo.InvariantCulture);

            // Export flags
            ChkExportPdf.IsChecked = config.Procurement.ExportPdf;
            ChkExportImage.IsChecked = config.Procurement.ExportImage;

            // Tools
            var currentTolerance = config.ToolsConfig.PipeSlopeConfig.AcceptedTolerance;
            AllowedAngles = new List<double>(config.ToolsConfig.PipeSlopeConfig.AllowedAngles);
            Tolerance = currentTolerance;
            ImportExportCsvPath.Text = config.ToolsConfig.RenamingConfig?.CsvPath ?? "";
            RefreshList();
            ToleranceTextBox.Text = currentTolerance.ToString(CultureInfo.InvariantCulture);

            RefreshProcurementLogoPlaceholders();

            var pipeFilterConfig = config.ToolsConfig.PipeFilterConfig;
            MaxColor = ToMediaColor(pipeFilterConfig.MaxPipeColor);
            LongColor = ToMediaColor(pipeFilterConfig.LongPipeColor);
            ShortColor = ToMediaColor(pipeFilterConfig.ShortPipeColor);

            MaxLengthButton.Background = new SolidColorBrush(MaxColor);
            TooLongButton.Background = new SolidColorBrush(LongColor);
            TooShortButton.Background = new SolidColorBrush(ShortColor);//

            MaxLengthTextBox.Text = pipeFilterConfig.MaxPipeLength.ToString(CultureInfo.InvariantCulture);
            PipeTooLongTextbox.Text = pipeFilterConfig.LongPipeLength.ToString(CultureInfo.InvariantCulture);
            PipeTooShortTextbox.Text = pipeFilterConfig.ShortPipeLength.ToString(CultureInfo.InvariantCulture);

            var endPrepFilters = Helpers.EndPrepFilter.GetFilterConfigurations(PipeEndPreps.Select(x => x.Value).Distinct().ToList());

            endPrepFilters = Helpers.EndPrepFilter.MatchMethod(endPrepFilters, config.ToolsConfig.EndPrepFilterConfigs);

            EndPrepFilters = new ObservableCollection<EndPrepFilterConfig>(endPrepFilters ?? new List<EndPrepFilterConfig>());

            EndPrepFilterGrid.ItemsSource = EndPrepFilters;

            FilterContains.Text = config.ToolsConfig.SheetCheckAndBomCheckConfig.FilterContains;
            ExcludeText.Text = config.ToolsConfig.SheetCheckAndBomCheckConfig.ExcludeText;
        }

        private void RefreshProcurementLogoPlaceholders()
        {
            ProcCompanyLogoPlaceholder.Visibility = (ProcCompanyLogoImage.Source == null)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

            ProcClientLogoPlaceholder.Visibility = (ProcClientLogoImage.Source == null)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        private string FormatProcDate(DateTime date)
        {
            return date.ToString("dddd, dd MMMM yyyy", new CultureInfo("en-US"));
        }

        #endregion

        #region Logo hover + browse

        private void LogoBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            ((Border)sender).BorderBrush = Brushes.DodgerBlue;
        }

        private void LogoBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            ((Border)sender).BorderBrush = Brushes.LightGray;
        }

        private void BrowseCompanyLogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Company Logo",
                Filter = "Supported Images|*.png;*.jpg;*.jpeg;*.bmp"
            };

            SetDefaultLogoDirectory(dlg);

            if (dlg.ShowDialog() == true)
            {
                _companyLogoPath = dlg.FileName;
                ProcCompanyLogoImage.Source = new BitmapImage(new Uri(dlg.FileName));
            }

            RefreshProcurementLogoPlaceholders();
        }

        private void BrowseClientLogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Client Logo",
                Filter = "Supported Images|*.png;*.jpg;*.jpeg;*.bmp"
            };

            SetDefaultLogoDirectory(dlg);

            if (dlg.ShowDialog() == true)
            {
                _clientLogoPath = dlg.FileName;
                ProcClientLogoImage.Source = new BitmapImage(new Uri(dlg.FileName));
            }

            RefreshProcurementLogoPlaceholders();
        }

        private static void SetDefaultLogoDirectory(OpenFileDialog dialog)
        {
            if (dialog == null)
                return;

            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            string logoDirectory = Path.Combine(
                programData,
                "Parallel Systems",
                "Images");

            if (Directory.Exists(logoDirectory))
                dialog.InitialDirectory = logoDirectory;
        }

        #endregion

        #region Folder browse

        private void BrowseTargetFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Select Target Folder";

                var current = ProcTargetFolderTextBox.Text;
                if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                    dlg.SelectedPath = current;

                var result = dlg.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    ProcTargetFolderTextBox.Text = dlg.SelectedPath;
            }
        }

        private void BrowsePublishSite_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Select Publish Site Folder (SharePoint synced directory or mapped drive)";

                var current = ProcPublishSiteTextBox.Text;
                if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                    dlg.SelectedPath = current;

                var result = dlg.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    ProcPublishSiteTextBox.Text = dlg.SelectedPath;
            }
        }

        #endregion

        #region Grid context menus (Add/Delete)

        private void AddIgnoreComponentsRow_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new IgnoreComponent { NameContains = "" };
            PipeIgnoreComponents.Add(newItem);

            PipeIgnoreComponentsGrid.SelectedItem = newItem;
            PipeIgnoreComponentsGrid.ScrollIntoView(newItem);
        }

        private void DeleteIgnoreComponentsRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = PipeIgnoreComponentsGrid.SelectedItem as IgnoreComponent;
            if (selectedItem == null)
            {
                AppDialog.Show("Delete Row", "Please select a row to delete.", MessageDialogIcon.Warning, MessageDialogButtons.OK, _ownerHwnd);
                return;
            }

            var res = AppDialog.Show("Confirm Delete",
                $"Are you sure you want to delete '{selectedItem.NameContains}'?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res == MessageDialogResult.Yes)
                PipeIgnoreComponents.Remove(selectedItem);
        }

        private void AddPipeEndPrepRow_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new EndPrep { NameContains = "", Value = "" };
            PipeEndPreps.Add(newItem);

            PipeEndPrepGrid.SelectedItem = newItem;
            PipeEndPrepGrid.ScrollIntoView(newItem);
        }

        private void DeletePipeEndPrepRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = PipeEndPrepGrid.SelectedItem as EndPrep;
            if (selectedItem == null)
            {
                AppDialog.Show("Delete Row", "Please select a row to delete.", MessageDialogIcon.Warning, MessageDialogButtons.OK, _ownerHwnd);
                return;
            }

            var res = AppDialog.Show("Confirm Delete",
                $"Are you sure you want to delete '{selectedItem.NameContains}'?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res == MessageDialogResult.Yes)
                PipeEndPreps.Remove(selectedItem);
        }

        private void AddFittingsIgnoredComponentsRow_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new IgnoreComponent { NameContains = "" };
            FittingsIgnoredComponents.Add(newItem);

            FittingsIgnoredComponentsGrid.SelectedItem = newItem;
            FittingsIgnoredComponentsGrid.ScrollIntoView(newItem);
        }

        private void DeleteFittingsIgnoreComponentsRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = FittingsIgnoredComponentsGrid.SelectedItem as IgnoreComponent;
            if (selectedItem == null)
            {
                AppDialog.Show("Delete Row", "Please select a row to delete.", MessageDialogIcon.Warning, MessageDialogButtons.OK, _ownerHwnd);
                return;
            }

            var res = AppDialog.Show("Confirm Delete",
                $"Are you sure you want to delete '{selectedItem.NameContains}'?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res == MessageDialogResult.Yes)
                FittingsIgnoredComponents.Remove(selectedItem);
        }

        private void AddApplyMappingToRow_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new AllowedMapFittingsElement { NameContains = "" };
            AllowedMappingElements.Add(newItem);

            AllowdMapFittingsElementsGrid.SelectedItem = newItem;
            AllowdMapFittingsElementsGrid.ScrollIntoView(newItem);
        }

        private void DeleteApplyMappingToRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = AllowdMapFittingsElementsGrid.SelectedItem as AllowedMapFittingsElement;
            if (selectedItem == null)
            {
                AppDialog.Show("Delete Row", "Please select a row to delete.", MessageDialogIcon.Warning, MessageDialogButtons.OK, _ownerHwnd);
                return;
            }

            var res = AppDialog.Show("Confirm Delete",
                $"Are you sure you want to delete '{selectedItem.NameContains}'?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res == MessageDialogResult.Yes)
                AllowedMappingElements.Remove(selectedItem);
        }

        private void AddFittingsEndPrepRow_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new EndPrep { NameContains = "", Value = "" };
            FittingsEndPreps.Add(newItem);

            FittingsEndPrepGrid.SelectedItem = newItem;
            FittingsEndPrepGrid.ScrollIntoView(newItem);
        }

        private void DeleteFittingsEndPrepRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = FittingsEndPrepGrid.SelectedItem as EndPrep;
            if (selectedItem == null)
            {
                AppDialog.Show("Delete Row", "Please select a row to delete.", MessageDialogIcon.Warning, MessageDialogButtons.OK, _ownerHwnd);
                return;
            }

            var res = AppDialog.Show("Confirm Delete",
                $"Are you sure you want to delete '{selectedItem.NameContains}'?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res == MessageDialogResult.Yes)
                FittingsEndPreps.Remove(selectedItem);
        }

        private void AddPipeWeightElementsRow_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new ElementsWeight { DryWeight = 0, Size = 0, PipeType = "", WetWeight = 0 };
            ElementsWeight.Add(newItem);

            ElementsGrid.SelectedItem = newItem;
            ElementsGrid.ScrollIntoView(newItem);
        }

        private void DeletePipeWeightElementsRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = ElementsGrid.SelectedItem as ElementsWeight;
            if (selectedItem == null)
            {
                AppDialog.Show("Delete Row", "Please select a row to delete.", MessageDialogIcon.Warning, MessageDialogButtons.OK, _ownerHwnd);
                return;
            }

            var res = AppDialog.Show("Confirm Delete",
                $"Are you sure you want to delete '{selectedItem.PipeType}'?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res == MessageDialogResult.Yes)
                ElementsWeight.Remove(selectedItem);
        }

        private void AddSystemAbbreviationRow_Click(object sender, RoutedEventArgs e)
        {
            var newItem = new SystemAbbreviation { AbbreviationContains = "", Density = 0 };
            SystemAbbreviations.Add(newItem);

            SystemAbbreviationGrid.SelectedItem = newItem;
            SystemAbbreviationGrid.ScrollIntoView(newItem);
        }

        private void DeleteSystemAbbreviationRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = SystemAbbreviationGrid.SelectedItem as SystemAbbreviation;
            if (selectedItem == null)
            {
                AppDialog.Show("Delete Row", "Please select a row to delete.", MessageDialogIcon.Warning, MessageDialogButtons.OK, _ownerHwnd);
                return;
            }

            var res = AppDialog.Show("Confirm Delete",
                $"Are you sure you want to delete '{selectedItem.AbbreviationContains}'?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res == MessageDialogResult.Yes)
                SystemAbbreviations.Remove(selectedItem);
        }

        #endregion

        #region Date + Numeric

        private void ProcDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProcDatePicker.SelectedDate.HasValue)
                ProcDateTextBox.Text = FormatProcDate(ProcDatePicker.SelectedDate.Value);
        }

        private void NumericOnly(object sender, TextCompositionEventArgs e)
        {
            var regex = new System.Text.RegularExpressions.Regex("[^0-9.]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        #endregion

        #region Save / Reset / Cancel

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var newConfig = new ApplicationConfig();

                // ===== Pipe =====
                newConfig.PipeMapParameters = new MapParameters
                {
                    End1 = PipeEnd1TextBox.Text,
                    End2 = PipeEnd2TextBox.Text,
                    EndPrep = PipeEndPrepTextBox.Text,
                    Unconnected = PipeUnconnectedTextBox.Text,
                    EnableMapping = PipeEnableUnconnectedMappingCheckbox.IsChecked == true
                };

                newConfig.PipeIgnoreComponents = (PipeIgnoreComponents ?? new ObservableCollection<IgnoreComponent>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.NameContains))
                    .ToList();

                newConfig.PipeEndPreps = (PipeEndPreps ?? new ObservableCollection<EndPrep>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.NameContains) && !string.IsNullOrWhiteSpace(x.Value))
                    .ToList();

                // ===== Fittings =====
                newConfig.FittingsMapParameters = new MapParameters
                {
                    End1 = FittingsEnd1TextBox.Text,
                    End2 = FittingsEnd2TextBox.Text,
                    EndPrep = FittingsEndPrepTextBox.Text,
                    HeaderND = FittingsHeaderNDTextBox.Text,
                    Unconnected = FittingsUnconnectedTextBox.Text,
                    EnableMapping = FittingsEnableUnconnectedMappingCheckbox.IsChecked == true
                };

                newConfig.FittingsIgnoreComponents = (FittingsIgnoredComponents ?? new ObservableCollection<IgnoreComponent>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.NameContains))
                    .ToList();

                newConfig.FittingsEndPreps = (FittingsEndPreps ?? new ObservableCollection<EndPrep>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.NameContains) && !string.IsNullOrWhiteSpace(x.Value))
                    .ToList();

                newConfig.AllowedMapFittingsElements = (AllowedMappingElements ?? new ObservableCollection<AllowedMapFittingsElement>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.NameContains))
                    .ToList();

                // ===== Pipe Weight =====
                newConfig.PipeWeightMapParameters = new MapParameters
                {
                    DryWeight = DryParameterTextBox.Text,
                    WetWeight = WetParameterTextBox.Text,
                    CladdingWeight = CladdingWeightTextBox.Text,
                    InsulationWeight = InsulationWeightTextBox.Text,
                    FluidWeight = FluidWeightTextBox.Text,
                    TotalWeight = TotalWeightTextBox.Text,
                    ComputedOverallSize = ComputedOverallSizeTextBox.Text,
                    NumOfDecimals = (short)ParseDoubleOrDefault(PipeWeightNumberDecimalsTextBox.Text, 0)
                };

                newConfig.ElementsWeight = (ElementsWeight ?? new ObservableCollection<ElementsWeight>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.PipeType))
                    .ToList();

                string strCladdingThickness = string.IsNullOrWhiteSpace(PipeWeightCladdingThicknessTextBox.Text) ? "0" : PipeWeightCladdingThicknessTextBox.Text;
                string strCladdingDensity = string.IsNullOrWhiteSpace(PipeWeightCladdingDensityTextBox.Text) ? "0" : PipeWeightCladdingDensityTextBox.Text;
                string strInsulationDensity = string.IsNullOrWhiteSpace(PipeWeightInsulationDensityTextBox.Text) ? "0" : PipeWeightInsulationDensityTextBox.Text;

                newConfig.PipeWeightMaterialProperties = new MaterialProperties
                {
                    CladdingThickness = strCladdingThickness.StringToDouble(),
                    CladdingDensity = strCladdingDensity.StringToDouble(),
                    InsulationDensity = strInsulationDensity.StringToDouble()
                };

                newConfig.SystemAbbreviations = (SystemAbbreviations ?? new ObservableCollection<SystemAbbreviation>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.AbbreviationContains))
                    .ToList();

                // ===== Procurement =====
                if (newConfig.Procurement == null)
                    newConfig.Procurement = new ProcurementConfig();

                newConfig.Procurement.CompanyLogoPath = _companyLogoPath;
                newConfig.Procurement.ClientLogoPath = _clientLogoPath;

                newConfig.Procurement.JobNumber = ProcJobNumberTextBox.Text?.Trim() ?? "";
                newConfig.Procurement.JobName = ProcJobNameTextBox.Text?.Trim() ?? "";
                //newConfig.Procurement.AutoDetect = ChkAutoDetect.IsChecked == true;

                newConfig.Procurement.TargetFolder = ProcTargetFolderTextBox.Text?.Trim() ?? "";
                newConfig.Procurement.PublishSite = ProcPublishSiteTextBox.Text?.Trim() ?? "";
                newConfig.Procurement.PublishFileName = ProcPublishFileNameTextBox.Text?.Trim() ?? "";

                newConfig.Procurement.Date = ProcDatePicker.SelectedDate ?? DateTime.Today;

                newConfig.Procurement.ExportPdf = ChkExportPdf.IsChecked == true;
                newConfig.Procurement.ExportImage = ChkExportImage.IsChecked == true;

                newConfig.Procurement.BomAssemblyRegister = ChkBomAssemblyRegister.IsChecked == true;
                newConfig.Procurement.BomCutList = ChkBomCutList.IsChecked == true;
                newConfig.Procurement.BomFittingReport = ChkBomFittingReport.IsChecked == true;
                newConfig.Procurement.IncludeWeldInFittingReport = ChkIncludeWeldInFittingReport.IsChecked == true;
                newConfig.Procurement.BomLoadingReport = ChkBomLoadingReport.IsChecked == true;
                newConfig.Procurement.BomPipeReport = ChkBomPipeReport.IsChecked == true;
                newConfig.Procurement.LabelReport = ChkLabelReport.IsChecked == true;
                newConfig.Procurement.BomFieldMaterialReport = ChkBomFieldMaterialReport.IsChecked == true;
                newConfig.Procurement.BomAccessoryReport = ChkBomAccessoryReport.IsChecked == true;
                newConfig.Procurement.IncludeSiteMeasure = ChkIncludeSiteMeasure.IsChecked == true;
                newConfig.Procurement.ExportReportsToExcel = RdoExportExcel.IsChecked == true;

                newConfig.Procurement.CutListMaximumLength = ParseDoubleOrDefault(ProcCutListMaximumLengthTextBox.Text, 6000);
                newConfig.Procurement.CutListBladeThickness = ParseDoubleOrDefault(ProcCutListBladeThicknessTextBox.Text, 2);
                newConfig.Procurement.CutListNegativeAllowance = ParseDoubleOrDefault(ProcCutListNegativeAllowanceTextBox.Text, 0);
                newConfig.Procurement.OffcutThreshold = ParseDoubleOrDefault(ProcOffcutThresholdTextBox.Text, 2500);
                if (newConfig.Procurement.OffcutThreshold < 0)
                    throw new InvalidOperationException("Offcut Threshold must be zero or greater.");

                newConfig.ToolsConfig = new ToolsConfig();
                newConfig.ToolsConfig.PipeSlopeConfig = new PipeSlopeConfig();
                newConfig.ToolsConfig.PipeSlopeConfig.AllowedAngles = AllowedAngles;
                newConfig.ToolsConfig.PipeSlopeConfig.AcceptedTolerance = GetTolerance();
                newConfig.ToolsConfig.RenamingConfig = new RenamingConfig();
                newConfig.ToolsConfig.RenamingConfig.CsvPath = ImportExportCsvPath.Text;

                newConfig.ToolsConfig.PipeFilterConfig.MaxPipeColor = ToRgbColor(MaxColor);
                newConfig.ToolsConfig.PipeFilterConfig.LongPipeColor = ToRgbColor(LongColor);
                newConfig.ToolsConfig.PipeFilterConfig.ShortPipeColor = ToRgbColor(ShortColor);

                newConfig.ToolsConfig.EndPrepFilterConfigs = EndPrepFilters.ToList();

                var endPrepFilters = Helpers.EndPrepFilter.GetFilterConfigurations(PipeEndPreps.Select(x => x.Value).Distinct().ToList());

                endPrepFilters = Helpers.EndPrepFilter.MatchMethod(endPrepFilters, newConfig.ToolsConfig.EndPrepFilterConfigs);

                EndPrepFilters = new ObservableCollection<EndPrepFilterConfig>(endPrepFilters ?? new List<EndPrepFilterConfig>());

                EndPrepFilterGrid.ItemsSource = EndPrepFilters;

                newConfig.ToolsConfig.SheetCheckAndBomCheckConfig.FilterContains = FilterContains.Text;
                newConfig.ToolsConfig.SheetCheckAndBomCheckConfig.ExcludeText = ExcludeText.Text;

                Helpers.Config.Save(newConfig);

                // IMPORTANT:
                // NO EXPORT HAPPENS HERE.
                // Export happens in ExportBomCommand -> PublishBomCommand when p.ExportPdf / p.ExportImage is true.

                AppDialog.Show("Configurations", "Your changes have been saved successfully.", MessageDialogIcon.Success, MessageDialogButtons.OK, _ownerHwnd);
                Close();
            }
            catch (Exception ex)
            {
                AppDialog.Show("Configurations", "Error saving changes.\n\n" + ex.Message, MessageDialogIcon.Error, MessageDialogButtons.OK, _ownerHwnd);
            }
        }

        private void ResetToDefault_Click(object sender, RoutedEventArgs e)
        {
            var res = AppDialog.Show(
                "Confirmation",
                "Are you sure you want to restore default configurations?",
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo,
                _ownerHwnd);

            if (res != MessageDialogResult.Yes)
                return;

            ProcCompanyLogoImage.Source = null;
            ProcClientLogoImage.Source = null;

            ProcCompanyLogoPlaceholder.Visibility = System.Windows.Visibility.Visible;
            ProcClientLogoPlaceholder.Visibility = System.Windows.Visibility.Visible;

            PipeEndPreps?.Clear();
            PipeIgnoreComponents?.Clear();
            FittingsEndPreps?.Clear();
            FittingsIgnoredComponents?.Clear();
            AllowedMappingElements?.Clear();
            ElementsWeight?.Clear();
            SystemAbbreviations?.Clear();

            ApplicationConfig defaultConfig = Helpers.Config.GetDefaultConfig();
            LoadConfigurationData(defaultConfig);

            AppDialog.Show("Success", "Default configurations successfully loaded.", MessageDialogIcon.Success, MessageDialogButtons.OK, _ownerHwnd);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static bool TryParseUserDouble(string text, out double value)
        {
            value = 0d;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }

        private static double ParseDoubleOrDefault(string text, double defaultValue)
        {
            if (string.IsNullOrWhiteSpace(text))
                return defaultValue;

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return v;

            return defaultValue;
        }

        #endregion

        #region Tools

        private void RefreshList()
        {
            AnglesListBox.ItemsSource = null;
            AnglesListBox.ItemsSource = AllowedAngles.OrderBy(x => x);
        }

        private void AddAngle_Click(object sender, RoutedEventArgs e)
        {
            if (TryParseUserDouble(AngleInputTextBox.Text, out double value))
            {
                if (!AllowedAngles.Contains(value))
                {
                    AllowedAngles.Add(value);
                    RefreshList();
                }
            }

            AngleInputTextBox.Clear();
        }

        private void RemoveAngle_Click(object sender, RoutedEventArgs e)
        {
            if (AnglesListBox.SelectedItem is double selected)
            {
                AllowedAngles.Remove(selected);
                RefreshList();
            }
        }

        private double GetTolerance()
        {
            double tolerance = 0;

            if (!TryParseUserDouble(ToleranceTextBox.Text, out double tol))
            {
                return tolerance;
            }

            tolerance = tol;

            return tolerance;
        }
        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = "CSV File (*.csv)|*.csv";

            if (dialog.ShowDialog() == true)
            {
                ImportExportCsvPath.Text = dialog.FileName;
            }
        }
        #endregion

        private static Color ToMediaColor(RgbColor color)
        {
            if (color == null) return Colors.Transparent;
            return Color.FromRgb(color.Red, color.Green, color.Blue);
        }

        private static RgbColor ToRgbColor(Color color)
        {
            return new RgbColor(color.R, color.G, color.B);
        }

        #region Pipe Length Check
        public Color MaxColor;
        public Color LongColor;
        public Color ShortColor;

        private void PickMaxColor_Click(object sender, RoutedEventArgs e)
        {
            var color = PickColor(MaxColor);

            if (color == MaxColor) return; // optional (no change)

            MaxColor = color;

            var button = sender as Button;
            button.Background = new SolidColorBrush(color);
        }

        private void PickTooLongColor_Click(object sender, RoutedEventArgs e)
        {
            var color = PickColor(LongColor);

            if (color == LongColor) return; // optional (no change)

            LongColor = color;

            var button = sender as Button;
            button.Background = new SolidColorBrush(color);
        }

        private void PickTooShortColor_Click(object sender, RoutedEventArgs e)
        {
            //var color = PickColor(ShortColor);
            //TooShortPreview.Background = new SolidColorBrush(color);
            //ShortColor = color;
            var color = PickColor(ShortColor);

            if (color == ShortColor) return; // optional (no change)

            ShortColor = color;

            var button = sender as Button;
            button.Background = new SolidColorBrush(color);
        }

        #endregion

        private Color PickColor(Color currentColor)
        {
            System.Windows.Forms.ColorDialog dialog = new System.Windows.Forms.ColorDialog();

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
            }

            return currentColor;
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            if (button == null) return;

            // Get the bound item
            var config = button.DataContext as EndPrepFilterConfig;
            if (config == null) return;

            // Open WinForms color dialog
            var dialog = new System.Windows.Forms.ColorDialog
            {
                AllowFullOpen = true,
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(config.Color.Red, config.Color.Green, config.Color.Blue)
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                // Update Revit color
                config.Color = new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B);

                // Refresh the DataGrid to show new color
                EndPrepFilterGrid.Items.Refresh();
            }
        }

        private void BtnAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            AutoDetectProjectDetails();
        }

        private void ReportCheckBox_Click(object sender, RoutedEventArgs e)
        {
            FittingReportOptionsPanel.Visibility = ReferenceEquals(sender, ChkBomFittingReport)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AutoDetectProjectDetails()
        {
            string jobNumberNew = GetProjectNumber(_doc)?.Trim() ?? "";
            string jobNameNew = GetProjectName(_doc)?.Trim() ?? "";

            // Update each field independently.
            if (!string.IsNullOrWhiteSpace(jobNumberNew))
            {
                ProcJobNumberTextBox.Text = jobNumberNew;
            }

            if (!string.IsNullOrWhiteSpace(jobNameNew))
            {
                ProcJobNameTextBox.Text = jobNameNew;
            }
        }
    }
}
