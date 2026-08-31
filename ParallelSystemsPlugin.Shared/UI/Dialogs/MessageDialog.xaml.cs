using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ParallelSystemPlugin.UI
{
    public enum MessageDialogIcon { Info, Warning, Error, Success, Question }
    public enum MessageDialogButtons { OK, OKCancel, YesNo, YesNoCancel }
    public enum MessageDialogResult { None, OK, Cancel, Yes, No }

    public partial class MessageDialog : Window
    {
        private readonly MessageDialogButtons _buttons;
        private MessageDialogResult _result = MessageDialogResult.None;
        private Button _defaultChoiceButton;
        private bool _choiceMode;

        public bool DefaultToSecondary { get; set; }

        public int SelectedChoiceIndex { get; private set; } = -1;

        public MessageDialog(
            string title,
            string message,
            MessageDialogIcon icon = MessageDialogIcon.Info,
            MessageDialogButtons buttons = MessageDialogButtons.OK)
        {
            InitializeComponent();

            _buttons = buttons;
            Title = title ?? "Message";
            PART_Title.Text = title ?? "Message";
            PART_Message.Text = message ?? string.Empty;
            Icon = AppDialog.LoadWindowIcon();

            ApplyIcon(icon);
            ConfigureButtons(buttons);

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;
        }

        public void SetInstruction(string instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                PART_Instruction.Text = string.Empty;
                PART_Instruction.Visibility = Visibility.Collapsed;
                return;
            }

            PART_Instruction.Text = instruction.Trim();
            PART_Instruction.Visibility = Visibility.Visible;
        }

        public void SetDetails(string details)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                PART_DetailsText.Text = string.Empty;
                PART_DetailsExpander.Visibility = Visibility.Collapsed;
                return;
            }

            PART_DetailsText.Text = details;
            PART_DetailsExpander.Visibility = Visibility.Visible;
        }

        public void ConfigureChoices(
            IList<string> options,
            int defaultOptionIndex = 0)
        {
            if (options == null || options.Count == 0)
                throw new ArgumentException(
                    "At least one dialog choice is required.",
                    nameof(options));

            _choiceMode = true;
            SelectedChoiceIndex = -1;
            PART_OptionsPanel.Children.Clear();
            PART_OptionsPanel.Visibility = Visibility.Visible;

            PART_PrimaryButton.Visibility = Visibility.Collapsed;
            PART_SecondaryButton.Visibility = Visibility.Collapsed;
            PART_TertiaryButton.Content = "Cancel";
            PART_TertiaryButton.Visibility = Visibility.Visible;
            PART_TertiaryButton.IsCancel = true;

            if (defaultOptionIndex < 0 || defaultOptionIndex >= options.Count)
                defaultOptionIndex = 0;

            for (int index = 0; index < options.Count; index++)
            {
                string optionText = options[index] ?? string.Empty;
                var text = new TextBlock
                {
                    Text = optionText,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold
                };

                var button = new Button
                {
                    Content = text,
                    Tag = index,
                    Style = TryFindResource("ChoiceButton") as Style,
                    IsDefault = index == defaultOptionIndex
                };

                button.Click += OnChoiceClick;
                PART_OptionsPanel.Children.Add(button);

                if (index == defaultOptionIndex)
                    _defaultChoiceButton = button;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_choiceMode && _defaultChoiceButton != null)
            {
                _defaultChoiceButton.Focus();
                return;
            }

            if (DefaultToSecondary &&
                PART_SecondaryButton.Visibility == Visibility.Visible)
            {
                PART_PrimaryButton.IsDefault = false;
                PART_SecondaryButton.IsDefault = true;
                PART_SecondaryButton.Focus();
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            if (_choiceMode)
            {
                SelectedChoiceIndex = -1;
                _result = MessageDialogResult.Cancel;
            }
            else
            {
                switch (_buttons)
                {
                    case MessageDialogButtons.OK:
                        _result = MessageDialogResult.OK;
                        break;
                    case MessageDialogButtons.OKCancel:
                    case MessageDialogButtons.YesNoCancel:
                        _result = MessageDialogResult.Cancel;
                        break;
                    case MessageDialogButtons.YesNo:
                        _result = MessageDialogResult.No;
                        break;
                }
            }

            e.Handled = true;
            DialogResult = true;
        }

        private void ApplyIcon(MessageDialogIcon icon)
        {
            Brush brush = TryFindResource("InfoBrush") as Brush;

            if (icon == MessageDialogIcon.Warning)
                brush = (Brush)(TryFindResource("WarningBrush") ?? brush);
            else if (icon == MessageDialogIcon.Error)
                brush = (Brush)(TryFindResource("ErrorBrush") ?? brush);
            else if (icon == MessageDialogIcon.Success)
                brush = (Brush)(TryFindResource("SuccessBrush") ?? brush);

            PART_IconPath.Fill = brush;

            string pathData;
            switch (icon)
            {
                case MessageDialogIcon.Warning:
                    pathData = "M1,21 L12,1 23,21 Z M12,8 L12,16 M12,18 L12,19";
                    break;
                case MessageDialogIcon.Error:
                    pathData = "M3,3 L21,21 M21,3 L3,21";
                    break;
                case MessageDialogIcon.Success:
                    pathData = "M2,12 L10,20 22,4";
                    break;
                case MessageDialogIcon.Question:
                    pathData = "M12,2 A10,10 0 1 1 12,22 M12,16 L12,18 M12,6 C9,6 8,8 8,9.5 8,10.5 9,11 10.5,11 12,11 12.5,12 12.5,13.5";
                    break;
                default:
                    pathData = "M12,2 A10,10 0 1 1 12,22 M12,6 L12,14 M12,16 L12,18";
                    break;
            }

            try
            {
                PART_IconPath.Data = Geometry.Parse(pathData);
            }
            catch
            {
            }
        }

        private void ConfigureButtons(MessageDialogButtons buttons)
        {
            PART_TertiaryButton.Visibility = Visibility.Collapsed;

            switch (buttons)
            {
                case MessageDialogButtons.OK:
                    PART_PrimaryButton.Content = "OK";
                    PART_SecondaryButton.Visibility = Visibility.Collapsed;
                    PART_PrimaryButton.IsDefault = true;
                    PART_PrimaryButton.IsCancel = true;
                    break;

                case MessageDialogButtons.OKCancel:
                    PART_PrimaryButton.Content = "OK";
                    PART_SecondaryButton.Content = "Cancel";
                    PART_SecondaryButton.Visibility = Visibility.Visible;
                    PART_PrimaryButton.IsDefault = true;
                    PART_SecondaryButton.IsCancel = true;
                    break;

                case MessageDialogButtons.YesNo:
                    PART_PrimaryButton.Content = "Yes";
                    PART_SecondaryButton.Content = "No";
                    PART_SecondaryButton.Visibility = Visibility.Visible;
                    PART_PrimaryButton.IsDefault = true;
                    PART_SecondaryButton.IsCancel = true;
                    break;

                case MessageDialogButtons.YesNoCancel:
                    PART_PrimaryButton.Content = "Yes";
                    PART_SecondaryButton.Content = "No";
                    PART_SecondaryButton.Visibility = Visibility.Visible;
                    PART_TertiaryButton.Content = "Cancel";
                    PART_TertiaryButton.Visibility = Visibility.Visible;
                    PART_PrimaryButton.IsDefault = true;
                    PART_TertiaryButton.IsCancel = true;
                    break;
            }
        }

        private void OnChoiceClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || !(button.Tag is int))
                return;

            SelectedChoiceIndex = (int)button.Tag;
            _result = MessageDialogResult.OK;
            DialogResult = true;
        }

        private void OnPrimaryClick(object sender, RoutedEventArgs e)
        {
            string text = PART_PrimaryButton.Content as string ?? "OK";
            _result = text == "Yes"
                ? MessageDialogResult.Yes
                : MessageDialogResult.OK;

            DialogResult = true;
        }

        private void OnSecondaryClick(object sender, RoutedEventArgs e)
        {
            string text = PART_SecondaryButton.Content as string ?? "Cancel";
            _result = text == "No"
                ? MessageDialogResult.No
                : MessageDialogResult.Cancel;

            DialogResult = true;
        }

        private void OnTertiaryClick(object sender, RoutedEventArgs e)
        {
            SelectedChoiceIndex = -1;
            _result = MessageDialogResult.Cancel;
            DialogResult = true;
        }

        /// <summary>
        /// Shows the dialog modally. Pass Revit's owner handle when available.
        /// Otherwise, the current process main window is used.
        /// </summary>
        public MessageDialogResult ShowModal(IntPtr owner = default(IntPtr))
        {
            try
            {
                if (owner != IntPtr.Zero)
                {
                    new WindowInteropHelper(this) { Owner = owner };
                }
                else
                {
                    Process process = Process.GetCurrentProcess();
                    if (process != null && process.MainWindowHandle != IntPtr.Zero)
                        new WindowInteropHelper(this) { Owner = process.MainWindowHandle };
                }
            }
            catch
            {
                // CenterOwner remains a safe fallback.
            }

            ShowDialog();
            return _result;
        }
    }
}
