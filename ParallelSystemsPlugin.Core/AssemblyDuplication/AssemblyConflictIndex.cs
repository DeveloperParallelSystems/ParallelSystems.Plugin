using System;
using System.Collections.Generic;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: one-time indexes used by duplication preflight.
    public sealed class AssemblyConflictIndex
    {
        private readonly HashSet<string> _assemblyNames;
        private readonly HashSet<string> _sheetNumbers;
        private readonly HashSet<string> _viewNames;

        public AssemblyConflictIndex(
            IEnumerable<string> assemblyNames,
            IEnumerable<string> sheetNumbers,
            IEnumerable<string> viewNames)
        {
            _assemblyNames = CreateSet(assemblyNames);
            _sheetNumbers = CreateSet(sheetNumbers);
            _viewNames = CreateSet(viewNames);
        }

        internal bool ContainsAssemblyName(string value) =>
            _assemblyNames.Contains(value ?? string.Empty);

        internal bool ContainsSheetNumber(string value) =>
            _sheetNumbers.Contains(value ?? string.Empty);

        internal bool ContainsViewName(string value) =>
            _viewNames.Contains(value ?? string.Empty);

        private static HashSet<string> CreateSet(IEnumerable<string> values)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
                return result;

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }

            return result;
        }
    }
}
