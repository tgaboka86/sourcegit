# Azure DevOps pull request checks

These executable regression checks cover remote parsing, published-source validation, sidebar and history menus (including published branches without an upstream and ambiguous remotes), request payloads, required and optional reviewers, draft/manual/automatic completion, duplicate detection, partial and uncertain creation outcomes, recovery persistence, account-scoped defaults, and native dialog behavior.

The HTTP handler and authentication provider are synthetic. The checks use a temporary local Git repository and temporary preferences, and make no live Azure DevOps requests. No user account or application registration is required.

Sign-in regression cases distinguish successful Microsoft authentication from a denied repository request, verify tenant-mismatch explanations and API redirect suppression, and ensure retrying repository loading does not start another interactive sign-in or perform any writes.

PAT regression cases cover Basic authentication, masked input, account identification, organization-isolated credential storage, reconnection, storage failures, sign-out failures, expiration/permission errors, and PR creation with reviewers and auto-complete. They verify that synthetic secrets are absent from ordinary preferences and repository settings. Credential storage is injected; tests do not access real saved PATs. `--render` also saves light and dark images of the PAT connection panel.

Commit-default checks cover single and multiple commits, complete messages across paginated comparisons, Unicode and multiline bodies, cancelled target changes, and preserving manual edits during delayed responses. Dialog checks verify the collapsed completion summary, first-use defaults, remembered overrides, and automatic source validation/retry without a separate refresh step.

Build into a separate output directory to avoid locking a running SourceGit installation:

```powershell
dotnet build tests/AzureDevOpsPullRequestChecks/AzureDevOpsPullRequestChecks.csproj -o ../sourcegit-checks
../sourcegit-checks/AzureDevOpsPullRequestChecks.exe --render
```

On other platforms run the produced executable or use `dotnet` with its DLL. The checks require a desktop environment for Avalonia. `--render` saves light and dark native dialog images beside the executable. Fixture contents are removed when the checks finish.

Also validate the ordinary Release build and a NativeAOT publish. A live sign-in and creation check requires a separately configured application registration or PAT and an explicitly designated disposable Azure DevOps repository; do not use production repositories for automated checks.
