using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;


namespace ParallelSystemPlugin.UI
{
    public partial class ProgressWindow : Window
    {
        private int _total = 100;

        public bool IsCanceled { get; private set; }
        public bool IsCompleted { get; private set; }
        private long _lastUiTick = Environment.TickCount;
        public int UpdateIntervalMs { get; set; } = 70;
        public int TargetMaxUpdates { get; set; } = 120;


        public ProgressWindow()
        {
            InitializeComponent();

            // ESC to cancel/close
            CommandBindings.Add(new CommandBinding(
                ApplicationCommands.Stop,
                (s, e) => OnCancelClick(this, null)));
            InputBindings.Add(new KeyBinding(ApplicationCommands.Stop, Key.Escape, ModifierKeys.None));
        }

        /// Call this instead of Update()
        public void UpdateSmart(int current, int total, string status = null, bool force = false)
        {
            // Compute a coarse item step so we don’t update more than ~TargetMaxUpdates times overall
            int step = Math.Max(1, total / Math.Max(1, TargetMaxUpdates));

            long now = Environment.TickCount;
            bool timeOk = now - _lastUiTick >= UpdateIntervalMs;
            bool itemOk = (current % step) == 0;

            if (force || (timeOk && itemOk))
            {
                Update(current, status);
                _lastUiTick = now;
            }
        }


        public void SetTitle(string title)
        {
            if (!string.IsNullOrWhiteSpace(title))
                this.Title = title;
        }


        // === called by your commands ===
        public void Initialize(int total, string title = "Processing…", string message = "")
        {
            _total = Math.Max(1, total);
            ProgressBarControl.Minimum = 0;
            ProgressBarControl.Maximum = _total;
            ProgressBarControl.Value = 0;

            SetTitle(title);
            StatusText.Text = message;
            IsCanceled = false;
            IsCompleted = false;

            CancelButton.IsEnabled = true;
            CancelButton.Content = "Cancel";

            _lastUiTick = Environment.TickCount; // reset throttle start
        }

        public void Update(int current, string status = null)
        {
            if (current < 0) current = 0;
            if (current > _total) current = _total;

            if (!string.IsNullOrWhiteSpace(status))
                StatusText.Text = status;

            ProgressBarControl.Value = current;
            PumpUI();
        }

        public void Done(string status, int total, string title = "Done…")
        {
            IsCompleted = true;
            IsCanceled = false;

            StatusText.Text = status;
            ProgressBarControl.Value = total;

            CancelButton.Content = "Close";
            CancelButton.IsEnabled = true;

            SetTitle(title);
            PumpUI();
        }

        public void Canceled(string status, int processed, string message = "")
        {
            IsCompleted = false;
            IsCanceled = true;

            StatusText.Text = message;
            ProgressBarControl.Value = processed;

            CancelButton.Content = "Close";
            CancelButton.IsEnabled = true;

            SetTitle(status);
            PumpUI();
        }
        // === end public API ===

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            // If still running, signal cancel
            if (!IsCompleted && !IsCanceled)
            {
                IsCanceled = true;
                CancelButton.IsEnabled = false;
                StatusText.Text = "Canceling…";
                PumpUI();
                return;
            }
            // If completed or already canceled, close
            Close();
        }

        private void PumpUI()
        {
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }
        
    }
   
}
