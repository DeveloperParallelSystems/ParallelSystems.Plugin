using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace ParallelSystemsPlugin.UI
{
    public class DetailingMenu
    {
        public static void Build(RibbonPanel panel)
        {
            var assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);

            var splitBtn = Helpers.SplitButton.AddSplitButton(panel, "DetailSplit", "DetailSplit");

            // Create button data
            var btn1 = Helpers.PushButton.Create(
                "ApplyDetailingButton",
                "Detail",
                "ParallelSystemPlugin.Commands.ApplyDetailingCommand",
                "Apply Detailing");

            //var btn2 = Helpers.PushButton.Create(
            //    "ClearDetailingView",
            //    "Select All",
            //    "YourNamespace.SelectAllSheetElements",
            //    "Select all elements in sheet");

            // ✅ Add ONE BY ONE
            var pb1 = splitBtn.AddPushButton(btn1);
            //var pb2 = splitBtn.AddPushButton(btn2); 

            // Apply icons/tooltips AFTER adding (important!)
            Helpers.PushButton.ApplySettings(pb1, "Fit views", null, icon32: Path.Combine(assemblyDirectory, "Icons", "File32.ico"));
            //Helpers.PushButton.ApplySettings(pb2, "Select elements", null, icon32: Path.Combine(assemblyDirectory, "Icons", "import32.ico"));
        }
    }
}
