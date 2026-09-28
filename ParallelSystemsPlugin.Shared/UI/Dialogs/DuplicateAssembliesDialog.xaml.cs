using Autodesk.Revit.DB;
using ParallelSystemsPlugin.AssemblyDuplication;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ParallelSystemPlugin.UI
{
    // Changed by Jhay: modal editable-number preview returning only its final immutable plan.
    public partial class DuplicateAssembliesDialog : Window
    {
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out ClientRect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private readonly IntPtr ownerHandle;
        private readonly Func<
            ElementId,
            long,
            IReadOnlyDictionary<long, long>,
            AssemblyBatchPreflightResult> createPlan;
        private readonly Dictionary<long, long> manualAnchors = new Dictionary<long, long>();
        private readonly Dictionary<long, string> invalidAnchorInputs =
            new Dictionary<long, string>();
        private string fullErrorReport = string.Empty;
        private bool errorDetailsVisible;
        private bool initialized;

        internal DuplicateAssembliesDialog(
            IntPtr ownerHandle,
            IReadOnlyList<DuplicateAssemblyLevelOption> levels,
            Func<ElementId, long, IReadOnlyDictionary<long, long>, AssemblyBatchPreflightResult> createPlan)
        {
            InitializeComponent();
            this.ownerHandle = ownerHandle;
            this.createPlan = createPlan ?? throw new ArgumentNullException(nameof(createPlan));
            Icon = AppDialog.LoadWindowIcon();
            if (ownerHandle != IntPtr.Zero)
                new WindowInteropHelper(this) { Owner = ownerHandle };
            PART_DestinationLevel.ItemsSource = levels;
            if (levels.Count > 0)
                PART_DestinationLevel.SelectedIndex = 0;
            Loaded += OnLoaded;
            SizeChanged += OnWindowSizeChanged;
        }

        internal AssemblyBatchPlan ConfirmedPlan { get; private set; }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyInitialWorkingAreaBounds();
            FillNativeClientArea();
            WindowCentering.CenterOnOwnerHwnd(this, ownerHandle);
            initialized = true;
            RefreshPreview();
            Dispatcher.BeginInvoke(
                new Action(FillNativeClientArea),
                DispatcherPriority.Loaded);
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            FillNativeClientArea();
        }

        // Changed by Jhay: Revit can arrange Window content at desired size; bind the
        // root explicitly to the native client rectangle so every pixel is painted.
        private void FillNativeClientArea()
        {
            if (PART_Root == null)
                return;

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetClientRect(handle, out ClientRect client))
                return;

            PresentationSource source = PresentationSource.FromVisual(this);
            Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            System.Windows.Point clientSize = fromDevice.Transform(
                new System.Windows.Point(client.Right - client.Left, client.Bottom - client.Top));
            PART_Root.Width = Math.Max(0.0, clientSize.X);
            PART_Root.Height = Math.Max(0.0, clientSize.Y);
        }

        // Changed by Jhay: size against the working area of the monitor containing Revit before centering.
        private void ApplyInitialWorkingAreaBounds()
        {
            Rect workingArea = SystemParameters.WorkArea;
            try
            {
                IntPtr monitorOwner = ownerHandle != IntPtr.Zero
                    ? ownerHandle
                    : new WindowInteropHelper(this).Handle;
                System.Drawing.Rectangle pixels =
                    System.Windows.Forms.Screen.FromHandle(monitorOwner).WorkingArea;
                PresentationSource source = PresentationSource.FromVisual(this);
                Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                System.Windows.Point topLeft = fromDevice.Transform(
                    new System.Windows.Point(pixels.Left, pixels.Top));
                System.Windows.Point bottomRight = fromDevice.Transform(
                    new System.Windows.Point(pixels.Right, pixels.Bottom));
                workingArea = new Rect(topLeft, bottomRight);
            }
            catch
            {
                // SystemParameters.WorkArea remains a safe DPI-aware fallback.
            }

            const double workingAreaMargin = 32.0;
            double availableWidth = Math.Max(1.0, workingArea.Width - workingAreaMargin);
            double availableHeight = Math.Max(1.0, workingArea.Height - workingAreaMargin);
            MinWidth = Math.Min(720.0, availableWidth);
            MinHeight = Math.Min(480.0, availableHeight);
            MaxWidth = availableWidth;
            MaxHeight = availableHeight;
            Width = Math.Min(1280.0, availableWidth);
            Height = Math.Min(720.0, availableHeight);
        }

        private void OnInputChanged(object sender, RoutedEventArgs e)
        {
            if (initialized)
                RefreshPreview();
        }

        private void RefreshPreview()
        {
            ConfirmedPlan = null;
            PART_Confirm.IsEnabled = false;

            string input = (PART_StartingNumber.Text ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                InvalidateVisiblePreview("Enter a starting number.");
                return;
            }
            if (!long.TryParse(
                    input,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long startingNumber) || startingNumber < 0)
            {
                InvalidateVisiblePreview(
                    "Starting number must be a whole number from 0 through " +
                    long.MaxValue.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }

            var destination = PART_DestinationLevel.SelectedItem as DuplicateAssemblyLevelOption;
            if (destination == null)
            {
                InvalidateVisiblePreview("Choose a destination level.");
                return;
            }

            AssemblyBatchPreflightResult result;
            try
            {
                result = createPlan(destination.LevelId, startingNumber, manualAnchors);
            }
            catch (Exception exception)
            {
                InvalidateVisiblePreview(exception.Message);
                return;
            }

            List<DuplicateAssemblyPreviewRow> rows = result.PreviewItems
                .Select(item => new DuplicateAssemblyPreviewRow
                {
                    Order = item.Order,
                    SourceAssemblyId = item.SourceAssemblyId,
                    SourceName = item.SourceAssemblyName,
                    SourceLevel = item.SourceLevelName,
                    TargetName = item.TargetAssemblyName,
                    AssignedNumber = item.AssignedNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Status = item.Status
                })
                .ToList();

            foreach (KeyValuePair<long, string> invalid in invalidAnchorInputs)
            {
                DuplicateAssemblyPreviewRow row = rows.FirstOrDefault(
                    item => item.SourceAssemblyId == invalid.Key);
                if (row == null)
                    continue;
                row.AssignedNumber = invalid.Value;
                row.Status = "Invalid";
            }

            PART_Preview.ItemsSource = rows;
            List<string> messages = result.Issues
                .Select(issue => issue.Message)
                .Concat(invalidAnchorInputs.Select(item =>
                    "Assigned Number '" + item.Value + "' for source " + item.Key +
                    " must be a whole number from 0 through " +
                    long.MaxValue.ToString(CultureInfo.InvariantCulture) + "."))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            List<string> documentationLines = result.PreviewItems
                .Select(item => "Documentation | " + item.SourceAssemblyName + " | " +
                    (item.DocumentationSummary ?? "<unavailable>"))
                .ToList();

            bool valid = result.IsValid && invalidAnchorInputs.Count == 0;
            if (messages.Count > 0)
            {
                ShowErrors(messages, documentationLines);
            }
            // Changed by Jhay: an invalid preflight may intentionally retain preview rows without a plan.
            else if (result.Plan != null && result.Plan.SkippedConflictingNames.Count > 0)
            {
                ShowReady("Ready. Skipped complete-name conflicts: " +
                    string.Join(", ", result.Plan.SkippedConflictingNames) +
                    ". Cancel leaves the model unchanged." + Environment.NewLine +
                    string.Join(Environment.NewLine, documentationLines));
            }
            else
            {
                ShowReady(
                    "Ready. No complete target-name conflicts. Cancel leaves the model unchanged." +
                    Environment.NewLine + string.Join(Environment.NewLine, documentationLines));
            }

            ConfirmedPlan = valid ? result.Plan : null;
            PART_Confirm.IsEnabled = valid;
        }

        // Changed by Jhay: committing an edit creates an anchor and replans that row and every row below it.
        private void OnPreviewCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit ||
                e.Column != PART_AssignedNumberColumn ||
                !(e.Row.Item is DuplicateAssemblyPreviewRow row) ||
                !(e.EditingElement is TextBox editor))
                return;

            string requestedText = (editor.Text ?? string.Empty).Trim();
            // Changed by Jhay: invalidate the old plan before the DataGrid edit transaction
            // finishes so an immediate Confirm click cannot execute stale assignments.
            ConfirmedPlan = null;
            PART_Confirm.IsEnabled = false;
            Dispatcher.BeginInvoke(
                new Action(() => ApplyManualAnchor(row.SourceAssemblyId, requestedText)),
                DispatcherPriority.Background);
        }

        private void ApplyManualAnchor(long sourceAssemblyId, string requestedText)
        {
            if (long.TryParse(
                    requestedText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long requestedNumber) && requestedNumber >= 0)
            {
                manualAnchors[sourceAssemblyId] = requestedNumber;
                invalidAnchorInputs.Remove(sourceAssemblyId);
            }
            else
            {
                manualAnchors.Remove(sourceAssemblyId);
                invalidAnchorInputs[sourceAssemblyId] = requestedText;
            }
            RefreshPreview();
        }

        private void InvalidateVisiblePreview(string message)
        {
            ConfirmedPlan = null;
            PART_Confirm.IsEnabled = false;
            foreach (DuplicateAssemblyPreviewRow row in
                (PART_Preview.ItemsSource as IEnumerable<DuplicateAssemblyPreviewRow>) ??
                Enumerable.Empty<DuplicateAssemblyPreviewRow>())
            {
                row.Status = "Invalid";
            }
            PART_Preview.Items.Refresh();
            ShowErrors(new[] { message }, Enumerable.Empty<string>());
        }

        // Changed by Jhay: keep the preview usable while retaining a complete copyable error report.
        private void ShowErrors(
            IEnumerable<string> errors,
            IEnumerable<string> contextLines)
        {
            List<string> errorList = (errors ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            List<string> context = (contextLines ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            fullErrorReport = string.Join(Environment.NewLine, new[]
            {
                "DUPLICATE ASSEMBLIES ERROR REPORT",
                "Generated UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                string.Empty,
                "Send this report to the Software Developer Administrator or the Developer for assistance.",
                string.Empty,
                "ERRORS",
                string.Join(Environment.NewLine, errorList.Select(
                    (message, index) => (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + message)),
                context.Count == 0
                    ? string.Empty
                    : Environment.NewLine + "PREVIEW CONTEXT" + Environment.NewLine +
                      string.Join(Environment.NewLine, context)
            });

            PART_Issues.Text =
                "Errors were found and Confirm is disabled. Select View Errors for details, then copy " +
                "the report and send it to the Software Developer Administrator or the Developer.";
            PART_IssuePanel.Visibility = System.Windows.Visibility.Visible;
            PART_ErrorText.Text = fullErrorReport;
            PART_ViewErrors.Visibility = System.Windows.Visibility.Visible;
            errorDetailsVisible = false;
            PART_ErrorDetails.Visibility = System.Windows.Visibility.Collapsed;
            PART_ViewErrors.Content = "View Errors";
        }

        private void ShowReady(string message)
        {
            fullErrorReport = string.Empty;
            errorDetailsVisible = false;
            PART_Issues.Text = message;
            // Changed by Jhay: a valid preview needs no warning/status panel.
            PART_IssuePanel.Visibility = System.Windows.Visibility.Collapsed;
            PART_ErrorText.Text = string.Empty;
            PART_ViewErrors.Visibility = System.Windows.Visibility.Collapsed;
            PART_ErrorDetails.Visibility = System.Windows.Visibility.Collapsed;
            PART_ViewErrors.Content = "View Errors";
        }

        private void OnViewErrorsClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(fullErrorReport))
                return;
            errorDetailsVisible = !errorDetailsVisible;
            PART_ErrorDetails.Visibility = errorDetailsVisible
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            PART_ViewErrors.Content = errorDetailsVisible ? "Hide Errors" : "View Errors";
        }

        private void OnCopyErrorsClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(fullErrorReport))
                return;
            try
            {
                Clipboard.SetText(fullErrorReport);
                PART_Issues.Text =
                    "Errors were found. The complete report was copied. Send it to the Software " +
                    "Developer Administrator or the Developer for assistance.";
            }
            catch (Exception exception)
            {
                PART_Issues.Text =
                    "The error report could not be copied: " + exception.Message +
                    ". Keep View Errors open and copy the text manually.";
            }
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            if (ConfirmedPlan == null)
                return;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            ConfirmedPlan = null;
            DialogResult = false;
        }
    }

    internal sealed class DuplicateAssemblyLevelOption
    {
        public ElementId LevelId { get; set; }
        public string DisplayName { get; set; }
    }

    internal sealed class DuplicateAssemblyPreviewRow
    {
        public int Order { get; set; }
        public long SourceAssemblyId { get; set; }
        public string SourceName { get; set; }
        public string SourceLevel { get; set; }
        public string TargetName { get; set; }
        public string AssignedNumber { get; set; }
        public string Status { get; set; }
    }
}
