using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Models.Configs
{
    public class ApplicationConfig
    {
        public MapParameters PipeMapParameters { get; set; }
        public List<IgnoreComponent> PipeIgnoreComponents { get; set; } 
        public List<EndPrep> PipeEndPreps { get; set; }
        public MapParameters FittingsMapParameters { get; set; }
        public List<IgnoreComponent> FittingsIgnoreComponents { get; set; }
        public List<EndPrep> FittingsEndPreps { get; set; }
        public List<AllowedMapFittingsElement> AllowedMapFittingsElements {  get; set; }
        public MapParameters PipeWeightMapParameters { get; set; }
        public List<ElementsWeight> ElementsWeight { get; set; }
        public MaterialProperties PipeWeightMaterialProperties { get; set; }
        public List<SystemAbbreviation> SystemAbbreviations { get; set; }
        public ProcurementConfig Procurement { get; set; }
        public ToolsConfig ToolsConfig { get; set; }

        public bool HasNullProperty()
        {
            return GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p => p.GetValue(this) == null);
        }
    }
}
