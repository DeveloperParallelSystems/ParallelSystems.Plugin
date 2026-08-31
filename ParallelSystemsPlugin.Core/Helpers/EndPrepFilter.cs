using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models.Configs;

namespace ParallelSystemsPlugin.Helpers
{
    public static class EndPrepFilter
    {
        public static List<EndPrepFilterConfig> GetFilterConfigurations()
        {
            return AppConfig.CurrentConfig.ToolsConfig.EndPrepFilterConfigs;
        }

        public static List<EndPrepFilterConfig> GetFilterConfigurations(List<string> input)
        {
            var result = new List<EndPrepFilterConfig>();

            for (int i = 0; i < input.Count; i++)
            {
                for (int j = i; j < input.Count; j++)
                {
                    var values = new List<string>();

                    // Self pair (BE-BE)
                    if (i == j)
                    {
                        values.Add($"{input[i]}-{input[j]}");
                    }
                    else
                    {
                        // Add both directions (BE-PE and PE-BE)
                        values.Add($"{input[i]}-{input[j]}");
                        values.Add($"{input[j]}-{input[i]}");
                    }

                    result.Add(new EndPrepFilterConfig
                    {
                        Name = $"EP-{values.First()}", // disregarded
                        Values = values,
                        Color = new RgbColor(255,255,255) // optional helper
                    });
                }
            }

            //// Add UNDEFINED manually if neededs
            //result.Add(new EndPrepFilterConfig
            //{
            //    Name = "EP-Undefined",
            //    Values = new List<string> { "Undefined", "" },
            //    Color = new RgbColor(255, 255, 255)
            //});

            return result;
        }

        public static List<EndPrepFilterConfig> MatchMethod(List<EndPrepFilterConfig> newFilters, List<EndPrepFilterConfig> existingFilters)
        {
            // Create lookup for existing filters
            var existingDict = existingFilters.ToDictionary(f => f.Name, f => f);

            var result = new List<EndPrepFilterConfig>();

            foreach (var newFilter in newFilters)
            {
                if (existingDict.TryGetValue(newFilter.Name, out var existing))
                {
                    // Match found → retain existing (keep original Color)
                    result.Add(existing);
                }
                else
                {
                    // No match → add new
                    result.Add(newFilter);
                }
            }

            return result;
        }

        private static RgbColor GenerateColor(int i, int j)
        {
            return new RgbColor(
                (byte)((i * 70) % 256),
                (byte)((j * 120) % 256),
                (byte)(((i + j) * 50) % 256)
            );
        }
    }
}
