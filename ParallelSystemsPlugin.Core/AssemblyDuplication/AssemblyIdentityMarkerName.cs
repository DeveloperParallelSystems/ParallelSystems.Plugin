using System;
using System.Text;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    public static class AssemblyIdentityMarkerName
    {
        private const string Prefix = "PS_ASM_ID_";
        private const int MaximumLength = 120;

        public static string Create(string visibleAssemblyName, Guid uniqueId)
        {
            if (string.IsNullOrWhiteSpace(visibleAssemblyName))
                throw new ArgumentException("An assembly name is required.", nameof(visibleAssemblyName));

            var readable = new StringBuilder(visibleAssemblyName.Length);
            bool pendingSeparator = false;

            foreach (char character in visibleAssemblyName.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(character) || character == '_' || character == '-')
                {
                    if (pendingSeparator && readable.Length > 0 && readable[readable.Length - 1] != '_')
                        readable.Append('_');

                    readable.Append(character);
                    pendingSeparator = false;
                }
                else
                {
                    pendingSeparator = true;
                }
            }

            string fragment = readable.ToString().Trim('_', '-');
            if (fragment.Length == 0)
                fragment = "ASSEMBLY";

            string suffix = uniqueId.ToString("N").Substring(0, 8).ToUpperInvariant();
            int maximumFragmentLength = MaximumLength - Prefix.Length - 1 - suffix.Length;
            if (fragment.Length > maximumFragmentLength)
                fragment = fragment.Substring(0, maximumFragmentLength).TrimEnd('_', '-');

            return Prefix + fragment + "_" + suffix;
        }
    }
}
