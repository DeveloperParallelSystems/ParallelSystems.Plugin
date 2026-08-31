using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using System.IO;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using static ParallelSystemsPlugin.Helpers.Elements;

using ParallelSystemsPlugin.Compatibility;
namespace ParallelSystemsPlugin.Helpers
{
    public class AssemblyCSVHelpers
    {
        public static int Export(Document doc, string path)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(AssemblyType))
                .Cast<AssemblyType>()
                .ToList();


            List<string> lines = new List<string>();

            lines.Add("AssemblyTypeId,OriginalAssemblyName,Vic_Zone,Vic_Area_PT,NewAssemblyName,NewVic_Zone,NewVic_Area_PT");

            foreach (var type in types)
            {
                string name = GetTypeName(type);

                lines.Add($"{RevitApiCompatibility.GetElementIdValue(type.Id)},{name},,,,");

            }

            File.WriteAllLines(path, lines);

            return 0;
        }

        public static void Import(Document doc, string path, out int updated)
        {
            var lines = File.ReadAllLines(path).Skip(1);

            using (Transaction t = new Transaction(doc, "Import Assembly CSV"))
            {
                t.Start();

                int count = 0;

                foreach (var line in lines)
                {
                    var cols = line.Split(',');

                    if (cols.Length < 7)
                        continue;

                    long typeId = long.Parse(cols[0], System.Globalization.CultureInfo.InvariantCulture);

                    string newName = cols[4];
                    string newZone = cols[5];
                    string newArea = cols[6];

                    AssemblyType type = doc.GetElement(RevitApiCompatibility.CreateElementId(typeId)) as AssemblyType;

                    if (type == null)
                        continue;

                    bool changed = false;

                    // Update Name
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        string currentName = GetTypeName(type);

                        if (Normalize(currentName) != Normalize(newName))
                        {
                            type.Name = newName;
                            changed = true;
                        }
                    }

                    // Update Vic_Zone
                    if (!string.IsNullOrWhiteSpace(newZone))
                    {
                        var zoneParam = type.LookupParameter("Vic_Zone");

                        if (zoneParam != null && !zoneParam.IsReadOnly)
                        {
                            zoneParam.Set(newZone);
                            changed = true;
                        }
                    }

                    // Update Vic_Area_PT
                    if (!string.IsNullOrWhiteSpace(newArea))
                    {
                        var areaParam = type.LookupParameter("Vic_Area_PT");

                        if (areaParam != null && !areaParam.IsReadOnly)
                        {
                            areaParam.Set(newArea);
                            changed = true;
                        }
                    }

                    if (changed)
                        count++;
                }

                updated = count;

                t.Commit();
            }
        }

        public static string GetTypeName(AssemblyType type)
        {
            Parameter p = type.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);

            if (p != null)
                return p.AsString();

            return type.Name;
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            return s.Trim().ToLower();
        }
    }
}
