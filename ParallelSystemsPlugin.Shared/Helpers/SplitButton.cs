using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace ParallelSystemsPlugin.Helpers
{
    public static class SplitButton
    {
        public static Autodesk.Revit.UI.SplitButton AddSplitButton(
        RibbonPanel panel,
        string name,
        string text)
        {
            SplitButtonData sbd = new SplitButtonData(name, text);
            Autodesk.Revit.UI.SplitButton splitBtn = panel.AddItem(sbd) as Autodesk.Revit.UI.SplitButton;
            return splitBtn;
        }
    }
}
