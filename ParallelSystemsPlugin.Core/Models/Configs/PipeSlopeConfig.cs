using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Models.Configs
{
    public class PipeSlopeConfig
    {
        public List<double> AllowedAngles { get; set; }
        public double AcceptedTolerance { get; set; }
    }
}
