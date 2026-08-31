using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Models.Configs
{
    public class MapParameters
    {
        public string End1 { get; set; } = string.Empty;
        public string End2 { get; set; } = string.Empty;
        public string EndPrep { get; set; } = string.Empty;
        public string Unconnected { get; set; } = string.Empty;
        public bool EnableMapping { get; set; }
        public string HeaderND { get; set; } = string.Empty;
        public string DryWeight { get; set; } = string.Empty;
        public string WetWeight { get; set; } = string.Empty;
        public int NumOfDecimals { get; set; } = 2;
        public string CladdingWeight { get; set; } = string.Empty;
        public string InsulationWeight { get; set; } = string.Empty;
        public string FluidWeight { get; set; } = string.Empty;
        public string TotalWeight { get; set; } = string.Empty;
        public string ComputedOverallSize { get; set; } = string.Empty;

    }
}
