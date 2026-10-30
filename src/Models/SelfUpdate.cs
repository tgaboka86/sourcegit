using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public class Version
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string Body { get; set; }

        [JsonPropertyName("assets")]
        public ReleaseAsset[] Assets { get; set; } = [];

        [JsonIgnore]
        public string ReleaseUrl => $"https://github.com/tib8/sourcegit/releases/tag/{Uri.EscapeDataString(TagName)}";

        [JsonIgnore]
        public ReleaseAsset WindowsPackage
        {
            get
            {
                if (!OperatingSystem.IsWindows())
                    return null;

                var runtime = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "win-x64",
                    Architecture.Arm64 => "win-arm64",
                    _ => null,
                };
                return runtime == null ? null : Array.Find(Assets ?? [], a => a.Name == $"sourcegit_{TagName?.TrimStart('v')}.{runtime}.zip");
            }
        }

        [JsonIgnore]
        public bool CanInstall => WindowsPackage?.HasValidDigest == true;

        [JsonIgnore]
        public System.Version CurrentVersion { get; }

        [JsonIgnore]
        public string CurrentVersionStr => $"v{CurrentVersion.ToString(3)}";

        [JsonIgnore]
        public bool IsNewVersion => CurrentVersion.CompareTo(ParseTag(TagName)) < 0;

        public static System.Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith('v') || !System.Version.TryParse(tag[1..], out var version))
                throw new FormatException("The release tag must have the form v2026.23.8.");

            // Assembly versions contain four components; release tags usually contain three.
            return new System.Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        }

        [JsonIgnore]
        public string ReleaseDateStr => DateTimeFormat.Format(PublishedAt, true);

        public Version()
        {
            var assembly = Assembly.GetExecutingAssembly().GetName();
            CurrentVersion = assembly.Version ?? new System.Version();
        }
    }

    public class ReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("digest")]
        public string Digest { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonIgnore]
        public bool HasValidDigest => Digest is { Length: 71 } && Digest.StartsWith("sha256:", StringComparison.Ordinal) &&
            Digest[7..].All(Uri.IsHexDigit);
    }

    public class AlreadyUpToDate;

    public class SelfUpdateFailed
    {
        public string Reason
        {
            get;
            private set;
        }

        public SelfUpdateFailed(Exception e)
        {
            if (e.InnerException is { } inner)
                Reason = inner.Message;
            else
                Reason = e.Message;
        }
    }
}
