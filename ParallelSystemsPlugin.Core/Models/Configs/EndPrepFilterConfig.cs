using System.Collections.Generic;

namespace ParallelSystemsPlugin.Models.Configs
{
    public class EndPrepFilterConfig
    {
        public string Name { get; set; }
        public List<string> Values { get; set; }
        public RgbColor Color { get; set; } = new RgbColor(255, 255, 255);
    }
}
