# Azure DevOps pull requests

SourceGit can create pull requests directly in Azure DevOps Services. Right-click a published local or remote branch in the sidebar and choose **Create Pull Request**. In the history graph, the action is available inside the branch-name submenu. Other hosting providers retain their existing browser workflow.

A local branch does not need an upstream setting when exactly one supported remote branch has the same name and commit. When several remotes match, choose the intended branch under **Remotes** explicitly. SourceGit never changes tracking settings to create a PR.

Push the source branch through the ordinary Git workflow first. SourceGit checks the server ref before creation and verifies a corresponding local branch against it. The dialog does not push, commit, rebase, or configure tracking. Uncommitted changes are not included.

## Connect an account

This integration uses Microsoft Entra work or school accounts and a public-client application registration. No organization, tenant, application ID, or reviewer is built into SourceGit. A maintainer or administrator needs to provide an application registration that the account is allowed to use.

1. Register an application in Microsoft Entra ID with the supported organizational account types required for your deployment.
2. Add the **Mobile and desktop applications** platform with the `http://localhost` redirect URI used by the system browser.
3. Configure delegated Azure DevOps access (`user_impersonation`) and obtain consent as required by the tenant. SourceGit requests `https://app.vssps.visualstudio.com/.default` through MSAL.
4. Open **Azure DevOps connection** in the dialog. Enter the public application/client ID and the tenant ID. Use `organizations` only with a registration configured for multiple organizational tenants.
5. Choose **Sign in with Microsoft**. Use an account with access to the repository and permission to create pull requests. A client secret is neither required nor stored.

Connection settings are saved in the local SourceGit preferences. Tokens are stored separately using MSAL's protected desktop cache. When secure storage is unavailable, a visible message explains that sign-in is limited to the current session. Signing out removes the cached accounts for this connection. Existing Git credentials are not copied into the REST integration.

See Microsoft's [Entra integration guidance](https://learn.microsoft.com/en-us/azure/devops/integrate/get-started/authentication/entra-oauth) and [desktop token-cache documentation](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization?tabs=desktop).

### Browser sign-in succeeds but repository loading fails

Microsoft authentication and Azure DevOps authorization are separate steps. The dialog retains the signed-in account, shows the HTTP status and Azure DevOps error, and offers **Retry loading** without reopening the browser.

If Azure DevOps reports a different organization tenant, use the tenant ID shown in the error. A single-tenant app must be registered in that directory. To reuse an app registered in another directory, it must support multiple organizational tenants and have the necessary consent in the target directory; enter the target directory's tenant ID in SourceGit. Changing an app to multi-tenant without changing the selected tenant does not resolve the mismatch. You can check the organization's existing directory under **Organization settings → Microsoft Entra ID**.

If the tenants already match, check that the signed-in account has access to the organization and repository. SourceGit does not change organizational access policies or grant permissions.

## Create a pull request

Select the target branch and review the title, description, and reviewers. The title starts from the source branch name. Add any number of users or groups and choose **Required** separately for each. The server's branch policies determine which approvals are necessary; SourceGit does not impose a particular issue tracker or review partner.

Completion options include squash, rebase and fast-forward, or a merge commit. Auto-complete waits for the required approvals and policies, and optional source-branch deletion happens only after a successful merge. The local source branch is retained. Drafts do not activate auto-complete. New repositories start with manual completion and source-branch retention, so automatic merging and deletion require an explicit initial choice.

After successful creation and verification, SourceGit remembers the target, selected reviewers and their Required flags, merge strategy, auto-complete preference, and branch-deletion preference. Defaults are isolated by Azure repository and account and stored in the local Git common directory's `sourcegit.settings` file. Deleted targets and unavailable reviewers require correction. A cancelled dialog does not replace the last successful defaults. Source, title, description, squash message, and draft state are fresh for each PR.

## Recover from failures

An existing active PR for the same source and target is opened rather than recreated. If creation succeeds but configuration fails, **Retry setup** uses the original PR ID and settings.

If a response is lost, SourceGit records the uncertain attempt locally and will not repeat its POST automatically. **Check creation** searches for a matching result without modifying it. Inspect the PR or repository in Azure DevOps. After acknowledging that inspection, **Clear the recovery record** discards only the local recovery information; it does not delete or change a PR. Refresh the source before a new attempt. A known PR whose configuration you prefer to complete in the browser can be handled the same way.

The server remains authoritative for permissions, branch policies, conflicts, and supported merge strategies. SourceGit never bypasses policies, casts review votes, or transitions work items. A source that changes during creation leaves configuration incomplete rather than enabling auto-complete for unverified changes.

## Supported scope

Modern Azure DevOps HTTPS and SSH URLs and legacy `visualstudio.com` HTTPS URLs are supported. Fetch and push URLs identifying different repositories are rejected with an explanation; select the intended remote explicitly. Azure DevOps Server/on-premises, personal Microsoft accounts, and cross-repository fork PRs are not supported by this dialog.

The integration uses Azure DevOps REST API 7.1 and source-generated JSON models. Authentication uses MSAL. Build and test it with the normal SourceGit .NET/Avalonia toolchain, including a Release NativeAOT publish. The regression checks in `tests/AzureDevOpsPullRequestChecks` use synthetic repositories, a fake HTTP server, and injected authentication. They do not send real PRs or notify reviewers.
