using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using System.Windows.Media.Imaging;
using System.IO;
using System.Reflection;

namespace ParallelSystemsPlugin.Helpers
{
    public static class PushButton
    {
        public static void Add(
           RibbonPanel panel,
           string name,
           string text,
           string className,
           string tooltip = null,
           string icon16 = null,
           string icon32 = null)
        {
            var assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;

            var pbd = new PushButtonData(name, text, assemblyPath, className);
            Autodesk.Revit.UI.PushButton btn = panel.AddItem(pbd) as Autodesk.Revit.UI.PushButton;

            if (btn != null)
            {
                if (!string.IsNullOrWhiteSpace(tooltip)) btn.ToolTip = tooltip;
                if (!string.IsNullOrWhiteSpace(icon16) && File.Exists(icon16))
                    btn.Image = new BitmapImage(new Uri(icon16));
                if (!string.IsNullOrWhiteSpace(icon32) && File.Exists(icon32))
                    btn.LargeImage = new BitmapImage(new Uri(icon32));
            }
        }

        public static PushButtonData Create(
        string name,
        string text,
        string className,
        string tooltip = null,
        string icon16 = null,
        string icon32 = null)
        {
            var assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            var pbd = new PushButtonData(name, text, assemblyPath, className);

            // For stacked items, tooltips and images must be set after AddStackedItems returns
            return pbd;
        }

        // Optional: helper to apply icon/tooltips after adding
        public static void ApplySettings(Autodesk.Revit.UI.PushButton btn, string tooltip = null, string icon16 = null, string icon32 = null)
        {
            if (btn == null) return;

            if (!string.IsNullOrWhiteSpace(tooltip)) btn.ToolTip = tooltip;
            if (!string.IsNullOrWhiteSpace(icon16) && File.Exists(icon16))
                btn.Image = new BitmapImage(new Uri(icon16));
            if (!string.IsNullOrWhiteSpace(icon32) && File.Exists(icon32))
                btn.LargeImage = new BitmapImage(new Uri(icon32));
        }
    }
}
