using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: restricts the Phase 2 diagnostic to AssemblyInstance elements.
    internal sealed class AssemblyInstanceSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element element) => element is AssemblyInstance;

        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
