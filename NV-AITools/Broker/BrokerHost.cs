using NVAITools.Infrastructure;

namespace NVAITools.Broker;

sealed record BrokerStatus(string Text, int WorkspaceCount, bool IsError);

sealed class BrokerHost : IAsyncDisposable
{
    const int MaximumConcurrentWorkspaces = 4;

    readonly BrokerLog log;
    readonly CommandDispatcher dispatcher = new();
    readonly SemaphoreSlim globalCapacity = new(MaximumConcurrentWorkspaces);
    readonly SemaphoreSlim reloadGate = new(1, 1);
    readonly CancellationTokenSource shutdown = new();
    Dictionary<string, WorkspaceRuntime> runtimes = new(StringComparer.OrdinalIgnoreCase);
    int stopping;
    int watcherFailures;

    public BrokerHost(BrokerLog log) => this.log = log;

    public event Action<BrokerStatus>? StatusChanged;

    public async Task<bool> StartAsync()
    {
        log.Write("Broker starting.");
        try
        {
            BrokerConfiguration.EnsureDefaultExists(BrokerPaths.ConfigurationPath);
            await ReloadAsync();
            return true;
        }
        catch (Exception exception)
        {
            if (shutdown.IsCancellationRequested)
                return false;
            ReportError($"Configuration was not loaded: {exception.Message}");
            return false;
        }
    }

    public async Task ReloadAsync()
    {
        CancellationToken cancellationToken = shutdown.Token;
        await reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref stopping) != 0)
                throw new InvalidOperationException("Broker is stopping.");

            BrokerConfiguration configuration = BrokerConfiguration.Load(BrokerPaths.ConfigurationPath);
            await ValidateWorkspacesAsync(configuration.WorkspaceRoots, cancellationToken);

            var additions = new Dictionary<string, WorkspaceRuntime>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, WorkspaceRuntime> replacement;
            try
            {
                for (int index = 0; index < configuration.WorkspaceRoots.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string root = configuration.WorkspaceRoots[index];
                    if (runtimes.ContainsKey(root))
                        continue;

                    var runtime = new WorkspaceRuntime(
                        root,
                        globalCapacity,
                        dispatcher.ExecuteAsync,
                        log,
                        ReportError,
                        ReportWatcherFailure,
                        ReportWatcherRecovered);
                    additions.Add(root, runtime);
                    runtime.Prepare();
                }

                cancellationToken.ThrowIfCancellationRequested();
                replacement = new Dictionary<string, WorkspaceRuntime>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < configuration.WorkspaceRoots.Count; index++)
                {
                    string root = configuration.WorkspaceRoots[index];
                    if (runtimes.TryGetValue(root, out WorkspaceRuntime? existing))
                        replacement.Add(root, existing);
                    else
                        replacement.Add(root, additions[root]);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await DisposeAllAsync(additions.Values);
                throw;
            }

            Dictionary<string, WorkspaceRuntime> previous = runtimes;
            runtimes = replacement;
            foreach (WorkspaceRuntime runtime in additions.Values)
                runtime.Activate();

            var removals = new List<WorkspaceRuntime>();
            foreach ((string root, WorkspaceRuntime runtime) in previous)
            {
                if (!replacement.ContainsKey(root))
                    removals.Add(runtime);
            }
            await DisposeAllAsync(removals);

            PublishOperationalStatus();
            log.Write($"Configuration loaded with {replacement.Count} workspace(s).");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            log.Write($"Configuration reload failed: {exception.Message}");
            throw;
        }
        finally
        {
            reloadGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0)
            return;

        shutdown.Cancel();
        await reloadGate.WaitAsync();
        try
        {
            Dictionary<string, WorkspaceRuntime> active = runtimes;
            runtimes = new Dictionary<string, WorkspaceRuntime>(StringComparer.OrdinalIgnoreCase);
            await DisposeAllAsync(active.Values);
            log.Write("Broker stopped.");
        }
        finally
        {
            reloadGate.Release();
        }

        globalCapacity.Dispose();
        reloadGate.Dispose();
        shutdown.Dispose();
    }

    static async Task ValidateWorkspacesAsync(
        IReadOnlyList<string> workspaceRoots,
        CancellationToken cancellationToken)
    {
        var resolver = new WorkspaceResolver(new ProcessRunner());
        for (int index = 0; index < workspaceRoots.Count; index++)
        {
            string configured = workspaceRoots[index];
            string resolved = await resolver.ResolveAsync(configured, cancellationToken);
            if (!resolved.Equals(configured, StringComparison.OrdinalIgnoreCase))
                throw new ToolException(
                    $"Configured path is not a UVCS workspace root: {configured}. Resolved root: {resolved}",
                    ExitCodes.DependencyOrWorkspaceFailure);
        }
    }

    static async Task DisposeAllAsync(IEnumerable<WorkspaceRuntime> workspaces)
    {
        foreach (WorkspaceRuntime workspace in workspaces)
            await workspace.DisposeAsync();
    }

    void ReportError(string message)
    {
        log.Write(message);
        PublishStatus(new BrokerStatus("Attention required", runtimes.Count, true));
    }

    void ReportWatcherFailure(string message)
    {
        Interlocked.Increment(ref watcherFailures);
        ReportError(message);
    }

    void ReportWatcherRecovered()
    {
        int remaining = Interlocked.Decrement(ref watcherFailures);
        if (remaining == 0 && Volatile.Read(ref stopping) == 0)
            PublishOperationalStatus();
    }

    void PublishOperationalStatus()
    {
        Dictionary<string, WorkspaceRuntime> active = runtimes;
        bool hasWatcherFailure = Volatile.Read(ref watcherFailures) != 0;
        string state = active.Count == 0 ? "Not configured" : "Running";
        PublishStatus(new BrokerStatus(
            hasWatcherFailure ? "Attention required" : state,
            active.Count,
            hasWatcherFailure));
    }

    void PublishStatus(BrokerStatus status) => StatusChanged?.Invoke(status);
}
