using System;
using System.Collections.Generic;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: safe assembly-token replacement for documentation names.
    public static class AssemblyNameSubstitution
    {
        public static AssemblyNameSubstitutionResult ReplaceExactToken(
            string documentName,
            string sourceAssemblyName,
            string proposedAssemblyName)
        {
            if (documentName == null)
                throw new ArgumentNullException(nameof(documentName));
            if (string.IsNullOrEmpty(sourceAssemblyName))
                throw new ArgumentException("Source assembly name is required.", nameof(sourceAssemblyName));
            if (proposedAssemblyName == null)
                throw new ArgumentNullException(nameof(proposedAssemblyName));

            var matches = new List<int>();
            int searchStart = 0;
            while (searchStart <= documentName.Length - sourceAssemblyName.Length)
            {
                int match = documentName.IndexOf(
                    sourceAssemblyName,
                    searchStart,
                    StringComparison.OrdinalIgnoreCase);
                if (match < 0)
                    break;

                int matchEnd = match + sourceAssemblyName.Length;
                bool startsAtBoundary = match == 0 ||
                                        !char.IsLetterOrDigit(documentName[match - 1]);
                bool endsAtBoundary = matchEnd == documentName.Length ||
                                      !char.IsLetterOrDigit(documentName[matchEnd]);

                if (startsAtBoundary && endsAtBoundary)
                    matches.Add(match);

                searchStart = match + sourceAssemblyName.Length;
            }

            if (matches.Count == 0)
                return new AssemblyNameSubstitutionResult(documentName, false, false);
            if (matches.Count > 1)
                return new AssemblyNameSubstitutionResult(documentName, false, true);

            int first = matches[0];

            string value = documentName.Substring(0, first) +
                           proposedAssemblyName +
                           documentName.Substring(first + sourceAssemblyName.Length);

            return new AssemblyNameSubstitutionResult(value, true, false);
        }
    }

    public sealed class AssemblyNameSubstitutionResult
    {
        public AssemblyNameSubstitutionResult(
            string value,
            bool wasReplaced,
            bool isAmbiguous)
        {
            Value = value;
            WasReplaced = wasReplaced;
            IsAmbiguous = isAmbiguous;
        }

        public string Value { get; }

        public bool WasReplaced { get; }

        public bool IsAmbiguous { get; }
    }
}
