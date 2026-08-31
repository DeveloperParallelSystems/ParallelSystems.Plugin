using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Models
{
    public class PipeWeightParams
    {
        public bool HaveDry { get; set; }
        public bool HaveWet { get; set; }
        public bool HaveCladding { get; set; }
        public bool HaveFluidWeight { get; set; }
        public bool HaveInsulationWeight { get; set; }
        public bool HaveOverallSize { get; set; }
        public bool HaveTotalWeight { get; set; }
        public int NumDecimals { get; set; }
    }
}
