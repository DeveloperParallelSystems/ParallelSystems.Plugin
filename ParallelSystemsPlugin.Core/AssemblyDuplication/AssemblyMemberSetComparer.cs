using System;
using System.Collections.Generic;

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
            if (expected.Count != actual.Count)
                return false;

            var expectedSet = new HashSet<long>(expected);
            var actualSet = new HashSet<long>(actual);
            return expectedSet.Count == expected.Count &&
                   actualSet.Count == actual.Count &&
                   expectedSet.SetEquals(actualSet);
        }
    }
}
