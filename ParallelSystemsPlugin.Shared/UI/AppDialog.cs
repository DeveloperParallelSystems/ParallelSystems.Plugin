using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace ParallelSystemPlugin.UI
{
    public static class AppDialog
    {
        public static MessageDialogResult Show(
            string title,
            string message,
            MessageDialogIcon icon = MessageDialogIcon.Info,
            MessageDialogButtons buttons = MessageDialogButtons.OK,
            IntPtr owner = default(IntPtr))
        {
            var dlg = new MessageDialog(title, message, icon, buttons);
            return dlg.ShowModal(owner);
        }

        public static MessageDialogResult Show(
            UIApplication uiapp,
            string title,
            string message,
            MessageDialogIcon icon = MessageDialogIcon.Info,
            MessageDialogButtons buttons = MessageDialogButtons.OK)
        {
            return Show(title, message, icon, buttons, GetOwner(uiapp));
        }

        public static void Info(string title, string message)
        {
            Show(title, message, MessageDialogIcon.Info, MessageDialogButtons.OK);
        }

        public static void Info(UIApplication uiapp, string title, string message)
        {
            Show(uiapp, title, message, MessageDialogIcon.Info, MessageDialogButtons.OK);
        }

        public static void Success(string title, string message)
        {
            Show(title, message, MessageDialogIcon.Success, MessageDialogButtons.OK);
        }

        public static void Success(UIApplication uiapp, string title, string message)
        {
            Show(uiapp, title, message, MessageDialogIcon.Success, MessageDialogButtons.OK);
        }

        public static void Warn(string title, string message)
        {
            Show(title, message, MessageDialogIcon.Warning, MessageDialogButtons.OK);
        }

        public static void Warn(UIApplication uiapp, string title, string message)
        {
            Show(uiapp, title, message, MessageDialogIcon.Warning, MessageDialogButtons.OK);
        }

        public static void Error(string title, string message)
        {
            Show(title, message, MessageDialogIcon.Error, MessageDialogButtons.OK);
        }

        public static void Error(UIApplication uiapp, string title, string message)
        {
            Show(uiapp, title, message, MessageDialogIcon.Error, MessageDialogButtons.OK);
        }

        public static bool Confirm(
            string title,
            string message,
            bool defaultNo = false,
            IntPtr owner = default(IntPtr))
        {
            var dlg = new MessageDialog(
                title,
                message,
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo)
            {
                DefaultToSecondary = defaultNo
            };

            return dlg.ShowModal(owner) == MessageDialogResult.Yes;
        }

        public static bool Confirm(
            UIApplication uiapp,
            string title,
            string message,
            bool defaultNo = false)
        {
            return Confirm(title, message, defaultNo, GetOwner(uiapp));
        }

        public static bool ConfirmDetailed(
            UIApplication uiapp,
            string title,
            string instruction,
            string message,
            string details,
            bool defaultNo = true)
        {
            var dlg = new MessageDialog(
                title,
                message,
                MessageDialogIcon.Question,
                MessageDialogButtons.YesNo)
            {
                DefaultToSecondary = defaultNo
            };

            dlg.SetInstruction(instruction);
            dlg.SetDetails(details);

            return dlg.ShowModal(GetOwner(uiapp)) ==
                   MessageDialogResult.Yes;
        }

        public static MessageDialogResult ShowDetailed(
            string title,
            string instruction,
            string message,
            string details,
            MessageDialogIcon icon = MessageDialogIcon.Info,
            IntPtr owner = default(IntPtr))
        {
            var dlg = new MessageDialog(
                title,
                message,
                icon,
                MessageDialogButtons.OK);

            dlg.SetInstruction(instruction);
            dlg.SetDetails(details);
            return dlg.ShowModal(owner);
        }

        public static MessageDialogResult ShowDetailed(
            UIApplication uiapp,
            string title,
            string instruction,
            string message,
            string details,
            MessageDialogIcon icon = MessageDialogIcon.Info)
        {
            return ShowDetailed(
                title,
                instruction,
                message,
                details,
                icon,
                GetOwner(uiapp));
        }

        /// <summary>
        /// Shows a command-choice dialog and returns the selected zero-based
        /// option index. Returns -1 when the user cancels or closes the dialog.
        /// </summary>
        public static int Choose(
            string title,
            string instruction,
            string message,
            IList<string> options,
            int defaultOptionIndex = 0,
            IntPtr owner = default(IntPtr))
        {
            var dlg = new MessageDialog(
                title,
                message,
                MessageDialogIcon.Question,
                MessageDialogButtons.OK);

            dlg.SetInstruction(instruction);
            dlg.ConfigureChoices(options, defaultOptionIndex);
            dlg.ShowModal(owner);
            return dlg.SelectedChoiceIndex;
        }

        public static int Choose(
            UIApplication uiapp,
            string title,
            string instruction,
            string message,
            IList<string> options,
            int defaultOptionIndex = 0)
        {
            return Choose(
                title,
                instruction,
                message,
                options,
                defaultOptionIndex,
                GetOwner(uiapp));
        }

        internal static ImageSource LoadWindowIcon()
        {
            try
            {
                string assemblyDirectory = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);

                if (string.IsNullOrWhiteSpace(assemblyDirectory))
                    return null;

                string[] candidates =
                {
                    Path.Combine(
                        assemblyDirectory,
                        "Icons",
                        "ParallelSystemLogo32.ico"),
                    Path.Combine(
                        assemblyDirectory,
                        "Icons",
                        "ParallelSystemLogo16.ico")
                };

                foreach (string path in candidates)
                {
                    if (!File.Exists(path))
                        continue;

                    using (FileStream stream = File.OpenRead(path))
                    {
                        var decoder = new IconBitmapDecoder(
                            stream,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);

                        if (decoder.Frames.Count == 0)
                            continue;

                        BitmapFrame frame = decoder.Frames[0];
                        if (frame.CanFreeze)
                            frame.Freeze();

                        return frame;
                    }
                }
            }
            catch
            {
                // A missing icon must never prevent an application dialog.
            }

            return null;
        }

        private static IntPtr GetOwner(UIApplication uiapp)
        {
            return uiapp != null
                ? uiapp.MainWindowHandle
                : default(IntPtr);
        }
    }
}
