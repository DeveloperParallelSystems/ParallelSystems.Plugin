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
    public static class SettingsPanelMenu
    {
        public static void Build(RibbonPanel panel)
        {
            var assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);

            Helpers.PushButton.Add(
                panel,
                "PS_Configurations",
                "Configurations",
                "ParallelSystemPlugin.Commands.ShowConfigurationsCommand",
                "Open the configurations",
                Path.Combine(assemblyDirectory, "Icons", "CogWheel16.ico"),
                Path.Combine(assemblyDirectory, "Icons", "CogWheel32.ico")
            );

            Helpers.PushButton.Add(
                panel,
                "PS_Reconnect",
                "Reconnect",
                "ParallelSystemPlugin.Commands.ReconnectCommand",
                "Reconnect",
                Path.Combine(assemblyDirectory, "Icons", "Sync16.ico"),
                Path.Combine(assemblyDirectory, "Icons", "Sync32.ico")
            );
        }
        
    }
}
