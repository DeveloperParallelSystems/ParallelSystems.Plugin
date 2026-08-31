using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Helpers
{
    public static class Unit
    {
        private const double FT_TO_MM = 304.8;
        private const double FT_TO_M = 0.3048;
       
        //feet
        public static double FeetToMm (this double val)
        {
            return val * FT_TO_MM;
        }

        public static double FeetToM(this double val)
        {
            return val * FT_TO_M;
        }
    }
}
