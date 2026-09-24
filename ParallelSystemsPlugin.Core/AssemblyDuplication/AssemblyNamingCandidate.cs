using System;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: Revit-independent source identity used for stable naming.
    public sealed class AssemblyNamingCandidate
    {
        public AssemblyNamingCandidate(string sourceName, long stableElementId)
        {
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
            StableElementId = stableElementId;
        }

        public string SourceName { get; }

        public long StableElementId { get; }
    }
}
