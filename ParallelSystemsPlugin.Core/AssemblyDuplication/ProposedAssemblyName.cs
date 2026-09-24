namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: one deterministic source-to-target assembly name mapping.
    public sealed class ProposedAssemblyName
    {
        public ProposedAssemblyName(
            string sourceName,
            long stableElementId,
            long sourceNumericValue,
            long assignedNumericValue,
            string proposedName)
        {
            SourceName = sourceName;
            StableElementId = stableElementId;
            SourceNumericValue = sourceNumericValue;
            AssignedNumericValue = assignedNumericValue;
            ProposedName = proposedName;
        }

        public string SourceName { get; }

        public long StableElementId { get; }

        public long SourceNumericValue { get; }

        public long AssignedNumericValue { get; }

        public string ProposedName { get; }
    }
}
