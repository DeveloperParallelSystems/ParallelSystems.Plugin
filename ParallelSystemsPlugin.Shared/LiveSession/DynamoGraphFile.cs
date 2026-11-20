using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ParallelSystemsPlugin.LiveSession
{
    internal sealed class DynamoGraphFile
    {
        internal string Path { get; private set; }
        internal string Sha256 { get; private set; }
        internal JObject Graph { get; private set; }

        internal static DynamoGraphFile Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) ||
                !string.Equals(System.IO.Path.GetExtension(path), ".dyn", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Provide an absolute local .dyn file path.");
            path = System.IO.Path.GetFullPath(path);
            byte[] bytes;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 8 * 1024 * 1024) throw new ArgumentException("Graph exceeds 8 MiB.");
                using (var buffer = new MemoryStream()) { stream.CopyTo(buffer); bytes = buffer.ToArray(); }
            }
            JObject graph;
            using (var stream = new MemoryStream(bytes))
            using (var text = new StreamReader(stream, new UTF8Encoding(false, true), true))
            using (var reader = new JsonTextReader(text) { MaxDepth = 128, DateParseHandling = DateParseHandling.None })
            {
                graph = JObject.Load(reader);
                if (reader.Read()) throw new ArgumentException("Expected one Dynamo JSON graph.");
            }
            if (graph["Nodes"] is not JArray || graph["Connectors"] is not JArray || (bool?)graph["IsCustomNode"] == true)
                throw new ArgumentException("Only JSON home-workspace .dyn graphs are supported.");
            using (var hash = SHA256.Create())
                return new DynamoGraphFile { Path = path, Graph = graph,
                    Sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
        }

        internal void RequireHash(string expected)
        {
            if (!string.Equals(Sha256, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Graph hash changed or expectedSha256 is missing. Inspect the file again.");
        }

        internal JObject Describe() => new JObject
        {
            ["path"] = Path, ["sha256"] = Sha256, ["name"] = Graph["Name"],
            ["workspaceId"] = Graph["Uuid"], ["dynamoVersion"] = Graph.SelectToken("View.Dynamo.Version"),
            ["savedRunType"] = Graph.SelectToken("View.Dynamo.RunType"),
            ["dependencies"] = Graph["NodeLibraryDependencies"]?.DeepClone(),
            ["nodeCount"] = ((JArray)Graph["Nodes"]).Count,
            ["nodes"] = new JArray(((JArray)Graph["Nodes"]).Take(200).OfType<JObject>().Select(node => new JObject
            {
                ["nodeId"] = node["Id"], ["type"] = node["ConcreteType"], ["engine"] = node["Engine"],
                ["inputValue"] = node["InputValue"]?.DeepClone(), ["codeLength"] = ((string)node["Code"] ?? "").Length,
                ["inputs"] = node["Inputs"]?.DeepClone(), ["outputs"] = node["Outputs"]?.DeepClone()
            })),
            ["connectors"] = new JArray(((JArray)Graph["Connectors"]).Take(400)),
            ["inspectionOnly"] = true
        };
    }
}
