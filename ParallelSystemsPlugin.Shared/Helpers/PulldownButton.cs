using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using System.Windows.Media.Imaging;
using System.IO;

namespace ParallelSystemsPlugin.Helpers
{
    public class PulldownButton
    {
        public static PulldownButtonData Create(
            string name,
            string text,
            string tooltip = null,
            string icon16 = null,
            string icon32 = null)
        {
            var pbd = new PulldownButtonData(name, text);

            // For stacked items, tooltips and images must be set after AddStackedItems returns
            return pbd;
        }

        public static void ApplySettings(
            Autodesk.Revit.UI.PulldownButton btn,
            string tooltip = null,
            string icon16 = null,
            string icon32 = null)
        {
            if (btn == null) return;

            if (!string.IsNullOrWhiteSpace(tooltip))
                btn.ToolTip = tooltip;

            if (!string.IsNullOrWhiteSpace(icon16) && File.Exists(icon16))
                btn.Image = new BitmapImage(new Uri(icon16));

            if (!string.IsNullOrWhiteSpace(icon32) && File.Exists(icon32))
                btn.LargeImage = new BitmapImage(new Uri(icon32));
        }
    }
}
