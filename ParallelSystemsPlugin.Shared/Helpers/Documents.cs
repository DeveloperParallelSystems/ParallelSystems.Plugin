using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB;

namespace ParallelSystemsPlugin.Helpers
{
    public static class Documents
    {
        public static List<string> GetAllPipeNames(this Document doc)
        {
            return new FilteredElementCollector(doc)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>()
            .Select(pt => pt.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToList();
        }
    }
}
