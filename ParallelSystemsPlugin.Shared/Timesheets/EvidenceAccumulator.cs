using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.Timesheets
{
    internal sealed class EvidenceAccumulator
    {
        public Dictionary<string, int> CategoryCounts { get; } = NewDictionary();
        public Dictionary<string, int> LevelCounts { get; } = NewDictionary();
        public Dictionary<string, int> SystemCounts { get; } = NewDictionary();
        public Dictionary<string, int> AreaCounts { get; } = NewDictionary();
        public Dictionary<string, int> ZoneCounts { get; } = NewDictionary();
        public Dictionary<string, int> WorksetCounts { get; } = NewDictionary();
        public Dictionary<string, int> TransactionNameCounts { get; } = NewDictionary();

        public int CreatedElementCount { get; private set; }
        public int ModifiedElementCount { get; private set; }
        public int DeletedElementCount { get; private set; }
        public int UninspectedElementCount { get; private set; }

        public void AddCreated() => CreatedElementCount++;
        public void AddModified() => ModifiedElementCount++;
        public void AddDeleted() => DeletedElementCount++;
        public void AddUninspected(int count)
        {
            if (count > 0) UninspectedElementCount += count;
        }

        public void AddCategory(string value) => Add(CategoryCounts, value);
        public void AddLevel(string value) => Add(LevelCounts, value);
        public void AddSystem(string value) => Add(SystemCounts, value);
        public void AddArea(string value) => Add(AreaCounts, value);
        public void AddZone(string value) => Add(ZoneCounts, value);
        public void AddWorkset(string value) => Add(WorksetCounts, value);
        public void AddTransactionName(string value) => Add(TransactionNameCounts, value);

        public string DominantCategory => Dominant(CategoryCounts);
        public string DominantLevel => Dominant(LevelCounts);
        public string DominantSystem => Dominant(SystemCounts);
        public string DominantArea => Dominant(AreaCounts);
        public string DominantZone => Dominant(ZoneCounts);
        public bool HasModelChanges => CreatedElementCount + ModifiedElementCount + DeletedElementCount > 0;

        public EvidenceAccumulator Clone()
        {
            var clone = new EvidenceAccumulator();
            Copy(CategoryCounts, clone.CategoryCounts);
            Copy(LevelCounts, clone.LevelCounts);
            Copy(SystemCounts, clone.SystemCounts);
            Copy(AreaCounts, clone.AreaCounts);
            Copy(ZoneCounts, clone.ZoneCounts);
            Copy(WorksetCounts, clone.WorksetCounts);
            Copy(TransactionNameCounts, clone.TransactionNameCounts);
            clone.CreatedElementCount = CreatedElementCount;
            clone.ModifiedElementCount = ModifiedElementCount;
            clone.DeletedElementCount = DeletedElementCount;
            clone.UninspectedElementCount = UninspectedElementCount;
            return clone;
        }

        private static Dictionary<string, int> NewDictionary()
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Add(IDictionary<string, int> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            int count;
            values.TryGetValue(value, out count);
            values[value] = count + 1;
        }

        private static string Dominant(IDictionary<string, int> values)
        {
            return values.Count == 0
                ? null
                : values.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
        }

        private static void Copy(IDictionary<string, int> source, IDictionary<string, int> destination)
        {
            foreach (var pair in source) destination[pair.Key] = pair.Value;
        }
    }
}
