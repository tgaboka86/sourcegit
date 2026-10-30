using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public static class ReleaseHistory
    {
        public static async Task<string> LoadAsync(HttpClient client, Version latest, CancellationToken cancellationToken)
        {
            var releases = new List<Version>();
            for (var page = 1; ; page++)
            {
                using var response = await client.GetAsync($"https://api.github.com/repos/tib8/sourcegit/releases?per_page=100&page={page}", cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var batch = JsonSerializer.Deserialize(json, JsonCodeGen.Default.VersionArray)
                    ?? throw new InvalidDataException("GitHub returned an empty release history.");
                releases.AddRange(batch);

                // Creation order is not necessarily version order. Read every page so
                // an older-looking release cannot hide an intermediate version.
                if (!response.Headers.TryGetValues("Link", out var links) || !links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal)))
                    break;
            }

            return BuildNotes(releases.Prepend(latest), latest.CurrentVersion, Version.ParseTag(latest.TagName));
        }

        public static string BuildNotes(IEnumerable<Version> releases, System.Version current, System.Version latest)
        {
            var versions = new SortedDictionary<System.Version, Version>();
            foreach (var release in releases)
            {
                if (release == null || release.IsDraft || release.IsPrerelease)
                    continue;

                System.Version version;
                try
                {
                    version = Version.ParseTag(release.TagName);
                }
                catch (FormatException)
                {
                    continue;
                }

                if (version > current && version <= latest)
                    versions.TryAdd(version, release);
            }

            var notes = new StringBuilder();
            foreach (var release in versions.Reverse().Select(pair => pair.Value))
            {
                if (notes.Length > 0)
                    notes.AppendLine().AppendLine("---").AppendLine();
                notes.Append("## ").Append(release.TagName);
                if (!string.IsNullOrWhiteSpace(release.Name) && release.Name != release.TagName && release.Name != release.TagName[1..])
                    notes.Append(" — ").Append(release.Name);
                if (release.PublishedAt != default)
                    notes.Append(" (").Append(release.PublishedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(')');
                notes.AppendLine().AppendLine().AppendLine(release.Body?.Trim() ?? string.Empty);
            }

            return notes.ToString().TrimEnd();
        }
    }
}
