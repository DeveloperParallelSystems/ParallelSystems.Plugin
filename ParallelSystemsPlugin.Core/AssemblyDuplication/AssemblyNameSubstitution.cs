using System;

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

            int first = documentName.IndexOf(
                sourceAssemblyName,
                StringComparison.OrdinalIgnoreCase);

            if (first < 0)
                return new AssemblyNameSubstitutionResult(documentName, false, false);

            int second = documentName.IndexOf(
                sourceAssemblyName,
                first + sourceAssemblyName.Length,
                StringComparison.OrdinalIgnoreCase);

            if (second >= 0)
                return new AssemblyNameSubstitutionResult(documentName, false, true);

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
