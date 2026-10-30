using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    // Owns the temporary package until it is handed to the out-of-process installer.
    public sealed class UpdateInstaller : IDisposable
    {
        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SourceGit/1.0");
            return client;
        }

        public static async Task VerifyHashAsync(string archive, string digest, CancellationToken cancellationToken)
        {
            if (!new ReleaseAsset { Digest = digest }.HasValidDigest)
                throw new InvalidDataException("The release has no valid SHA-256 digest. Open the release in your browser instead.");

            await using var stream = File.OpenRead(archive);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(digest[7..])))
                throw new InvalidDataException("SHA-256 verification failed. The downloaded package will not be installed.");
        }

        public async Task PrepareAsync(ReleaseAsset asset, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows() || !string.Equals(Path.GetFileName(Environment.ProcessPath), "SourceGit.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("In-app installation requires a packaged Windows version of SourceGit.");

            if (asset?.HasValidDigest != true || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/tib8/sourcegit/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("The release has no verified Windows package from tib8/sourcegit.");

            _destination = Path.GetDirectoryName(Environment.ProcessPath)!;
            // Fail before downloading or exiting if this installation requires administrator access.
            using (new FileStream(Path.Combine(_destination, $".update-{Guid.NewGuid():N}"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            { }

            _directory = Path.Combine(Path.GetTempPath(), $"SourceGit-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _archive = Path.Combine(_directory, "package.zip");
            _digest = asset.Digest[7..];

            using var client = CreateClient();
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(_archive))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > asset.Size || total > 1024L * 1024 * 1024)
                        throw new InvalidDataException("The package exceeds the expected release size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report(total * 100.0 / Math.Max(asset.Size, 1));
                }

                if (total != asset.Size)
                    throw new InvalidDataException("The downloaded package is incomplete.");
            }

            await VerifyHashAsync(_archive, asset.Digest, cancellationToken);
            // Check the package before handing it off. Extraction also rejects traversal paths.
            using (var zip = ZipFile.OpenRead(_archive))
            {
                if (zip.GetEntry("SourceGit/SourceGit.exe") == null)
                    throw new InvalidDataException("The package does not contain SourceGit/SourceGit.exe.");
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith("SourceGit/", StringComparison.Ordinal) || name.Contains("../", StringComparison.Ordinal) ||
                        name.StartsWith("SourceGit/data/", StringComparison.OrdinalIgnoreCase) || name.Contains(':'))
                        throw new InvalidDataException("The package contains an unexpected installation path.");
                }
            }

            await using var script = Assembly.GetExecutingAssembly().GetManifestResourceStream("SourceGit.InstallUpdate.ps1")!;
            await using var scriptFile = File.Create(Path.Combine(_directory, "install.ps1"));
            await script.CopyToAsync(scriptFile, cancellationToken);
        }

        public async Task InstallAndRestartAsync()
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _directory,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(_directory, "install.ps1"), "-Destination", _destination, "-Archive", _archive,
                "-ExpectedHash", _digest, "-ParentId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start) ?? throw new IOException("Could not start the update installer.");
            // Keep the application open if PowerShell is blocked or fails to initialize.
            try
            {
                var ready = Path.Combine(_directory, "ready");
                var timeout = Stopwatch.StartNew();
                while (!File.Exists(ready))
                {
                    if (process.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(15))
                        throw new IOException("The update installer could not initialize. Open the release in your browser instead.");
                    await Task.Delay(100);
                }
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill();
                throw;
            }
            _handedOff = true;
        }

        public void Dispose()
        {
            if (!_handedOff && _directory != null)
            {
                try
                { Directory.Delete(_directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private string _directory;
        private string _archive;
        private string _destination;
        private string _digest;
        private bool _handedOff;
    }
}
