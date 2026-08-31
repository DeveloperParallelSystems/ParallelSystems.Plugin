using System;
using Newtonsoft.Json;

namespace ParallelSystemsPlugin.Models.Configs
{
    /// <summary>
    /// Framework-neutral RGB color used by persisted configuration and shared logic.
    /// It accepts the legacy WPF R/G/B JSON shape and the legacy Revit Red/Green/Blue shape.
    /// </summary>
    public sealed class RgbColor : IEquatable<RgbColor>
    {
        public RgbColor() { }

        public RgbColor(byte red, byte green, byte blue)
        {
            Red = red;
            Green = green;
            Blue = blue;
        }

        public byte Red { get; set; }
        public byte Green { get; set; }
        public byte Blue { get; set; }

        [JsonIgnore]
        public byte R { get { return Red; } }

        [JsonIgnore]
        public byte G { get { return Green; } }

        [JsonIgnore]
        public byte B { get { return Blue; } }

        [JsonProperty("R")]
        private byte LegacyR { set { Red = value; } }

        [JsonProperty("G")]
        private byte LegacyG { set { Green = value; } }

        [JsonProperty("B")]
        private byte LegacyB { set { Blue = value; } }

        public static RgbColor FromRgb(byte red, byte green, byte blue)
        {
            return new RgbColor(red, green, blue);
        }

        public bool Equals(RgbColor other)
        {
            return other != null && Red == other.Red && Green == other.Green && Blue == other.Blue;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as RgbColor);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Red * 397) ^ (Green * 31) ^ Blue;
            }
        }
    }
}
