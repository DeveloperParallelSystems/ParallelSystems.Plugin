using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: deterministic numeric ordering and sequence generation.
    public static class AssemblyNamingService
    {
        public static IReadOnlyList<ProposedAssemblyName> Generate(
            IReadOnlyCollection<AssemblyNamingCandidate> candidates,
            long startingNumber)
        {
            if (candidates == null)
                throw new ArgumentNullException(nameof(candidates));
            if (startingNumber < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(startingNumber),
                    "Starting number cannot be negative.");

            var parsed = new List<ParsedCandidate>(candidates.Count);
            foreach (AssemblyNamingCandidate candidate in candidates)
            {
                if (candidate == null)
                    throw new ArgumentException("A naming candidate is null.", nameof(candidates));

                if (!AssemblyNameParser.TryParse(
                        candidate.SourceName,
                        out AssemblyNameParts parts,
                        out string error))
                {
                    throw new ArgumentException(
                        $"Assembly '{candidate.SourceName}' is invalid: {error}",
                        nameof(candidates));
                }

                parsed.Add(new ParsedCandidate(candidate, parts));
            }

            List<ParsedCandidate> ordered = parsed
                .OrderBy(x => x.Parts.NumericValue)
                .ThenBy(x => x.Candidate.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Candidate.StableElementId)
                .ToList();

            var results = new List<ProposedAssemblyName>(ordered.Count);
            for (int index = 0; index < ordered.Count; index++)
            {
                long assignedValue = checked(startingNumber + index);
                ParsedCandidate item = ordered[index];
                string numericText = assignedValue.ToString(
                    "D" + item.Parts.MinimumWidth,
                    CultureInfo.InvariantCulture);

                results.Add(new ProposedAssemblyName(
                    item.Candidate.SourceName,
                    item.Candidate.StableElementId,
                    item.Parts.NumericValue,
                    assignedValue,
                    item.Parts.Prefix + numericText + item.Parts.Suffix));
            }

            return results;
        }

        private sealed class ParsedCandidate
        {
            public ParsedCandidate(
                AssemblyNamingCandidate candidate,
                AssemblyNameParts parts)
            {
                Candidate = candidate;
                Parts = parts;
            }

            public AssemblyNamingCandidate Candidate { get; }

            public AssemblyNameParts Parts { get; }
        }
    }
}
