using System.Security.Cryptography;
using System.Text.Json;
using SourceGit.Models;
using ReleaseVersion = SourceGit.Models.Version;

var fixture = Path.Combine(Path.GetTempPath(), "SourceGit-update-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var release = JsonSerializer.Deserialize<ReleaseVersion>("""
        {"name":"Update","tag_name":"v2026.23.8","published_at":"2026-10-30T12:00:00Z","body":"Notes",
         "assets":[{"name":"sourcegit_2026.23.8.win-x64.zip","browser_download_url":"https://github.com/tib8/sourcegit/releases/download/v2026.23.8/package.zip",
                    "size":42,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
        """)!;
    Check(release.Assets.Length == 1 && release.Assets[0].HasValidDigest, "GitHub release fields and digest");
    Check(release.ReleaseUrl == "https://github.com/tib8/sourcegit/releases/tag/v2026.23.8", "fork release link");
    Check(ReleaseVersion.ParseTag("v2026.23.7").Equals(new System.Version(2026, 23, 7, 0)), "normalize assembly version components");
    Check(!new ReleaseVersion { TagName = "v" + release.CurrentVersion.ToString(3) }.IsNewVersion, "same version is up to date");
    Check(new ReleaseVersion { TagName = "v65000.1.1" }.IsNewVersion, "newer version is detected");
    using (var script = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("SourceGit.InstallUpdate.ps1"))
        Check(script != null && script.Length > 0, "installer embedded in app assembly");
    foreach (var tag in new[] { "", "2026.23.8", "v2026.23.8-preview", "vbad" })
    {
        try { ReleaseVersion.ParseTag(tag); throw new Exception("Invalid tag accepted: " + tag); }
        catch (FormatException) { }
    }
    foreach (var digest in new[] { "", "sha256:abc", "md5:" + new string('a', 64), "sha256:" + new string('z', 64) })
        Check(!new ReleaseAsset { Digest = digest }.HasValidDigest, "reject malformed digest");

    var archive = Path.Combine(fixture, "download.zip");
    await File.WriteAllTextAsync(archive, "release package");
    var correct = "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive)));
    await UpdateInstaller.VerifyHashAsync(archive, correct, CancellationToken.None);
    await File.AppendAllTextAsync(archive, "tampered");
    await RejectDigest(correct);
    await RejectDigest(null);
    await RejectDigest("sha256:" + new string('0', 64));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await UpdateInstaller.VerifyHashAsync(archive, correct, cancellation.Token);
        throw new Exception("Cancellation ignored");
    }
    catch (OperationCanceledException) { }
    Console.WriteLine("PASS: release parsing, version comparison, digest validation, tamper rejection, and cancellation");

    async Task RejectDigest(string digest)
    {
        try
        {
            await UpdateInstaller.VerifyHashAsync(archive, digest, CancellationToken.None);
            throw new Exception("Unsafe package accepted");
        }
        catch (InvalidDataException) { }
    }
}
finally
{
    Directory.Delete(fixture, true);
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
