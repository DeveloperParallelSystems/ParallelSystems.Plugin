namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: immutable result of parsing the final numeric block.
    public sealed class AssemblyNameParts
    {
        public AssemblyNameParts(
            string sourceName,
            string prefix,
            string numericText,
            long numericValue,
            string suffix)
        {
            SourceName = sourceName;
            Prefix = prefix;
            NumericText = numericText;
            NumericValue = numericValue;
            Suffix = suffix;
        }

        public string SourceName { get; }

        public string Prefix { get; }

        public string NumericText { get; }

        public long NumericValue { get; }

        public string Suffix { get; }

        public int MinimumWidth => NumericText.Length;
    }
}
