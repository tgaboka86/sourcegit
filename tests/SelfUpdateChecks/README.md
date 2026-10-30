# Self-update checks

Run `dotnet run --project tests/SelfUpdateChecks` for release parsing, normalized
version comparison, cumulative release notes (ordering, filtering, deduplication and
GitHub pagination), SHA-256 verification, tampered/missing hash rejection, and cancellation.

On Windows, run `pwsh -File tests/SelfUpdateChecks/InstallerChecks.ps1` to exercise
the actual installer against isolated fixtures: replacement, user data preservation,
hash rejection, invalid package rejection, and rollback after a restart failure.
The test intercepts process launches; it never changes the running installation.

The updater checks public releases from tib8/sourcegit. In-app installation supports
packaged Windows x64/ARM64 builds with a matching ZIP and GitHub SHA-256 asset digest.
Other platforms and assets without a digest use the browser option. Direct installation
requires write access to the current installation directory; it does not request elevation.
After download verification, the user confirms installation and restart. The helper waits
for exit, rechecks the digest, backs up overwritten files, copies the new package, and
restarts SourceGit. Existing extra files (including portable data) are preserved.
On failure it restores replaced files, keeps the backup in the temporary update directory,
and opens an error log. A digest from GitHub verifies package integrity; it is not an
independent publisher signature.
