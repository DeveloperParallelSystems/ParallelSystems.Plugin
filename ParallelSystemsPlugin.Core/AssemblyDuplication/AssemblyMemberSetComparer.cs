using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: exact order-independent membership validation for copied IDs.
    public static class AssemblyMemberSetComparer
    {
        public static bool Matches(
            IReadOnlyCollection<long> expected,
            IReadOnlyCollection<long> actual)
        {
            if (expected == null)
                throw new ArgumentNullException(nameof(expected));
            if (actual == null)
                throw new ArgumentNullException(nameof(actual));
            return Canonicalize(expected).SequenceEqual(Canonicalize(actual));
        }

        // Changed by Jhay: canonical snapshots prevent Revit enumeration order from causing stale plans.
        public static IReadOnlyList<long> Canonicalize(IEnumerable<long> ids)
        {
            if (ids == null)
                throw new ArgumentNullException(nameof(ids));

            return ids.Distinct().OrderBy(id => id).ToArray();
        }
    }
}
