using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Web;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SourceGit;
using SourceGit.AzureDevOps;
using SourceGit.ViewModels;

internal static class Program
{
    private static int _checks;
    private const string ReviewerId = "33333333-3333-3333-3333-333333333333";
    private const string Source = "refs/heads/feature/widgets";
    private const string Target = "refs/heads/main";
    private static readonly RemoteAddress Address = new("example-org", "sample-project", "sample-repo");

    [STAThread]
    private static void Main(string[] args)
    {
        CheckRemotes();
        Task.Run(CheckWorkflowAsync).GetAwaiter().GetResult();
        Task.Run(CheckPersonalAccessTokenAsync).GetAwaiter().GetResult();
        var fixture = Path.Combine(Path.GetTempPath(), "sourcegit-pr-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            SourceGit.Native.OS.BasicDirectories.ConfigDir = fixture;
            SourceGit.Native.OS.BasicDirectories.CacheDir = fixture;
            Task.Run(async () =>
            {
                using var auth = await Authentication.CreateAsync(new ConnectionSettings { ClientId = "55555555-5555-5555-5555-555555555555" }, fixture);
                Check(string.IsNullOrEmpty(auth.AccountId), "MSAL and protected cache initialize without an account");
                try { await auth.GetTokenAsync(CancellationToken.None); throw new Exception("Expected sign-in requirement"); }
                catch (ServiceException ex) { Check(ex.Code == "SignInRequired", "no implicit interactive sign-in"); }
            }).GetAwaiter().GetResult();
            App.BuildAvaloniaApp().SetupWithoutStarting();
            App.SetLocale("en_US");
            Check(Path.GetFullPath(SourceGit.Native.OS.BasicDirectories.ConfigDir) == Path.GetFullPath(fixture), "preferences stay inside the test fixture");
            Preferences.Instance.SetCanModify();
            var git = new ProcessStartInfo("git") { WorkingDirectory = fixture, CreateNoWindow = true };
            git.ArgumentList.Add("init");
            git.ArgumentList.Add("-b");
            git.ArgumentList.Add("main");
            using (var process = Process.Start(git)) process.WaitForExit();
            CheckBranchMenus(fixture);
            CheckSignInFailure(fixture);
            CheckDialog(fixture, args.Contains("--render"));
            CheckTokenDialog(fixture, args.Contains("--render"));
        }
        finally
        {
            if (!Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture escaped temporary directory");
            foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(fixture, true);
        }
        Console.WriteLine($"PASS: {_checks} Azure DevOps pull request checks");
    }

    private static void CheckRemotes()
    {
        foreach (var url in new[]
        {
            "https://dev.azure.com/example-org/sample-project/_git/sample-repo",
            "https://user@dev.azure.com/example-org/sample-project/_git/sample-repo",
            "https://example-org.visualstudio.com/sample-project/_git/sample-repo",
            "https://example-org.visualstudio.com/DefaultCollection/sample-project/_git/sample-repo",
            "git@ssh.dev.azure.com:v3/example-org/sample-project/sample-repo",
            "ssh://git@ssh.dev.azure.com/v3/example-org/sample-project/sample-repo",
        })
            Check(RemoteAddress.TryParse(url, out var parsed) && parsed == Address, "supported remote: " + url);
        foreach (var url in new[] { "https://dev.azure.com.evil.invalid/o/p/_git/r", "https://evilazure.com/o/p/_git/r", "http://dev.azure.com/o/p/_git/r", "https://dev.azure.com:8443/o/p/_git/r", "https://github.com/o/r", "https://dev.azure.com/o/p/_git/r?token=secret", "https://dev.azure.com/o/p/_git/a%2Fb" })
            Check(!RemoteAddress.TryParse(url, out _), "reject unsupported destination");
        Check(RemoteAddress.TryParse("https://dev.azure.com/o/My%20Project/_git/My%20Repo", out var escaped) && escaped.Project == "My Project" && escaped.RepositoryApi.Contains("My%20Repo"), "escape path components once");
    }

    private static void CheckBranchMenus(string fixture)
    {
        var configPath = Path.Combine(fixture, ".git", "config");
        var originalConfig = File.ReadAllText(configPath);
        var repo = new Repository(false, fixture, Path.Combine(fixture, ".git"));
        var local = new SourceGit.Models.Branch { IsLocal = true, IsCurrent = true, Name = "feature/widgets", FullName = Source, Head = new('a', 40) };
        var published = new SourceGit.Models.Branch { Name = local.Name, FullName = "refs/remotes/origin/feature/widgets", Remote = "origin", Head = local.Head };
        var remote = new SourceGit.Models.Remote { Name = "origin", URL = Address.WebUrl };
        repo.Branches.AddRange([local, published]);
        repo.Remotes.Add(remote);
        typeof(Repository).GetField("_currentBranch", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(repo, local);
        var tree = new SourceGit.Views.BranchTree();
        var history = new SourceGit.Views.Histories();

        MenuItem Item(SourceGit.Models.Branch branch) => SourceGit.Views.AzureDevOpsPullRequestMenu.Create(tree, repo, branch);
        static MenuItem FindItem(IEnumerable<object> items)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (item.Name == "CreateAzureDevOpsPullRequest") return item;
                var nested = FindItem(item.Items);
                if (nested != null) return nested;
            }
            return null;
        }

        Check(string.IsNullOrEmpty(local.Upstream) && Item(local) is { IsEnabled: true }, "published local branch without upstream enables Create PR");
        Check(Item(published) is { IsEnabled: true }, "remote context resolves the untracked local counterpart");
        var localMenuMethod = typeof(SourceGit.Views.BranchTree).GetMethod("CreateContextMenuForLocalBranch", BindingFlags.Instance | BindingFlags.NonPublic);
        var localMenu = (ContextMenu)localMenuMethod.Invoke(tree, [repo, local]);
        Check(FindItem(localMenu.Items) is { IsEnabled: true }, "sidebar local context exposes Create PR without upstream");
        Check(FindItem(tree.CreateContextMenuForRemoteBranch(repo, published).Items) is { IsEnabled: true }, "sidebar remote context exposes Create PR");

        foreach (var method in new[] { "FillCurrentBranchMenu", "FillOtherLocalBranchMenu", "FillRemoteBranchMenu" })
        {
            var menu = new ContextMenu();
            object[] arguments = method == "FillCurrentBranchMenu" ? [menu, repo, local] : [menu, repo, method == "FillRemoteBranchMenu" ? published : local, local, false];
            typeof(SourceGit.Views.Histories).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(history, arguments);
            Check(FindItem(menu.Items) is { IsEnabled: true }, "history branch submenu exposes Create PR: " + method);
        }

        local.Head = new('b', 40);
        Check(Item(local) is { IsEnabled: false }, "local commits not yet published keep Create PR disabled");
        Check(Item(published) is { IsEnabled: false }, "remote context also checks an untracked local counterpart");
        local.Head = published.Head;
        published.Name = "different-name";
        Check(Item(local) is { IsEnabled: false }, "matching commit alone does not infer the destination");
        published.Name = local.Name;

        var secondRemote = new SourceGit.Models.Remote { Name = "mirror", URL = "https://dev.azure.com/another-org/sample-project/_git/sample-repo" };
        var secondBranch = new SourceGit.Models.Branch { Name = local.Name, FullName = "refs/remotes/mirror/feature/widgets", Remote = "mirror", Head = local.Head };
        repo.Remotes.Add(secondRemote);
        repo.Branches.Add(secondBranch);
        Check(Item(local) is { IsEnabled: false } ambiguous && Equals(ambiguous.Header, App.Text("CreatePR.SelectPublishedBranch")), "multiple matching remotes require explicit choice");
        Check(Item(published) is { IsEnabled: true }, "explicit remote branch resolves ambiguity");
        local.Upstream = published.FullName;
        Check(Item(local) is { IsEnabled: true }, "configured upstream takes precedence over other matches");
        local.IsDetachedHead = true;
        Check(Item(local) == null, "detached HEAD has no branch PR action");
        local.IsDetachedHead = false;
        remote.URL = "https://github.com/example/sample-repo.git";
        Check(Item(local) == null, "non-Azure upstream keeps its existing provider menu");
        Check(File.ReadAllText(configPath) == originalConfig, "resolving menus does not change Git tracking configuration");
    }

    private static CreateRequest Request(bool draft = false) => new()
    {
        SourceRefName = Source, TargetRefName = Target, Title = "Improve widget selection", Description = "Description with Unicode: á\n\nValidation details.", IsDraft = draft,
        Reviewers = [new() { Id = ReviewerId, IsRequired = true }],
    };

    private static async Task<(CreationService Service, MemoryStore Store, Client Client, HttpClient Http)> ServiceAsync(FakeServer server, string localTip = null)
    {
        var http = new HttpClient(server);
        var client = new Client(http, Address, _ => Task.FromResult(new AuthenticationHeaderValue("Bearer", "fixture-token")));
        await client.ResolveAsync(CancellationToken.None);
        var store = new MemoryStore();
        return (new(client, store, _ => Task.FromResult(localTip)), store, client, http);
    }

    private static async Task CheckWorkflowAsync()
    {
        var server = new FakeServer { IgnoreRequiredAtCreation = true };
        var context = await ServiceAsync(server, server.SourceTip);
        var result = await context.Service.CreateAsync(Request(), new() { MergeStrategy = "squash", DeleteSourceBranch = true }, true, CancellationToken.None);
        Check(result.State == CreationState.Created && context.Store.Pending == null, "successful creation clears recovery");
        Check(server.Posts == 1 && server.ReviewerWrites == 1 && server.Patches == 1, "verify required reviewer before completion");
        Check(server.Current.AutoCompleteSetBy != null && server.Current.CompletionOptions.DeleteSourceBranch, "completion options applied");
        Check(server.LastCreate.Description.Contains("á\n\n") && server.LastCreate.SourceRefName == Source, "full ref and multiline Unicode preserved");
        var duplicate = await context.Service.CreateAsync(Request(), new(), false, CancellationToken.None);
        Check(duplicate.State == CreationState.Existing && server.Posts == 1, "existing PR does not send another POST");
        context.Http.Dispose();

        server = new FakeServer();
        context = await ServiceAsync(server);
        var optionalRequest = Request();
        optionalRequest.Reviewers[0].IsRequired = false;
        result = await context.Service.CreateAsync(optionalRequest, new() { MergeStrategy = "noFastForward" }, false, CancellationToken.None);
        Check(result.State == CreationState.Created && server.Current.AutoCompleteSetBy == null && !server.Current.Reviewers[0].IsRequired, "optional reviewers and manual completion remain optional");
        context.Http.Dispose();

        server = new FakeServer();
        context = await ServiceAsync(server);
        var noReviewers = Request();
        noReviewers.Reviewers.Clear();
        result = await context.Service.CreateAsync(noReviewers, new(), false, CancellationToken.None);
        Check(result.State == CreationState.Created, "review requirements are governed by server policy");
        context.Http.Dispose();

        server = new FakeServer { FailPatchOnce = true };
        context = await ServiceAsync(server);
        result = await context.Service.CreateAsync(Request(), new(), true, CancellationToken.None);
        Check(result.State == CreationState.Incomplete && context.Store.Pending.PullRequestId == 17, "partial success retains identity");
        result = await context.Service.ResumeAsync(CancellationToken.None);
        Check(result.State == CreationState.Created && server.Posts == 1 && server.Patches == 2, "retry updates same PR only");
        context.Http.Dispose();

        server = new FakeServer { LoseCreateResponse = true };
        context = await ServiceAsync(server);
        result = await context.Service.CreateAsync(Request(), new(), true, CancellationToken.None);
        Check(result.State == CreationState.Unknown && context.Store.Pending != null, "lost POST response is uncertain");
        result = await context.Service.CreateAsync(Request(), new(), true, CancellationToken.None);
        Check(result.State == CreationState.Unknown && result.PullRequest?.PullRequestId == 17 && server.Posts == 1 && server.Patches == 0, "uncertain POST reconciles without writing candidate");
        context.Http.Dispose();

        server = new FakeServer { MoveSourceAfterCreate = true };
        context = await ServiceAsync(server);
        result = await context.Service.CreateAsync(Request(), new(), true, CancellationToken.None);
        Check(result.State == CreationState.Incomplete && server.Patches == 0, "moving source never enables auto-complete");
        context.Http.Dispose();

        server = new FakeServer();
        context = await ServiceAsync(server, new string('b', 40));
        await ExpectCodeAsync(() => context.Service.CreateAsync(Request(), new(), false, CancellationToken.None), "SourceChanged");
        Check(server.Posts == 0, "unpublished local commits do not create a PR");
        context.Http.Dispose();

        server = new FakeServer { SourceMissing = true };
        context = await ServiceAsync(server);
        await ExpectCodeAsync(() => context.Service.CreateAsync(Request(), new(), false, CancellationToken.None), "Unpublished");
        Check(server.Posts == 0, "missing remote source cannot create");
        context.Http.Dispose();

        server = new FakeServer { RejectCreate = true };
        context = await ServiceAsync(server);
        await ExpectCodeAsync(() => context.Service.CreateAsync(Request(), new(), false, CancellationToken.None), "ServerRejected");
        Check(context.Store.Pending == null && server.Posts == 1, "definitive rejection can be corrected");
        context.Http.Dispose();

        foreach (var strategy in new[] { "squash", "rebase", "noFastForward" })
        {
            server = new FakeServer();
            context = await ServiceAsync(server);
            result = await context.Service.CreateAsync(Request(true), new() { MergeStrategy = strategy }, true, CancellationToken.None);
            Check(result.State == CreationState.Created && server.Current.IsDraft && server.Current.AutoCompleteSetBy == null && server.Current.CompletionOptions.MergeStrategy == strategy, "draft never activates auto-complete: " + strategy);
            context.Http.Dispose();
        }

        server = new FakeServer();
        context = await ServiceAsync(server);
        context.Store.FailSave = true;
        await ExpectCodeAsync(() => context.Service.CreateAsync(Request(), new(), true, CancellationToken.None), "StorageFailed");
        Check(server.Posts == 0, "cannot dispatch POST without recovery record");
        context.Http.Dispose();
    }

    private static void CheckSignInFailure(string fixture)
    {
        var repo = new Repository(false, fixture, Path.Combine(fixture, ".git"));
        var server = new FakeServer { RepositoryError = HttpStatusCode.Unauthorized };
        var authentication = new FakeAuthentication();
        using var model = new CreatePullRequest(repo,
            new() { Name = "origin", URL = Address.WebUrl },
            new() { Name = "feature/widgets", Remote = "origin", FullName = "refs/remotes/origin/feature/widgets" },
            http: new HttpClient(server), authenticationFactory: (_, _) => Task.FromResult<IAuthentication>(authentication));
        model.ClientId = "55555555-5555-5555-5555-555555555555";
        var signIn = model.SignInAsync();
        PumpUntil(() => signIn.IsCompleted);
        signIn.GetAwaiter().GetResult();
        Check(model.IsSignedIn && model.HasAccount && !model.IsConnected && !model.IsBusy, "successful sign-in remains distinct from repository access failure");
        Check(model.Status == App.Text("CreatePR.RepositoryLoadFailed"), "repository failure does not keep the initial sign-in prompt");
        Check(model.Error.Contains("401") && model.Error.Contains("Example account cannot read this repository"), "show HTTP status and server explanation");
        Check(!model.ConnectionExpanded && model.RefreshLabel == App.Text("CreatePR.RetryConnection"), "signed-in user can retry repository loading");
        Check(server.SuppressedAuthRedirects, "API requests suppress federated HTML login redirects");
        server.ExpectedTenant = "77777777-7777-7777-7777-777777777777";
        var mismatch = model.RefreshAsync();
        PumpUntil(() => mismatch.IsCompleted);
        Check(model.Error.Contains(server.ExpectedTenant) && model.Error.Contains(authentication.TenantId) && model.Error.Contains("multi-tenant"), "tenant mismatch provides the expected directory without changing configuration");
        Check(model.Tenant == "organizations" && model.IsSignedIn, "server tenant hint does not automatically switch the sign-in authority");
        server.RepositoryError = null;
        var retry = model.RefreshAsync();
        PumpUntil(() => retry.IsCompleted);
        retry.GetAwaiter().GetResult();
        Check(model.IsConnected && model.IsSignedIn && authentication.SignIns == 1, "retry reuses sign-in without reopening the browser");
        Check(server.Posts == 0 && server.Patches == 0, "sign-in and connection retry do not mutate the repository");
    }

    private static void CheckDialog(string fixture, bool render)
    {
        var repo = new Repository(false, fixture, Path.Combine(fixture, ".git"));
        var remote = new SourceGit.Models.Remote { Name = "origin", URL = Address.WebUrl };
        var branch = new SourceGit.Models.Branch { Name = "feature/widgets", FullName = "refs/remotes/origin/feature/widgets", Remote = "origin" };
        var server = new FakeServer();
        var model = new CreatePullRequest(repo, remote, branch, http: new HttpClient(server), authenticationFactory: (_, _) => Task.FromResult<IAuthentication>(new FakeAuthentication()));
        model.ClientId = "55555555-5555-5555-5555-555555555555";
        var window = new SourceGit.Views.CreatePullRequest { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        PumpUntil(() => !model.IsBusy);
        Check(model.IsConnected, "dialog loads through injected connection: " + model.Error);
        Check(model.Target == null && !model.CanCreate, "first use requires explicit target");
        model.Target = model.Branches.Single(x => x.Name == Target);
        model.Title = "Improve widget selection";
        model.Description = "Use the selected widget consistently.\n\nVerified with sample data.";
        model.AutoComplete = true;
        model.DeleteSourceBranch = true;
        model.IsDraft = true;
        Check(!model.AutoCompleteAvailable && model.PrimaryLabel.Contains("draft"), "draft UI keeps auto-complete inactive");
        model.IsDraft = false;
        Check(model.MergeMessage == model.Title, "squash message follows fresh title");
        model.SelectedSearchResult = FakeServer.Reviewer();
        model.AddReviewer();
        model.Reviewers[0].IsRequired = true;
        Check(model.CanCreate, "valid published branch allows creation");
        Check(window.GetVisualDescendants().OfType<TextBox>().Any(x => x.AcceptsReturn), "native description accepts newlines");

        if (render)
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                App.Current.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "create-pr-" + theme.Key + ".png"));
            }
        }

        var submit = model.SubmitAsync();
        PumpUntil(() => submit.IsCompleted);
        submit.GetAwaiter().GetResult();
        Check(model.IsFinished && model.HasResultLink && string.IsNullOrEmpty(model.Error), "native dialog creates and verifies PR: " + model.Error);
        window.Close();

        var restored = new CreatePullRequest(repo, remote, branch, http: new HttpClient(server), authenticationFactory: (_, _) => Task.FromResult<IAuthentication>(new FakeAuthentication()));
        var initialize = restored.InitializeAsync();
        PumpUntil(() => initialize.IsCompleted);
        Check(restored.Target?.Name == Target && restored.AutoComplete && restored.DeleteSourceBranch && restored.Reviewers.Single().IsRequired, "restore last successful options");
        Check(!restored.IsDraft && restored.Title == branch.Name && restored.Description.Length == 0, "PR-specific fields are fresh");
        restored.DeleteSourceBranch = false;
        restored.Dispose();
        var saved = repo.Settings.AzureDevOpsPullRequests.Values.Single();
        Check(saved.DeleteSourceBranch, "cancelled edits do not replace defaults");
        var restart = Path.Combine(fixture, "restart");
        Directory.CreateDirectory(restart);
        File.Copy(Path.Combine(fixture, ".git", "sourcegit.settings"), Path.Combine(restart, "sourcegit.settings"));
        Check(SourceGit.Models.RepositorySettings.Get(restart).AzureDevOpsPullRequests.Values.Single().DeleteSourceBranch, "defaults deserialize after restart");

        var anotherAccount = new CreatePullRequest(repo, remote, branch, http: new HttpClient(server), authenticationFactory: (_, _) => Task.FromResult<IAuthentication>(new FakeAuthentication("another-account")));
        var connect = anotherAccount.InitializeAsync();
        PumpUntil(() => connect.IsCompleted);
        Check(anotherAccount.IsConnected && anotherAccount.Target == null && anotherAccount.Reviewers.Count == 0 && !anotherAccount.AutoComplete, "defaults stay isolated between accounts");
        anotherAccount.Dispose();
    }

    private static async Task CheckPersonalAccessTokenAsync()
    {
        const string token = "synthetic-pat-for-checks";
        var keychain = new FakeKeychain();
        using (var auth = await PersonalAccessTokenAuthentication.CreateAsync(Address.Organization, " " + token + " ", keychain))
        {
            var header = await auth.GetAuthorizationAsync(CancellationToken.None);
            Check(header.Scheme == "Basic" && Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter)) == ":" + token, "PAT uses Basic with an empty username");
            await auth.RememberAsync();
            Check(keychain.Values.Count == 0, "unverified PAT is not persisted");
            auth.SetIdentity(FakeServer.Reviewer());
            await auth.RememberAsync();
            Check(!auth.SessionOnly && keychain.Values.Count == 1, "verified PAT stored separately in credential store");
            Check(auth.AccountId == "pat/" + ReviewerId, "PAT preferences keyed by authenticated account, not secret");
        }
        using (var restored = await PersonalAccessTokenAuthentication.CreateAsync(Address.Organization.ToUpperInvariant(), "", keychain))
        {
            Check(restored.HasCredentials && !restored.SessionOnly, "restore saved PAT for the same organization");
            keychain.FailErase = true;
            try { await restored.SignOutAsync(); throw new Exception("Expected removal failure"); }
            catch (ServiceException ex) { Check(ex.Code == "CredentialRemovalFailed" && restored.HasCredentials, "failed sign-out does not claim token removal"); }
            keychain.FailErase = false;
            await restored.SignOutAsync();
            Check(!restored.HasCredentials && keychain.Values.Count == 0, "sign-out clears credential store and memory");
        }
        using (var auth = await PersonalAccessTokenAuthentication.CreateAsync(Address.Organization, token, keychain))
        {
            auth.SetIdentity(FakeServer.Reviewer());
            keychain.FailStore = true;
            await auth.RememberAsync();
            Check(auth.SessionOnly && auth.HasCredentials && keychain.Values.Count == 0, "storage failure uses only session credentials");
            keychain.FailStore = false;
            await auth.RememberAsync();
        }
        using (var other = await PersonalAccessTokenAuthentication.CreateAsync("another-example-org", "", keychain))
        {
            Check(!other.HasCredentials, "PAT is not shared across organizations");
            try { await other.GetAuthorizationAsync(CancellationToken.None); throw new Exception("Expected missing token"); }
            catch (ServiceException ex) { Check(ex.Code == "PersonalAccessTokenRequired", "missing PAT asks for token without Microsoft login"); }
        }
        using (var invalid = await PersonalAccessTokenAuthentication.CreateAsync(Address.Organization, "invalid\r\ntoken", null))
        {
            try { await invalid.GetAuthorizationAsync(CancellationToken.None); throw new Exception("Expected malformed token"); }
            catch (ServiceException ex) { Check(ex.Code == "PersonalAccessTokenRequired", "reject token with embedded whitespace"); }
        }
    }

    private static void CheckTokenDialog(string fixture, bool render)
    {
        const string token = "synthetic-pat-for-checks";
        var previous = Preferences.Instance.AzureDevOpsConnection;
        Preferences.Instance.AzureDevOpsConnection = new ConnectionSettings { UsePersonalAccessToken = true };
        var repo = new Repository(false, fixture, Path.Combine(fixture, ".git"));
        var remote = new SourceGit.Models.Remote { Name = "origin", URL = Address.WebUrl };
        var branch = new SourceGit.Models.Branch { Name = "feature/widgets", FullName = "refs/remotes/origin/feature/widgets", Remote = "origin" };
        var keychain = new FakeKeychain();
        var server = new FakeServer { ExpectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)) };
        CreatePullRequest CreateModel() => new(repo, remote, branch, http: new HttpClient(server, false),
            authenticationFactory: (_, _) => throw new Exception("PAT must not initialize MSAL"),
            personalAccessTokenFactory: (org, value) => PersonalAccessTokenAuthentication.CreateAsync(org, value, keychain));
        using var model = CreateModel();
        model.PersonalAccessToken = token;
        var window = new SourceGit.Views.CreatePullRequest { DataContext = model, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        PumpUntil(() => !model.IsBusy);
        Check(model.IsConnected && model.IsSignedIn && model.AccountName == "Example reviewer" && !model.SessionOnly, "PAT connects without application ID or tenant setup: " + model.Error);
        Check(model.PersonalAccessToken.Length == 0, "PAT field cleared after connecting");
        Check(window.GetVisualDescendants().OfType<TextBox>().Any(x => x.PasswordChar != '\0'), "PAT input uses password masking");
        Check(server.Posts == 0 && server.Patches == 0, "PAT connection only performs reads");
        Check(!File.ReadAllText(Path.Combine(fixture, "preference.json")).Contains(token), "PAT is absent from ordinary preferences");
        if (render)
        {
            model.ConnectionExpanded = true;
            model.PersonalAccessToken = token;
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                App.Current.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "create-pr-token-" + theme.Key + ".png"));
            }
            model.PersonalAccessToken = string.Empty;
        }
        model.Target = model.Branches.Single(x => x.Name == Target);
        model.AutoComplete = true;
        model.SelectedSearchResult = FakeServer.Reviewer();
        model.AddReviewer();
        var submit = model.SubmitAsync();
        PumpUntil(() => submit.IsCompleted);
        Check(model.IsFinished && model.HasResultLink && string.IsNullOrEmpty(model.Error), "PAT creates PR, resolves reviewers, and verifies completion settings");
        Check(!File.ReadAllText(Path.Combine(fixture, ".git", "sourcegit.settings")).Contains(token), "PAT absent from repository defaults and recovery journal");
        using var restored = CreateModel();
        var initialize = restored.InitializeAsync();
        PumpUntil(() => initialize.IsCompleted);
        Check(restored.IsConnected && restored.Target?.Name == Target && restored.AutoComplete, "PAT reconnect restores account-specific PR defaults");
        server.AccountId = "66666666-6666-6666-6666-666666666666";
        using var other = CreateModel();
        initialize = other.InitializeAsync();
        PumpUntil(() => initialize.IsCompleted);
        Check(other.IsConnected && other.Target == null && other.Reviewers.Count == 0, "a different PAT account does not inherit defaults");
        server.RepositoryError = HttpStatusCode.Unauthorized;
        var retry = other.SignInAsync();
        PumpUntil(() => retry.IsCompleted);
        Check(!other.IsConnected && other.ConnectionExpanded && other.Error.Contains("expired") && !other.Error.Contains("tenant"), "PAT rejection explains token permissions without Entra setup advice");
        Check(!other.Error.Contains(token), "connection error does not expose the PAT");
        server.RepositoryError = null;
        keychain.FailStore = true;
        retry = other.RefreshAsync();
        PumpUntil(() => retry.IsCompleted);
        Check(other.IsConnected && other.SessionOnly, "repository retry reuses session PAT after secure-store failure");
        var signOut = other.SignOutAsync();
        PumpUntil(() => signOut.IsCompleted);
        Check(!other.IsConnected && !other.HasAccount && keychain.Values.Count == 0, "dialog sign-out forgets stored PAT");
        restored.AuthenticationMethod = 0;
        Check(!restored.IsConnected && !restored.HasAccount && restored.Branches.Count == 0, "switching login method invalidates old connection");
        window.Close();
        Preferences.Instance.AzureDevOpsConnection = previous;
    }

    private static async Task ExpectCodeAsync(Func<Task<CreationResult>> operation, string code)
    {
        try { await operation(); throw new Exception("Expected " + code); }
        catch (ServiceException ex) { Check(ex.Code == code, "expected service error: " + code); }
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        if (!predicate()) throw new Exception("Timed out waiting for UI");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    private sealed class MemoryStore : ICreationStore
    {
        public PendingCreation Pending { get; private set; }
        public bool FailSave;
        public void Save(PendingCreation pending) { if (FailSave) throw new ServiceException("StorageFailed"); Pending = pending; }
        public void Clear() { Pending = null; }
    }

    private sealed class FakeAuthentication(string accountId = "fixture-account") : IAuthentication
    {
        public int SignIns { get; private set; }
        public string AccountId => accountId;
        public string UserName => "reviewer@example.invalid";
        public string TenantId => "88888888-8888-8888-8888-888888888888";
        public bool SessionOnly => true;
        public bool HasCredentials => true;
        public Task SignInAsync(CancellationToken token) { SignIns++; return Task.CompletedTask; }
        public Task SignOutAsync() => Task.CompletedTask;
        public Task<AuthenticationHeaderValue> GetAuthorizationAsync(CancellationToken token) => Task.FromResult(new AuthenticationHeaderValue("Bearer", "fixture-token"));
        public void Dispose() { }
    }

    private sealed class FakeKeychain : SourceGit.Native.OS.IKeychain
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailStore, FailErase;
        public void Store(string host, string username, string password)
        {
            if (FailStore) throw new IOException("Synthetic credential-store failure");
            Values[host + "/" + username] = password;
        }
        public string Read(string host, string username) => Values.GetValueOrDefault(host + "/" + username);
        public void Erase(string host, string username)
        {
            if (FailErase) throw new IOException("Synthetic credential-store failure");
            Values.Remove(host + "/" + username);
        }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public string SourceTip = new('a', 40);
        public bool SourceMissing, IgnoreRequiredAtCreation, FailPatchOnce, LoseCreateResponse, MoveSourceAfterCreate, RejectCreate;
        public HttpStatusCode? RepositoryError;
        public string ExpectedTenant;
        public string ExpectedAuthorization = "Bearer fixture-token";
        public string AccountId = ReviewerId;
        public bool SuppressedAuthRedirects;
        public int Posts, Patches, ReviewerWrites;
        public PullRequest Current;
        public CreateRequest LastCreate;
        public static Identity Reviewer() => new() { Id = ReviewerId, ProviderDisplayName = "Example reviewer", DisplayName = "Example reviewer", UniqueName = "reviewer@example.invalid", IsActive = true };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Headers.Authorization?.ToString() == ExpectedAuthorization, "authenticated request");
            var path = request.RequestUri.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            SuppressedAuthRedirects = request.Headers.TryGetValues("X-TFS-FedAuthRedirect", out var fedAuth) && fedAuth.Single() == "Suppress" &&
                request.Headers.TryGetValues("X-VSS-ReauthenticationAction", out var reauthentication) && reauthentication.Single() == "Suppress";
            if (path.EndsWith("/connectionData"))
            {
                var identity = Reviewer();
                identity.Id = AccountId;
                return Json(new ConnectionData { AuthenticatedUser = identity }, ApiJsonContext.Default.ConnectionData);
            }
            if (path.EndsWith("/identities"))
                return Json(new ListResult<Identity> { Value = [Reviewer()] }, ApiJsonContext.Default.ListResultIdentity);
            if (path.EndsWith("/refs"))
            {
                var refs = new List<GitRef> { new() { Name = Target, ObjectId = new('c', 40) } };
                if (!SourceMissing) refs.Add(new() { Name = Source, ObjectId = SourceTip });
                return Json(new ListResult<GitRef> { Value = refs.Where(x => x.Name[5..].StartsWith(query["filter"], StringComparison.Ordinal)).ToList() }, ApiJsonContext.Default.ListResultGitRef);
            }
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                if (RejectCreate) return new(HttpStatusCode.BadRequest);
                LastCreate = JsonSerializer.Deserialize(await request.Content.ReadAsStringAsync(cancellationToken), ApiJsonContext.Default.CreateRequest);
                Current = new() { PullRequestId = 17, Title = LastCreate.Title, SourceRefName = LastCreate.SourceRefName, TargetRefName = LastCreate.TargetRefName, IsDraft = LastCreate.IsDraft, Status = "active", CreationDate = DateTimeOffset.UtcNow, CreatedBy = new() { Id = "44444444-4444-4444-4444-444444444444" }, LastMergeSourceCommit = new() { CommitId = SourceTip }, Reviewers = LastCreate.Reviewers.Select(x => new Identity { Id = x.Id, IsRequired = x.IsRequired && !IgnoreRequiredAtCreation }).ToList() };
                if (MoveSourceAfterCreate) SourceTip = new('d', 40);
                if (LoseCreateResponse) throw new HttpRequestException("Simulated lost response");
                return Json(Current, ApiJsonContext.Default.PullRequest);
            }
            if (request.Method == HttpMethod.Put)
            {
                ReviewerWrites++;
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                Check(!body.Contains("vote"), "reviewer update never casts a vote");
                var selection = JsonSerializer.Deserialize(body, ApiJsonContext.Default.ReviewerSelection);
                Current.Reviewers.RemoveAll(x => x.Id == selection.Id);
                Current.Reviewers.Add(new() { Id = selection.Id, IsRequired = selection.IsRequired });
                return new(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Patch)
            {
                Patches++;
                if (FailPatchOnce) { FailPatchOnce = false; return new(HttpStatusCode.Forbidden); }
                var update = JsonSerializer.Deserialize(await request.Content.ReadAsStringAsync(cancellationToken), ApiJsonContext.Default.UpdateRequest);
                Check(LastCreate.Reviewers.Where(x => x.IsRequired).All(x => Current.Reviewers.Any(y => x.Id == y.Id && y.IsRequired)), "required reviewer confirmed before merge setup");
                Check(!update.CompletionOptions.BypassPolicy && !update.CompletionOptions.TransitionWorkItems, "no policy bypass or work-item transition");
                Current.CompletionOptions = update.CompletionOptions;
                Current.AutoCompleteSetBy = update.AutoCompleteSetBy == null ? null : new() { Id = update.AutoCompleteSetBy.Id };
                return Json(Current, ApiJsonContext.Default.PullRequest);
            }
            if (path.EndsWith("/pullrequests"))
                return Json(new ListResult<PullRequest> { Value = Current == null ? [] : [Current] }, ApiJsonContext.Default.ListResultPullRequest);
            if (path.EndsWith("/pullrequests/17"))
                return Json(Current, ApiJsonContext.Default.PullRequest);
            if (RepositoryError is { } errorStatus)
            {
                var failure = new HttpResponseMessage(errorStatus) { Content = new StringContent("{\"message\":\"Example account cannot read this repository.\"}", Encoding.UTF8, "application/json") };
                if (ExpectedTenant != null) failure.Headers.TryAddWithoutValidation("X-VSS-ResourceTenant", ExpectedTenant);
                return failure;
            }
            return Json(new RepositoryInfo { Id = "22222222-2222-2222-2222-222222222222", Name = "sample-repo", Project = new() { Id = "11111111-1111-1111-1111-111111111111", Name = "sample-project" }, DefaultBranch = Target }, ApiJsonContext.Default.RepositoryInfo);
        }

        private static HttpResponseMessage Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, type), Encoding.UTF8, "application/json") };
    }
}
