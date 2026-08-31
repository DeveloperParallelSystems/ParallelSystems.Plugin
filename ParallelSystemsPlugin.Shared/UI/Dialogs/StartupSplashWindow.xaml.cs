using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace ParallelSystemPlugin.UI
{
    public partial class StartupSplashWindow : Window
    {
        private static readonly TimeSpan MinimumDisplayTime =
            TimeSpan.FromMilliseconds(1400);

        private readonly Stopwatch _visibleTime;
        private readonly DispatcherTimer _closeTimer;

        public StartupSplashWindow()
        {
            InitializeComponent();

            VersionLabel.Text = "Version " + GetDisplayVersion();

            _visibleTime = Stopwatch.StartNew();
            _closeTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher);
            _closeTimer.Tick += CloseTimer_Tick;
        }

        public void SetStatus(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                StatusLabel.Text = message;
        }

        public void CompleteLoading()
        {
            StatusLabel.Text = "Parallel Systems tools loaded.";
            LoadingIndicator.IsIndeterminate = false;
            LoadingIndicator.Value = 100;

            TimeSpan remaining =
                MinimumDisplayTime - _visibleTime.Elapsed;

            if (remaining <= TimeSpan.Zero)
            {
                CloseSafely();
                return;
            }

            _closeTimer.Interval = remaining;
            _closeTimer.Start();
        }

        public void CloseSafely()
        {
            try
            {
                _closeTimer.Stop();
                Close();
            }
            catch
            {
                // A splash screen must never affect Revit startup.
            }
        }

        private void CloseTimer_Tick(object sender, EventArgs e)
        {
            CloseSafely();
        }

        private static string GetDisplayVersion()
        {
            try
            {
                Assembly assembly = typeof(ParallelSystemsPlugin.App).Assembly;
                string version = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;

                if (string.IsNullOrWhiteSpace(version))
                    version = assembly.GetName().Version?.ToString();

                if (string.IsNullOrWhiteSpace(version))
                    return "1.17.7";

                int suffixIndex = version.IndexOf('+');
                if (suffixIndex >= 0)
                    version = version.Substring(0, suffixIndex);

                if (version.EndsWith(".0", StringComparison.Ordinal))
                    version = version.Substring(0, version.Length - 2);

                return version;
            }
            catch
            {
                return "1.17.7";
            }
        }
    }
}
