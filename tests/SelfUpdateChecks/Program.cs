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

    var history = ReleaseHistory.BuildNotes([
        new ReleaseVersion { TagName = "v2026.23.8", Body = "Middle release" },
        new ReleaseVersion { TagName = "v2026.23.7", Body = "Already installed" },
        new ReleaseVersion { TagName = "v2026.23.9", Body = "Latest release" },
        new ReleaseVersion { TagName = "v2026.23.8", Body = "Duplicate release" },
        new ReleaseVersion { TagName = "v2026.23.10", Body = "Beyond target" },
        new ReleaseVersion { TagName = "v2026.23.8.1", Body = "Draft release", IsDraft = true },
        new ReleaseVersion { TagName = "v2026.23.8.2", Body = "Preview release", IsPrerelease = true },
        new ReleaseVersion { TagName = "nightly", Body = "Non-version release" },
    ], ReleaseVersion.ParseTag("v2026.23.7"), ReleaseVersion.ParseTag("v2026.23.9"));
    Check(history.StartsWith("## v2026.23.9") && history.IndexOf("Latest release") < history.IndexOf("Middle release"), "notes sorted newest first");
    Check(!history.Contains("Already installed") && !history.Contains("Duplicate release") && !history.Contains("Beyond target") &&
        !history.Contains("Draft release") && !history.Contains("Preview release") && !history.Contains("Non-version release"), "only distinct intervening stable releases included");

    using (var handler = new HistoryHandler())
    using (var client = new HttpClient(handler))
    {
        var paged = await ReleaseHistory.LoadAsync(client, new ReleaseVersion { TagName = "v65000.1.3", Body = "Authoritative latest notes" }, CancellationToken.None);
        Check(handler.Requests == 2, "follow GitHub pagination");
        Check(paged.Contains("Intermediate notes from page two") && paged.Contains("Authoritative latest notes") && !paged.Contains("Stale latest notes"), "combine pages and retain latest snapshot");
    }
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
    Console.WriteLine("PASS: release parsing, version comparison, cumulative notes, pagination, digest validation, tamper rejection, and cancellation");

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

internal sealed class HistoryHandler : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        if (Requests == 1)
        {
            if (request.RequestUri!.Query != "?per_page=100&page=1")
                throw new Exception("Unexpected history request");
            response.Content = new StringContent("""[{"tag_name":"v65000.1.3","body":"Stale latest notes"},{"tag_name":"v2020.1.0","body":"Older release"}]""");
            response.Headers.Add("Link", "<https://api.github.com/repos/tib8/sourcegit/releases?per_page=100&page=2>; rel=\"next\"");
        }
        else if (Requests == 2)
        {
            if (request.RequestUri!.Query != "?per_page=100&page=2")
                throw new Exception("Unexpected history page");
            response.Content = new StringContent("""[{"tag_name":"v65000.1.2","body":"Intermediate notes from page two"}]""");
        }
        else
            throw new Exception("Pagination did not terminate");
        return Task.FromResult(response);
    }
}
