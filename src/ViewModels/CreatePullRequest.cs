using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Identity.Client;
using SourceGit.AzureDevOps;

namespace SourceGit.ViewModels
{
    public sealed class PullRequestReviewer : ObservableObject
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public string Detail { get; init; }
        public bool IsAvailable { get; init; } = true;
        public bool IsRequired { get => _required; set => SetProperty(ref _required, value); }
        private bool _required;
    }

    public sealed class CreatePullRequest : ObservableObject, IDisposable
    {
        public ObservableCollection<GitRef> Branches { get; } = [];
        public ObservableCollection<Identity> SearchResults { get; } = [];
        public ObservableCollection<PullRequestReviewer> Reviewers { get; } = [];
        public string SourceBranch { get; }
        public string RepositoryLabel { get => _repositoryLabel; private set => SetProperty(ref _repositoryLabel, value); }
        public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }
        public string Tenant { get => _tenant; set => SetProperty(ref _tenant, value); }
        public bool ConnectionExpanded { get => _connectionExpanded; set => SetProperty(ref _connectionExpanded, value); }
        public string AccountName { get => _accountName; private set => SetProperty(ref _accountName, value); }
        public bool SessionOnly { get => _sessionOnly; private set => SetProperty(ref _sessionOnly, value); }
        public bool IsConnected { get => _connected; private set { SetProperty(ref _connected, value); NotifyState(); } }
        public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); NotifyState(); } }
        public bool CanEdit => IsConnected && !IsBusy && !IsFinished && _store?.Pending == null;
        public bool IsFinished { get => _finished; private set { SetProperty(ref _finished, value); NotifyState(); } }
        public string Status { get => _status; private set => SetProperty(ref _status, value); }
        public string Error { get => _error; private set => SetProperty(ref _error, value); }
        public bool HasResultLink => _result?.PullRequest?.PullRequestId > 0;
        public bool HasPending => _store?.Pending != null;
        public bool HasUnknown => _store?.Pending is { PullRequestId: 0 };
        public bool RecoveryAcknowledged { get => _recoveryAcknowledged; set { SetProperty(ref _recoveryAcknowledged, value); NotifyState(); } }
        public bool CanClearRecovery => !IsBusy && HasPending && RecoveryAcknowledged;
        public string PrimaryLabel => HasPending ? App.Text(_store.Pending.PullRequestId > 0 ? "CreatePR.RetrySetup" : "CreatePR.CheckCreation") : App.Text(IsDraft ? "CreatePR.CreateDraft" : "CreatePR.Create");
        public bool CanCreate => IsConnected && !IsBusy && !IsFinished && (HasPending || (_sourceValid && Target != null && !string.IsNullOrWhiteSpace(Title) && Reviewers.All(x => x.IsAvailable)));
        public bool AutoCompleteAvailable => !IsDraft;
        public bool IsSquash => MergeStrategy == 0;
        public string[] MergeStrategies { get; } = [App.Text("CreatePR.Squash"), App.Text("CreatePR.Rebase"), App.Text("CreatePR.Merge")];
        public string MergeHelp => App.Text("CreatePR.Help." + (MergeStrategy == 0 ? "Squash" : MergeStrategy == 1 ? "Rebase" : "Merge"));
        public string AutoCompleteHelp => App.Text(IsDraft ? "CreatePR.Help.Draft" : AutoComplete ? "CreatePR.Help.Auto" : "CreatePR.Help.Manual");
        public GitRef Target { get => _target; set { SetProperty(ref _target, value); NotifyState(); } }
        public Identity SelectedSearchResult { get => _selectedSearchResult; set => SetProperty(ref _selectedSearchResult, value); }
        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value) && !_messageEdited)
                {
                    _mergeMessage = value;
                    OnPropertyChanged(nameof(MergeMessage));
                }
                NotifyState();
            }
        }
        public string Description { get => _description; set => SetProperty(ref _description, value); }
        public bool IsDraft { get => _draft; set { SetProperty(ref _draft, value); OnPropertyChanged(nameof(AutoCompleteAvailable)); OnPropertyChanged(nameof(AutoCompleteHelp)); NotifyState(); } }
        public bool AutoComplete { get => _auto; set { SetProperty(ref _auto, value); OnPropertyChanged(nameof(AutoCompleteHelp)); } }
        public bool DeleteSourceBranch { get => _delete; set => SetProperty(ref _delete, value); }
        public int MergeStrategy { get => _mergeStrategy; set { SetProperty(ref _mergeStrategy, value); OnPropertyChanged(nameof(IsSquash)); OnPropertyChanged(nameof(MergeHelp)); } }
        public string MergeMessage { get => _mergeMessage; set { _messageEdited = true; SetProperty(ref _mergeMessage, value); } }
        public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) _ = SearchAsync(value); } }

        public CreatePullRequest(Repository repository, Models.Remote remote, Models.Branch branch, Models.Branch localBranch = null,
            HttpClient http = null, Func<ConnectionSettings, string, Task<IAuthentication>> authenticationFactory = null)
        {
            _repo = repository;
            _remote = remote;
            _localBranch = localBranch;
            SourceBranch = branch.Name;
            _sourceRef = "refs/heads/" + branch.Name;
            _title = branch.Name;
            _mergeMessage = _title;
            RemoteAddress.TryParse(remote.URL, out _address);
            RepositoryLabel = _address == null ? remote.Name : $"{_address.Organization} / {_address.Project} / {_address.Repository}";
            var settings = Preferences.Instance.AzureDevOpsConnection ?? new ConnectionSettings();
            _clientId = settings.ClientId;
            _tenant = settings.Tenant;
            _connectionExpanded = string.IsNullOrWhiteSpace(_clientId);
            _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            _authenticationFactory = authenticationFactory ?? (async (settings, directory) => await Authentication.CreateAsync(settings, directory));
            Status = App.Text("CreatePR.ConnectPrompt");
        }

        public Task InitializeAsync() => string.IsNullOrWhiteSpace(ClientId) ? Task.CompletedTask : ConnectAsync(false);
        public Task SignInAsync() => ConnectAsync(true);

        private Task ConnectAsync(bool interactive) => RunAsync(async () =>
        {
            IsConnected = false;
            _sourceValid = false;
            _result = null;
            IsFinished = false;
            _store = null;
            Reviewers.Clear();
            Branches.Clear();
            SearchResults.Clear();
            _searchCancellation?.Cancel();
            if (_address == null)
                throw new ServiceException("UnsupportedRemote");
            if (!string.IsNullOrEmpty(_remote.PushURL) && (!RemoteAddress.TryParse(_remote.PushURL, out var push) || push.Key != _address.Key))
                throw new ServiceException("AmbiguousRemote");

            var settings = new ConnectionSettings { ClientId = ClientId?.Trim(), Tenant = Tenant?.Trim(), AccountId = Preferences.Instance.AzureDevOpsConnection?.AccountId };
            _auth?.Dispose();
            _auth = await _authenticationFactory(settings, Native.OS.BasicDirectories.ConfigDir);
            if (interactive)
                await _auth.SignInAsync(_lifetime.Token);
            await _auth.GetTokenAsync(_lifetime.Token);
            settings.AccountId = _auth.AccountId;
            Preferences.Instance.AzureDevOpsConnection = settings;
            Preferences.Instance.Save();
            AccountName = _auth.UserName;
            SessionOnly = _auth.SessionOnly;
            _client = new Client(_http, _address, _auth.GetTokenAsync);
            var info = await _client.ResolveAsync(_lifetime.Token);
            RepositoryLabel = $"{_address.Organization} / {info.Project.Name} / {info.Name}";
            _preferenceKey = _client.PreferenceKey(_auth.AccountId);
            _store = new RepositoryCreationStore(_repo.Settings, _preferenceKey + "/" + _sourceRef);
            _service = new CreationService(_client, _store, ReadLocalTipAsync);
            foreach (var branch in (await _client.GetBranchesAsync(_lifetime.Token)).Where(x => x.Name != _sourceRef).OrderBy(x => x.BranchName, StringComparer.Ordinal))
                Branches.Add(branch);

            var defaults = _repo.Settings.AzureDevOpsPullRequests.GetValueOrDefault(_preferenceKey) ?? new PullRequestDefaults();
            var pending = _store.Pending;
            Target = Branches.FirstOrDefault(x => x.Name == (pending?.TargetRef ?? defaults.TargetRef));
            AutoComplete = pending is { IsDraft: false } ? pending.AutoComplete : defaults.AutoComplete;
            DeleteSourceBranch = pending?.Completion.DeleteSourceBranch ?? defaults.DeleteSourceBranch;
            MergeStrategy = Array.IndexOf(StrategyValues, pending?.Completion.MergeStrategy ?? defaults.MergeStrategy);
            if (MergeStrategy < 0)
                MergeStrategy = 0;
            var selectedReviewers = pending?.Reviewers ?? defaults.Reviewers;
            var restored = await _client.ResolveIdentitiesAsync(selectedReviewers.Select(x => x.Id), _lifetime.Token);
            foreach (var saved in selectedReviewers)
            {
                var identity = restored.FirstOrDefault(x => x.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase));
                Reviewers.Add(new PullRequestReviewer { Id = saved.Id, Name = identity?.Label ?? App.Text("CreatePR.UnavailableReviewer"), Detail = identity?.Detail, IsAvailable = identity != null, IsRequired = saved.IsRequired });
            }
            IsConnected = true;
            ConnectionExpanded = false;
            if (pending != null)
            {
                Target = Branches.FirstOrDefault(x => x.Name == pending.TargetRef);
                Title = pending.Title;
                IsDraft = pending.IsDraft;
                MergeMessage = pending.Completion.MergeCommitMessage;
                Status = App.Text("CreatePR.PendingNotice");
            }
            else
            {
                await _service.ValidateSourceAsync(_sourceRef, _lifetime.Token);
                _sourceValid = true;
                Status = App.Text("CreatePR.SourceReady");
                if (defaults.TargetRef.Length > 0 && Target == null)
                    Error = App.Text("CreatePR.Error.SavedTargetUnavailable");
            }
        });

        public Task RefreshAsync() => RunAsync(async () =>
        {
            if (_client == null)
                return;
            await _service.ValidateSourceAsync(_sourceRef, _lifetime.Token);
            _sourceValid = true;
            Status = App.Text("CreatePR.SourceReady");
        });

        public Task SignOutAsync() => RunAsync(async () =>
        {
            if (_auth != null)
                await _auth.SignOutAsync();
            IsConnected = false;
            AccountName = string.Empty;
            Reviewers.Clear();
            Branches.Clear();
            SearchResults.Clear();
            _store = null;
            _result = null;
            _searchCancellation?.Cancel();
            Preferences.Instance.AzureDevOpsConnection.AccountId = string.Empty;
            Preferences.Instance.Save();
            ConnectionExpanded = true;
            Status = App.Text("CreatePR.ConnectPrompt");
        });

        public void AddReviewer()
        {
            var identity = SelectedSearchResult;
            if (!CanEdit || identity == null || Reviewers.Any(x => x.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase)))
                return;
            Reviewers.Add(new PullRequestReviewer { Id = identity.Id, Name = identity.Label, Detail = identity.Detail });
            SelectedSearchResult = null;
            SearchText = string.Empty;
            NotifyState();
        }

        public void RemoveReviewer(PullRequestReviewer reviewer)
        {
            if (CanEdit)
                Reviewers.Remove(reviewer);
            NotifyState();
        }

        public Task SubmitAsync() => RunAsync(async () =>
        {
            if (!IsConnected || IsFinished)
                return;
            Status = App.Text(HasPending ? "CreatePR.Checking" : "CreatePR.Creating");
            if (HasPending)
                _result = await _service.ResumeAsync(_lifetime.Token);
            else
            {
                if (Target == null || string.IsNullOrWhiteSpace(Title) || Reviewers.Any(x => !x.IsAvailable))
                    throw new ServiceException("InvalidTarget");
                _result = await _service.CreateAsync(new CreateRequest
                {
                    SourceRefName = _sourceRef,
                    TargetRefName = Target.Name,
                    Title = Title.Trim(),
                    Description = Description,
                    IsDraft = IsDraft,
                    Reviewers = Reviewers.Select(x => new ReviewerSelection { Id = x.Id, IsRequired = x.IsRequired }).ToList(),
                }, new CompletionOptions
                {
                    MergeStrategy = StrategyValues[MergeStrategy],
                    DeleteSourceBranch = DeleteSourceBranch,
                    MergeCommitMessage = IsSquash ? MergeMessage : null,
                }, AutoComplete, _lifetime.Token);
            }

            Status = App.Text("CreatePR.Result." + _result.State);
            if (_result.PullRequest?.PullRequestId > 0)
                Status += $"  #{_result.PullRequest.PullRequestId}: {_result.PullRequest.Title}";
            Error = _result.ErrorCode == null ? string.Empty : App.Text("CreatePR.Error." + _result.ErrorCode);
            IsFinished = _result.State is CreationState.Created or CreationState.Existing;
            if (_result.State == CreationState.Created)
                Remember(_result.PullRequest);
        });

        private void Remember(PullRequest pr)
        {
            var previous = _repo.Settings.AzureDevOpsPullRequests.GetValueOrDefault(_preferenceKey);
            _repo.Settings.AzureDevOpsPullRequests[_preferenceKey] = new PullRequestDefaults
            {
                TargetRef = pr.TargetRefName,
                Reviewers = pr.Reviewers.Where(x => Reviewers.Any(y => string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase))).Select(x => new ReviewerSelection { Id = x.Id, IsRequired = x.IsRequired }).ToList(),
                AutoComplete = pr.IsDraft ? previous?.AutoComplete ?? false : pr.AutoCompleteSetBy != null || (pr.Status == "completed" && AutoComplete),
                MergeStrategy = pr.CompletionOptions.MergeStrategy,
                DeleteSourceBranch = pr.CompletionOptions.DeleteSourceBranch,
            };
            if (!_repo.Settings.TrySave())
                Error = App.Text("CreatePR.Error.PreferencesNotSaved");
        }

        public void OpenResult()
        {
            if (HasResultLink)
                Native.OS.OpenBrowser(_client.PullRequestUrl(_result.PullRequest.PullRequestId));
        }

        public void OpenRepository()
        {
            if (_address != null)
                Native.OS.OpenBrowser(_address.WebUrl + "/pullrequests");
        }

        public void ClearRecovery()
        {
            if (!CanClearRecovery)
                return;
            try
            {
                _store.Clear();
                RecoveryAcknowledged = false;
                _result = null;
                _sourceValid = false;
                Status = App.Text("CreatePR.RefreshRequired");
                NotifyState();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async Task SearchAsync(string text)
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _searchCancellation = cancellation;
            SearchResults.Clear();
            if (!CanEdit || string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
                return;
            try
            {
                await Task.Delay(350, cancellation.Token);
                var identities = await _client.SearchIdentitiesAsync(text.Trim(), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                foreach (var identity in identities)
                    SearchResults.Add(identity);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_disposed && !cancellation.IsCancellationRequested) ShowError(ex); }
        }

        private async Task<string> ReadLocalTipAsync(CancellationToken cancellationToken)
        {
            if (_localBranch == null)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            var tip = await new Commands.ReadPullRequestBranch(_repo.FullPath, _localBranch.FullName).GetTipAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return tip;
        }

        private async Task RunAsync(Func<Task> action)
        {
            if (IsBusy || _disposed)
                return;
            IsBusy = true;
            Error = string.Empty;
            try
            { await action(); }
            catch (OperationCanceledException) { if (!_disposed) Status = App.Text("CreatePR.Cancelled"); }
            catch (Exception ex) { if (!_disposed) ShowError(ex); }
            finally { IsBusy = false; NotifyState(); }
        }

        private void ShowError(Exception ex)
        {
            Error = App.Text("CreatePR.Error." + (ex is ServiceException service ? service.Code : ex is MsalException ? "Authentication" : "Connection"));
            if (ex is ServiceException { Code: "SourceChanged" or "Unpublished" or "InvalidSource" })
                _sourceValid = false;
            if (ex is ServiceException { Code: "SignInRequired" or "ConnectionSetup" })
                ConnectionExpanded = true;
            NotifyState();
        }

        private void NotifyState()
        {
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(HasUnknown));
            OnPropertyChanged(nameof(HasResultLink));
            OnPropertyChanged(nameof(PrimaryLabel));
            OnPropertyChanged(nameof(CanClearRecovery));
        }

        public void Dispose()
        {
            _disposed = true;
            _lifetime.Cancel();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _http.Dispose();
            _lifetime.Dispose();
            _auth?.Dispose();
        }

        private static readonly string[] StrategyValues = ["squash", "rebase", "noFastForward"];
        private readonly Repository _repo;
        private readonly Models.Remote _remote;
        private readonly Models.Branch _localBranch;
        private readonly RemoteAddress _address;
        private readonly string _sourceRef;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource _searchCancellation;
        private readonly Func<ConnectionSettings, string, Task<IAuthentication>> _authenticationFactory;
        private IAuthentication _auth;
        private Client _client;
        private RepositoryCreationStore _store;
        private CreationService _service;
        private CreationResult _result;
        private string _preferenceKey;
        private string _repositoryLabel;
        private string _clientId;
        private string _tenant;
        private string _accountName;
        private string _status;
        private string _error;
        private string _title;
        private string _description = string.Empty;
        private string _mergeMessage;
        private string _searchText = string.Empty;
        private GitRef _target;
        private Identity _selectedSearchResult;
        private bool _connectionExpanded;
        private bool _sessionOnly;
        private bool _connected;
        private bool _busy;
        private bool _finished;
        private bool _sourceValid;
        private bool _recoveryAcknowledged;
        private bool _messageEdited;
        private bool _draft;
        private bool _auto;
        private bool _delete;
        private bool _disposed;
        private int _mergeStrategy;
    }
}
