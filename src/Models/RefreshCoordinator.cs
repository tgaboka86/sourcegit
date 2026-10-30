using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public enum RefreshKind
    {
        Branches,
        Worktrees,
        Tags,
        Commits,
        Submodules,
        WorkingCopy,
        Stashes,
        Configuration,
    }

    /// <summary>
    ///     Runs at most one refresh per category. Requests received during a refresh
    ///     invalidate its result and are combined into one subsequent refresh.
    /// </summary>
    public sealed class RefreshCoordinator : IDisposable
    {
        public sealed class Context
        {
            public CancellationToken CancellationToken { get; }
            public bool IsCurrent => _owner.IsCurrent(_state, _version);

            internal Context(RefreshCoordinator owner, State state, long version, CancellationToken cancellationToken)
            {
                _owner = owner;
                _state = state;
                _version = version;
                CancellationToken = cancellationToken;
            }

            private readonly RefreshCoordinator _owner;
            private readonly State _state;
            private readonly long _version;
        }

        internal sealed class State
        {
            public long Version;
            public bool Running;
            public Func<Context, Task> Refresh;
            public Task Task;
        }

        public RefreshCoordinator(Action<Exception> onError)
        {
            _onError = onError;
        }

        public void Request(RefreshKind kind, Func<Context, Task> refresh)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                if (!_states.TryGetValue(kind, out var state))
                {
                    state = new State();
                    _states.Add(kind, state);
                }

                state.Version++;
                state.Refresh = refresh;
                if (!state.Running)
                {
                    state.Running = true;
                    state.Task = Task.Run(() => RunAsync(state));
                }
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            // Git process termination callbacks may wait for the process to exit.
            // Mark the repository closed immediately, then run those callbacks off the UI thread.
            _ = CancelAsync();
        }

        private async Task CancelAsync()
        {
            try
            {
                await _cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        internal Task WhenIdleAsync()
        {
            lock (_syncRoot)
            {
                var tasks = new List<Task>();
                foreach (var state in _states.Values)
                    tasks.Add(state.Task);
                return Task.WhenAll(tasks);
            }
        }

        private bool IsCurrent(State state, long version)
        {
            lock (_syncRoot)
                return !_disposed && state.Version == version;
        }

        private async Task RunAsync(State state)
        {
            while (true)
            {
                Context context;
                CancellationTokenSource cancellation;
                Func<Context, Task> refresh;
                lock (_syncRoot)
                {
                    if (_disposed)
                        return;

                    cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                    context = new Context(this, state, state.Version, cancellation.Token);
                    refresh = state.Refresh;
                }

                using (cancellation)
                {
                    try
                    {
                        await refresh(context).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                    {
                        // Closing a repository cancels its active read-only commands.
                    }
                    catch (Exception ex)
                    {
                        if (context.IsCurrent)
                        {
                            try
                            {
                                _onError(ex);
                            }
                            catch (Exception notificationError)
                            {
                                Trace.TraceError(notificationError.ToString());
                            }
                        }
                    }
                }

                lock (_syncRoot)
                {
                    if (_disposed || context.IsCurrent)
                    {
                        state.Running = false;
                        return;
                    }
                }
            }
        }

        private readonly object _syncRoot = new();
        private readonly Dictionary<RefreshKind, State> _states = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Action<Exception> _onError;
        private bool _disposed;
    }
}
