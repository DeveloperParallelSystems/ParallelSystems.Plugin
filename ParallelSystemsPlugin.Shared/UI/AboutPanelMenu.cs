using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin.Helpers;

namespace ParallelSystemsPlugin.UI
{
    public static class AboutPanelMenu
    {
        public static void Build(RibbonPanel panel)
        {
            var assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);

            Helpers.PushButton.Add(
                panel,
                "PS_About",
                "About & Manual",
                "ParallelSystemPlugin.Commands.ShowAboutCommand",
                "Open the user manual, version info, and what's new.",
                Path.Combine(assemblyDirectory, "Icons", "About16.ico"),
                Path.Combine(assemblyDirectory, "Icons", "About32.ico")
            );

            Helpers.PushButton.Add(
                panel,
                "PS_AboutBtn",
                "About Us",
                "ParallelSystemPlugin.Commands.AboutUsCommand",
                "Open the ParallelSystems website in your browser.",
                Path.Combine(assemblyDirectory, "Icons", "ParallelSystemLogo16.ico"),
                Path.Combine(assemblyDirectory, "Icons", "ParallelSystemLogo32.ico")
            );
        }
    }
}
