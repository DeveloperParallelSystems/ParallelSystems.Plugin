using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Changed by Jhay: plan deterministic numbering against complete target names with optional anchors.
    public static class AssemblyBatchNumberingService
    {
        public static AssemblyBatchNumberingResult CreatePlan(
            IReadOnlyCollection<AssemblyNamingCandidate> candidates,
            long startingNumber,
            IReadOnlyCollection<string> occupiedNames,
            IReadOnlyDictionary<long, long> requestedAnchors)
        {
            if (candidates == null)
                throw new ArgumentNullException(nameof(candidates));
            if (occupiedNames == null)
                throw new ArgumentNullException(nameof(occupiedNames));
            if (requestedAnchors == null)
                throw new ArgumentNullException(nameof(requestedAnchors));

            var issues = new List<AssemblyBatchNumberingIssue>();
            if (startingNumber < 0)
            {
                issues.Add(new AssemblyBatchNumberingIssue(
                    null,
                    string.Empty,
                    "Starting number cannot be negative."));
                return new AssemblyBatchNumberingResult(null, issues);
            }
            foreach (KeyValuePair<long, long> anchor in requestedAnchors.Where(item => item.Value < 0))
            {
                AssemblyNamingCandidate candidate = candidates.FirstOrDefault(
                    item => item != null && item.StableElementId == anchor.Key);
                issues.Add(new AssemblyBatchNumberingIssue(
                    anchor.Key,
                    candidate?.SourceName ?? string.Empty,
                    "A manual anchor cannot be negative."));
            }
            if (issues.Count > 0)
                return new AssemblyBatchNumberingResult(null, issues);

            var parsed = new List<ParsedCandidate>(candidates.Count);
            foreach (AssemblyNamingCandidate candidate in candidates)
            {
                if (candidate == null)
                {
                    issues.Add(new AssemblyBatchNumberingIssue(
                        null,
                        string.Empty,
                        "A naming candidate is null."));
                    continue;
                }

                if (!AssemblyNameParser.TryParse(
                        candidate.SourceName,
                        out AssemblyNameParts parts,
                        out string error))
                {
                    issues.Add(new AssemblyBatchNumberingIssue(
                        candidate.StableElementId,
                        candidate.SourceName,
                        "Assembly '" + candidate.SourceName + "' is invalid: " + error));
                    continue;
                }

                parsed.Add(new ParsedCandidate(candidate, parts));
            }

            List<ParsedCandidate> ordered = parsed
                .OrderBy(item => item.Parts.NumericValue)
                .ThenBy(item => item.Candidate.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Candidate.StableElementId)
                .ToList();
            var unavailableNames = new HashSet<string>(
                occupiedNames.Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allSkippedNames = new List<string>();
            var assignments = new List<AssemblyBatchNumberingAssignment>(ordered.Count);
            long cursor = startingNumber;

            for (int index = 0; index < ordered.Count; index++)
            {
                ParsedCandidate item = ordered[index];
                long? requestedAnchor = requestedAnchors.TryGetValue(
                    item.Candidate.StableElementId,
                    out long anchor)
                    ? anchor
                    : (long?)null;
                if (requestedAnchor.HasValue)
                    cursor = requestedAnchor.Value;

                var skippedForSource = new List<string>();
                string proposedName = BuildTargetName(item.Parts, cursor);
                while (unavailableNames.Contains(proposedName) || reservedNames.Contains(proposedName))
                {
                    skippedForSource.Add(proposedName);
                    allSkippedNames.Add(proposedName);
                    if (cursor == long.MaxValue)
                        return Exhausted(item, issues);
                    cursor = checked(cursor + 1);
                    proposedName = BuildTargetName(item.Parts, cursor);
                }

                long assigned = cursor;
                reservedNames.Add(proposedName);
                assignments.Add(new AssemblyBatchNumberingAssignment(
                    new ProposedAssemblyName(
                        item.Candidate.SourceName,
                        item.Candidate.StableElementId,
                        item.Parts.NumericValue,
                        assigned,
                        proposedName),
                    requestedAnchor,
                    skippedForSource));

                if (index < ordered.Count - 1)
                {
                    ParsedCandidate next = ordered[index + 1];
                    // Changed by Jhay: a lower-row manual anchor starts a new sequence and does not
                    // require incrementing the preceding row's resolved Int64 value.
                    if (!requestedAnchors.ContainsKey(next.Candidate.StableElementId))
                    {
                        if (cursor == long.MaxValue)
                            return Exhausted(next, issues);
                        cursor = checked(cursor + 1);
                    }
                }
            }

            return new AssemblyBatchNumberingResult(
                new AssemblyBatchNumberingPlan(startingNumber, assignments, allSkippedNames),
                issues);
        }

        private static string BuildTargetName(AssemblyNameParts parts, long number)
        {
            string numericText = number.ToString(
                "D" + parts.MinimumWidth,
                CultureInfo.InvariantCulture);
            return parts.Prefix + numericText + parts.Suffix;
        }

        private static AssemblyBatchNumberingResult Exhausted(
            ParsedCandidate candidate,
            ICollection<AssemblyBatchNumberingIssue> issues)
        {
            issues.Add(new AssemblyBatchNumberingIssue(
                candidate.Candidate.StableElementId,
                candidate.Candidate.SourceName,
                "No available complete target assembly name remains within Int64 numbering."));
            return new AssemblyBatchNumberingResult(null, issues);
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
