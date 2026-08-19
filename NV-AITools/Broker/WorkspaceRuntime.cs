using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using NVAITools.Cli;

namespace NVAITools.Broker;

sealed class WorkspaceRuntime : IAsyncDisposable
{
    static readonly TimeSpan ResultRetention = TimeSpan.FromHours(24);
    static readonly TimeSpan[] WatcherRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5)
    ];

    readonly WorkspaceQueue queue;
    readonly SemaphoreSlim globalCapacity;
    readonly Func<CommandRequest, CancellationToken, Task<CommandOutcome>> executeCommand;
    readonly BrokerLog log;
    readonly Action<string> reportError;
    readonly Action<string> reportWatcherFailure;
    readonly Action reportWatcherRecovered;
    readonly CancellationTokenSource stopping = new();
    readonly Channel<bool> drainSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    readonly Channel<ClaimedRequest> executionQueue = Channel.CreateUnbounded<ClaimedRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    readonly ConcurrentDictionary<string, ClaimedRequest> claimed = new(StringComparer.Ordinal);
    readonly object watcherGate = new();

    FileSystemWatcher? watcher;
    Task? intakeTask;
    Task? executionTask;
    int watcherRecoveryRequested;
    bool watcherFaulted;
    bool active;
    bool disposed;

    public WorkspaceRuntime(
        string workspaceRoot,
        SemaphoreSlim globalCapacity,
        Func<CommandRequest, CancellationToken, Task<CommandOutcome>> executeCommand,
        BrokerLog log,
        Action<string> reportError,
        Action<string> reportWatcherFailure,
        Action reportWatcherRecovered)
    {
        queue = WorkspaceQueue.CreateForBroker(workspaceRoot);
        this.globalCapacity = globalCapacity;
        this.executeCommand = executeCommand;
        this.log = log;
        this.reportError = reportError;
        this.reportWatcherFailure = reportWatcherFailure;
        this.reportWatcherRecovered = reportWatcherRecovered;
    }

    public string WorkspaceRoot => queue.WorkspaceRoot;

    public void Prepare()
    {
        RecoverInterruptedRequests();
        RemoveExpiredResults();
        watcher = CreateWatcher();
        watcher.EnableRaisingEvents = true;
    }

    public void Activate()
    {
        if (active)
            throw new InvalidOperationException("Workspace runtime is already active.");

        active = true;
        intakeTask = Task.Run(IntakeLoopAsync);
        executionTask = Task.Run(ExecutionLoopAsync);
        SignalDrain();
        log.Write($"Watching workspace '{WorkspaceRoot}'.");
    }

    public async ValueTask DisposeAsync()
    {
        FileSystemWatcher? currentWatcher;
        lock (watcherGate)
        {
            disposed = true;
            currentWatcher = watcher;
            watcher = null;
            if (watcherFaulted)
            {
                watcherFaulted = false;
                reportWatcherRecovered();
            }
        }
        if (currentWatcher is not null)
        {
            currentWatcher.Dispose();
        }

        stopping.Cancel();
        drainSignals.Writer.TryComplete();
        executionQueue.Writer.TryComplete();

        await AwaitWorkerAsync(intakeTask);
        await AwaitWorkerAsync(executionTask);

        foreach (ClaimedRequest request in claimed.Values)
            await PublishStoppedAsync(request);

        queue.Dispose();
        stopping.Dispose();
        log.Write($"Stopped workspace '{WorkspaceRoot}'.");
    }

    async Task IntakeLoopAsync()
    {
        try
        {
            await foreach (bool _ in drainSignals.Reader.ReadAllAsync(stopping.Token))
            {
                if (Interlocked.Exchange(ref watcherRecoveryRequested, 0) != 0)
                    await RecoverWatcherAsync();
                else
                    await DrainRequestsAsync();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure($"Intake failed for '{WorkspaceRoot}': {exception.Message}");
        }
    }

    async Task ExecutionLoopAsync()
    {
        try
        {
            await foreach (ClaimedRequest request in executionQueue.Reader.ReadAllAsync(stopping.Token))
                await ExecuteAsync(request);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure($"Execution lane failed for '{WorkspaceRoot}': {exception.Message}");
        }
    }

    async Task ExecuteAsync(ClaimedRequest request)
    {
        bool capacityHeld = false;
        try
        {
            await globalCapacity.WaitAsync(stopping.Token);
            capacityHeld = true;
            TimeSpan queueWait = Stopwatch.GetElapsedTime(request.ClaimedTimestamp);
            var execution = Stopwatch.StartNew();
            CommandOutcome outcome = await executeCommand(request.Command, stopping.Token);
            execution.Stop();

            await PublishResultAsync(request, outcome);
            log.Write(
                $"Completed request {request.Id} ({request.Tool}) in '{WorkspaceRoot}': " +
                $"queue={queueWait.TotalMilliseconds:0}ms execution={execution.ElapsedMilliseconds}ms exit={outcome.ExitCode}.");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await PublishStoppedAsync(request);
        }
        catch (Exception exception)
        {
            var outcome = new CommandOutcome(
                string.Empty,
                ExitCodes.InternalFailure,
                $"Broker could not complete request: {exception.Message}{Environment.NewLine}");
            await TryPublishFailureAsync(request, outcome, exception.Message);
        }
        finally
        {
            if (capacityHeld)
                globalCapacity.Release();
        }
    }

    async Task DrainRequestsAsync()
    {
        try
        {
            string[] requestPaths = Directory.GetFiles(queue.Requests, "*.request", SearchOption.TopDirectoryOnly);
            Array.Sort(requestPaths, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < requestPaths.Length && !stopping.IsCancellationRequested; index++)
                await ClaimAsync(requestPaths[index]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ToolException)
        {
            ReportFailure($"Could not drain requests for '{WorkspaceRoot}': {exception.Message}");
        }
    }

    async Task ClaimAsync(string requestPath)
    {
        string filename = Path.GetFileName(requestPath);
        string id = Path.GetFileNameWithoutExtension(filename);
        string processingPath = Path.Combine(queue.Processing, filename);

        try
        {
            File.Move(requestPath, processingPath);
        }
        catch (IOException)
        {
            return;
        }

        if (!QueueProtocol.IsRequestId(id))
        {
            File.Delete(processingPath);
            log.Write($"Discarded request with invalid filename '{filename}' in '{WorkspaceRoot}'.");
            return;
        }

        ClaimedRequest? claimedRequest = null;
        try
        {
            byte[] bytes = await ReadLimitedAsync(processingPath, QueueProtocol.MaximumRequestBytes);
            CommandRequest command = QueueProtocol.ParseRequest(bytes, id, WorkspaceRoot);
            claimedRequest = new ClaimedRequest(
                id,
                GetToolName(command),
                processingPath,
                command,
                Stopwatch.GetTimestamp());

            if (!claimed.TryAdd(id, claimedRequest))
                throw new ToolException("A request with this ID is already claimed.", ExitCodes.InvalidArguments);

            await executionQueue.Writer.WriteAsync(claimedRequest, stopping.Token);
            log.Write($"Claimed request {id} ({claimedRequest.Tool}) in '{WorkspaceRoot}'.");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            if (claimedRequest is not null)
                claimed.TryAdd(id, claimedRequest);
        }
        catch (Exception exception)
        {
            claimedRequest ??= new ClaimedRequest(
                id,
                "invalid",
                processingPath,
                new StatusRequest(WorkspaceRoot),
                Stopwatch.GetTimestamp());
            claimed.TryAdd(id, claimedRequest);
            var outcome = new CommandOutcome(
                string.Empty,
                exception is ToolException tool ? tool.ExitCode : ExitCodes.InternalFailure,
                $"Invalid broker request: {exception.Message}{Environment.NewLine}");
            await TryPublishFailureAsync(claimedRequest, outcome, exception.Message);
        }
    }

    async Task RecoverWatcherAsync()
    {
        FileSystemWatcher? failedWatcher;
        lock (watcherGate)
        {
            failedWatcher = watcher;
            watcher = null;
        }
        failedWatcher?.Dispose();
        await DrainRequestsAsync();

        int attempt = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                FileSystemWatcher replacement = CreateWatcher();
                try
                {
                    lock (watcherGate)
                    {
                        if (disposed)
                        {
                            replacement.Dispose();
                            return;
                        }
                        watcher = replacement;
                        replacement.EnableRaisingEvents = true;
                    }
                }
                catch
                {
                    lock (watcherGate)
                    {
                        if (ReferenceEquals(watcher, replacement))
                            watcher = null;
                    }
                    replacement.Dispose();
                    throw;
                }

                lock (watcherGate)
                {
                    if (disposed || !ReferenceEquals(watcher, replacement))
                        return;
                    if (Volatile.Read(ref watcherRecoveryRequested) != 0)
                        return;
                    if (watcherFaulted)
                    {
                        watcherFaulted = false;
                        reportWatcherRecovered();
                    }
                    log.Write($"Recovered watcher for '{WorkspaceRoot}'.");
                }
                await DrainRequestsAsync();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TimeSpan delay = WatcherRetryDelays[Math.Min(attempt++, WatcherRetryDelays.Length - 1)];
                log.Write(
                    $"Could not recreate watcher for '{WorkspaceRoot}'; retrying in {delay.TotalSeconds:0.##} seconds: {exception.Message}");
                await Task.Delay(delay, stopping.Token);
            }
        }
    }

    FileSystemWatcher CreateWatcher()
    {
        var created = new FileSystemWatcher(queue.Requests)
        {
            Filter = "*.request",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
        };
        created.Created += OnRequestChanged;
        created.Renamed += OnRequestChanged;
        created.Error += OnWatcherError;
        return created;
    }

    void OnRequestChanged(object sender, FileSystemEventArgs args) => SignalDrain();

    void OnWatcherError(object sender, ErrorEventArgs args)
    {
        string message = $"Watcher error for '{WorkspaceRoot}': {args.GetException().Message}";
        lock (watcherGate)
        {
            if (disposed || !ReferenceEquals(sender, watcher))
                return;
            if (!watcherFaulted)
            {
                watcherFaulted = true;
                reportWatcherFailure(message);
            }
            else
            {
                log.Write(message);
            }
            Interlocked.Exchange(ref watcherRecoveryRequested, 1);
        }
        SignalDrain();
    }

    void SignalDrain() => drainSignals.Writer.TryWrite(true);

    async Task PublishResultAsync(ClaimedRequest request, CommandOutcome outcome)
    {
        string completionPath = ResultPath(request.Id, "result");
        if (File.Exists(completionPath))
        {
            File.Delete(request.ProcessingPath);
            claimed.TryRemove(request.Id, out _);
            return;
        }

        DeletePartialResults(request.Id);
        await WriteAtomicAsync(ResultPath(request.Id, "stdout"), outcome.Output);
        await WriteAtomicAsync(ResultPath(request.Id, "stderr"), outcome.Diagnostics);
        await WriteAtomicAsync(
            completionPath,
            QueueProtocol.SerializeResult(request.Id, outcome.ExitCode));
        File.Delete(request.ProcessingPath);
        claimed.TryRemove(request.Id, out _);
    }

    async Task PublishStoppedAsync(ClaimedRequest request)
    {
        if (!claimed.ContainsKey(request.Id))
            return;

        var outcome = new CommandOutcome(
            string.Empty,
            ExitCodes.ExternalCommandFailure,
            $"Broker stopped before request {request.Id} completed.{Environment.NewLine}");
        await TryPublishFailureAsync(request, outcome, "broker stopped");
    }

    async Task TryPublishFailureAsync(
        ClaimedRequest request,
        CommandOutcome outcome,
        string reason)
    {
        try
        {
            await PublishResultAsync(request, outcome);
            log.Write($"Failed request {request.Id} ({request.Tool}) in '{WorkspaceRoot}': {reason}");
        }
        catch (Exception exception)
        {
            ReportFailure($"Could not publish failure for request {request.Id} in '{WorkspaceRoot}': {exception.Message}");
        }
    }

    void RecoverInterruptedRequests()
    {
        string[] processingPaths = Directory.GetFiles(queue.Processing, "*.request", SearchOption.TopDirectoryOnly);
        for (int index = 0; index < processingPaths.Length; index++)
        {
            string processingPath = processingPaths[index];
            string id = Path.GetFileNameWithoutExtension(processingPath);
            if (!QueueProtocol.IsRequestId(id))
            {
                File.Delete(processingPath);
                log.Write($"Removed invalid interrupted request '{processingPath}'.");
                continue;
            }

            if (File.Exists(ResultPath(id, "result")))
            {
                File.Delete(processingPath);
                log.Write($"Removed completed processing marker for request {id} in '{WorkspaceRoot}'.");
                continue;
            }

            DeletePartialResults(id);
            string requestPath = Path.Combine(queue.Requests, $"{id}.request");
            if (File.Exists(requestPath))
                File.Delete(requestPath);
            File.Move(processingPath, requestPath);
            log.Write($"Recovered interrupted request {id} in '{WorkspaceRoot}'.");
        }
    }

    void RemoveExpiredResults()
    {
        DateTime cutoff = DateTime.UtcNow - ResultRetention;
        string[] paths = Directory.GetFiles(queue.Results, "*", SearchOption.TopDirectoryOnly);
        for (int index = 0; index < paths.Length; index++)
        {
            string path = paths[index];
            string extension = Path.GetExtension(path);
            if (extension is not ".result" and not ".stdout" and not ".stderr" and not ".tmp")
                continue;
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
                continue;

            File.Delete(path);
            log.Write($"Removed expired result file '{path}'.");
        }
    }

    void DeletePartialResults(string id)
    {
        string[] suffixes = ["stdout", "stderr", "result"];
        for (int index = 0; index < suffixes.Length; index++)
        {
            File.Delete(ResultPath(id, suffixes[index]));
            File.Delete(ResultPath(id, $"{suffixes[index]}.tmp"));
        }
    }

    string ResultPath(string id, string suffix) => Path.Combine(queue.Results, $"{id}.{suffix}");

    static async Task WriteAtomicAsync(string finalPath, string contents) =>
        await WriteAtomicAsync(finalPath, System.Text.Encoding.UTF8.GetBytes(contents));

    static async Task WriteAtomicAsync(string finalPath, byte[] contents)
    {
        string temporaryPath = finalPath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(contents);
            await stream.FlushAsync();
            stream.Flush(true);
        }
        File.Move(temporaryPath, finalPath);
    }

    static async Task<byte[]> ReadLimitedAsync(string path, int maximumBytes)
    {
        byte[] buffer = new byte[maximumBytes + 1];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            true);
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
                return buffer[..total];
            total += read;
        }
        throw new ToolException(
            $"Request exceeded the {maximumBytes}-byte limit.",
            ExitCodes.InvalidArguments);
    }

    static string GetToolName(CommandRequest command) => command switch
    {
        StatusRequest => "status",
        PendingChangesDiffsRequest => "pending-changes-diffs",
        ChangesetDiffsRequest => "changeset-diffs",
        _ => "unknown"
    };

    void ReportFailure(string message)
    {
        reportError(message);
    }

    static async Task AwaitWorkerAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    sealed record ClaimedRequest(
        string Id,
        string Tool,
        string ProcessingPath,
        CommandRequest Command,
        long ClaimedTimestamp);
}
