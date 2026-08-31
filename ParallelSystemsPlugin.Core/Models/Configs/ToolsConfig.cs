using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Models.Configs
{
    public class ToolsConfig
    {
        public PipeSlopeConfig PipeSlopeConfig { get; set; }
        public RenamingConfig RenamingConfig { get; set; }
        public PipeFilterConfig PipeFilterConfig { get; set; } = new PipeFilterConfig();
        public List<EndPrepFilterConfig> EndPrepFilterConfigs { get; set; } = new List<EndPrepFilterConfig>();
        public SheetCheckAndBomCheck SheetCheckAndBomCheckConfig { get; set; } = new SheetCheckAndBomCheck();
    }

}
