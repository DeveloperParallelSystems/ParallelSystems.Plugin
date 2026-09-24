// Created by Jhay
using System;
using System.IO;
using Autodesk.Revit.UI;
using System.Windows.Media.Imaging;

namespace ParallelSystemPlugin.UI
{
    internal static class ProjectLaunchMenu
    {
        public static void Build(RibbonPanel panel)
        {
            if (panel == null) return;
            string assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            string assemblyDirectory = Path.GetDirectoryName(assemblyPath);
            var data = new PushButtonData("PS_ProjectLaunch", "Project\nLaunch", assemblyPath,
                typeof(Commands.ProjectLaunchCommand).FullName)
            {
                ToolTip = "Open the Parallel Systems Project Launch workflow."
            };
            string icon16 = Path.Combine(assemblyDirectory, "Icons", "ParallelSystemLogo16.ico");
            string icon32 = Path.Combine(assemblyDirectory, "Icons", "ParallelSystemLogo32.ico");
            if (File.Exists(icon16)) data.Image = new BitmapImage(new Uri(icon16));
            if (File.Exists(icon32)) data.LargeImage = new BitmapImage(new Uri(icon32));
            panel.AddItem(data);
        }
    }
}
