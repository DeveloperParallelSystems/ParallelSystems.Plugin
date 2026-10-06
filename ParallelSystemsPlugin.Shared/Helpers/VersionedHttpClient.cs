using System;
using System.Net.Http;

namespace ParallelSystemsPlugin.Helpers
{
    internal static class VersionedHttpClient
    {
        public static HttpClient Create(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.Add("X-Parallel-Product", "plugin");
            client.DefaultRequestHeaders.Add("X-Parallel-Version",
                typeof(VersionedHttpClient).Assembly.GetName().Version.ToString());
            return client;
        }
    }
}
