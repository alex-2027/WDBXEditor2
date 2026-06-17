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
        private static Dictionary<string, string> DbdNameLookup;
        private HttpClient client = new HttpClient();

        public DBDProvider()
        {
            if (!Directory.Exists(CachePath))
                Directory.CreateDirectory(CachePath);

            client.BaseAddress = BaseURI;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WDBXEditor2");
        }

        public Stream StreamForTableName(string tableName, string build = null)
        {
            string dbdName = ResolveDbdName(tableName);
            string cacheFile = Path.Combine(CachePath, dbdName);

            if (!File.Exists(cacheFile) || (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalHours > 24)
            {
                var bytes = client.GetByteArrayAsync(dbdName).Result;
                File.WriteAllBytes(cacheFile, bytes);

                return new MemoryStream(bytes);
            }
            else
                return new MemoryStream(File.ReadAllBytes(cacheFile));
        }

        private string ResolveDbdName(string tableName)
        {
            string requestedName = Path.GetFileNameWithoutExtension(tableName) + ".dbd";
            string cachedName = Directory
                .EnumerateFiles(CachePath, "*.dbd")
                .Select(Path.GetFileName)
                .FirstOrDefault(name => string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(cachedName))
                return cachedName;

            var manifest = GetDbdNameLookup();
            return manifest.TryGetValue(requestedName, out string resolvedName)
                ? resolvedName
                : requestedName;
        }

        private Dictionary<string, string> GetDbdNameLookup()
        {
            if (DbdNameLookup != null)
                return DbdNameLookup;

            if (!File.Exists(ManifestCachePath) || (DateTime.Now - File.GetLastWriteTime(ManifestCachePath)).TotalHours > 24)
            {
                var manifestBytes = client.GetByteArrayAsync(GitHubDefinitionsApi).Result;
                File.WriteAllBytes(ManifestCachePath, manifestBytes);
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
    }
}
