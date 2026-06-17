using DBCD.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace WDBXEditor2.Controller
{
    public class DBDProvider : IDBDProvider
    {
        private static Uri BaseURI = new Uri("https://raw.githubusercontent.com/wowdev/WoWDBDefs/master/definitions/");
        private static Uri GitHubDefinitionsApi = new Uri("https://api.github.com/repos/wowdev/WoWDBDefs/contents/definitions?ref=master");
        private static string CachePath = Path.Combine(AppContext.BaseDirectory, "Cache");
        private static string ManifestCachePath = Path.Combine(CachePath, "definitions-manifest.json");
        private static TimeSpan CacheRefreshTime = TimeSpan.FromHours(24);
        private static TimeSpan NetworkTimeout = TimeSpan.FromSeconds(10);
        private static Dictionary<string, string> DbdNameLookup;
        private HttpClient client = new HttpClient();

        public DBDProvider()
        {
            if (!Directory.Exists(CachePath))
                Directory.CreateDirectory(CachePath);

            client.BaseAddress = BaseURI;
            client.Timeout = NetworkTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WDBXEditor2");
        }

        public Stream StreamForTableName(string tableName, string build = null)
        {
            foreach (string dbdName in GetDbdNameCandidates(tableName))
            {
                string cacheFile = Path.Combine(CachePath, dbdName);

                if (File.Exists(cacheFile))
                {
                    if (DateTime.Now - File.GetLastWriteTime(cacheFile) > CacheRefreshTime)
                        TryRefreshCachedFile(dbdName, cacheFile);

                    return new MemoryStream(File.ReadAllBytes(cacheFile));
                }

                try
                {
                    var bytes = DownloadBytes(dbdName);
                    File.WriteAllBytes(cacheFile, bytes);
                    return new MemoryStream(bytes);
                }
                catch
                {
                    // Try the next casing candidate before surfacing a network/404 failure.
                }
            }

            throw new FileNotFoundException($"Unable to find definitions for '{Path.GetFileName(tableName)}'. Check GitHub/network access, or place the matching .dbd file in '{CachePath}'.");
        }

        private IEnumerable<string> GetDbdNameCandidates(string tableName)
        {
            string requestedName = Path.GetFileNameWithoutExtension(tableName) + ".dbd";
            string cachedName = Directory
                .EnumerateFiles(CachePath, "*.dbd")
                .Select(Path.GetFileName)
                .FirstOrDefault(name => string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(cachedName))
                yield return cachedName;

            Dictionary<string, string> manifest = null;
            try
            {
                manifest = GetDbdNameLookup();
            }
            catch
            {
                // The manifest is only for casing resolution. Direct candidates below still work offline or with partial network access.
            }

            if (manifest != null && manifest.TryGetValue(requestedName, out string resolvedName))
                yield return resolvedName;

            yield return requestedName;

            if (!string.IsNullOrEmpty(requestedName))
                yield return char.ToUpperInvariant(requestedName[0]) + requestedName.Substring(1);
        }

        private Dictionary<string, string> GetDbdNameLookup()
        {
            if (DbdNameLookup != null)
                return DbdNameLookup;

            if (!File.Exists(ManifestCachePath))
            {
                var manifestBytes = DownloadBytes(GitHubDefinitionsApi);
                File.WriteAllBytes(ManifestCachePath, manifestBytes);
            }
            else if (DateTime.Now - File.GetLastWriteTime(ManifestCachePath) > CacheRefreshTime)
            {
                TryRefreshCachedFile(GitHubDefinitionsApi, ManifestCachePath);
            }

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(File.ReadAllBytes(ManifestCachePath));

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var nameProperty))
                    continue;

                string name = nameProperty.GetString();
                if (!string.IsNullOrEmpty(name) && name.EndsWith(".dbd", StringComparison.OrdinalIgnoreCase))
                    lookup[name] = name;
            }

            DbdNameLookup = lookup;
            return DbdNameLookup;
        }

        private byte[] DownloadBytes(string relativeUrl)
        {
            return client.GetByteArrayAsync(relativeUrl).GetAwaiter().GetResult();
        }

        private byte[] DownloadBytes(Uri uri)
        {
            return client.GetByteArrayAsync(uri).GetAwaiter().GetResult();
        }

        private void TryRefreshCachedFile(string relativeUrl, string cacheFile)
        {
            try
            {
                File.WriteAllBytes(cacheFile, client.GetByteArrayAsync(relativeUrl).GetAwaiter().GetResult());
            }
            catch
            {
                // Keep stale cache usable when GitHub or the local network is unavailable.
            }
        }

        private void TryRefreshCachedFile(Uri uri, string cacheFile)
        {
            try
            {
                File.WriteAllBytes(cacheFile, client.GetByteArrayAsync(uri).GetAwaiter().GetResult());
            }
            catch
            {
                // Keep stale cache usable when GitHub or the local network is unavailable.
            }
        }
    }
}
