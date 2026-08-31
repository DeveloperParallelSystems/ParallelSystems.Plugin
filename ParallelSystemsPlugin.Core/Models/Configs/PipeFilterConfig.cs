namespace ParallelSystemsPlugin.Models.Configs
{
    public class PipeFilterConfig
    {
        public RgbColor MaxPipeColor { get; set; } = new RgbColor(0, 128, 0);
        public RgbColor LongPipeColor { get; set; } = new RgbColor(255, 0, 0);
        public RgbColor ShortPipeColor { get; set; } = new RgbColor(0, 0, 255);
        public double MaxPipeLength { get; set; } = 6000;
        public double LongPipeLength { get; set; } = 6001;
        public double ShortPipeLength { get; set; } = 100;
    }
}
