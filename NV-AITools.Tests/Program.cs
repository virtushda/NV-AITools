using System.Security.Cryptography;
using System.Text;
using NVAITools;
using NVAITools.Broker;
using NVAITools.Cli;
using NVAITools.Commands;
using NVAITools.Infrastructure;

static class Program
{
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    static async Task<int> Main()
    {
        string runRoot = CreateRunRoot();
        try
        {
            await RunAsync("configuration and protocol", () => TestConfigurationAndProtocolAsync(runRoot));
            await RunAsync("pending filename filters", () => TestPendingFileNameFiltersAsync(runRoot));
            await RunAsync("pre-canceled process", () => TestPreCanceledProcessAsync(runRoot));
            await RunAsync("incremental process output limits", () => TestIncrementalProcessOutputLimitsAsync(runRoot));
            await RunAsync("workspace read boundary", () => TestWorkspaceReadBoundaryAsync(runRoot));
            await RunAsync("workspace queue boundary", () => TestWorkspaceQueueBoundaryAsync(runRoot));
            await RunAsync("ordered parallel pipeline", TestOrderedParallelPipelineAsync);
            await RunAsync("pipeline backpressure and termination", TestPipelineBackpressureAndTerminationAsync);
            await RunAsync("serialized UVCS runner", TestUvcsRunnerAsync);
            await RunAsync("changeset metadata and batches", () => TestChangesetMetadataAndBatchesAsync(runRoot));
            await RunAsync("changeset batch command", () => TestChangesetBatchCommandAsync(runRoot));
            await RunAsync("ordered batch pipeline", TestOrderedBatchPipelineAsync);
            await RunAsync("per-file Git equivalence", () => TestPerFileGitEquivalenceAsync(runRoot));
            await RunAsync("client queue round trip", () => TestClientRoundTripAsync(runRoot));
            await RunAsync("workspace concurrency", () => TestWorkspaceConcurrencyAsync(runRoot));
            await RunAsync("crash recovery", () => TestCrashRecoveryAsync(runRoot));
            await RunAsync("shutdown completion", () => TestShutdownCompletionAsync(runRoot));
            Console.WriteLine("All NV-AITools integration tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (Directory.Exists(runRoot))
                Directory.Delete(runRoot, true);
        }
    }

    static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        Console.WriteLine($"PASS: {name}");
    }

    static Task TestConfigurationAndProtocolAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "configuration", "workspace")).FullName;
        string configPath = Path.Combine(runRoot, "configuration", "config.ini");
        File.WriteAllText(configPath, "[Workspaces]\r\n");
        Assert(BrokerConfiguration.Load(configPath).WorkspaceRoots.Count == 0, "Empty configuration should be valid.");

        File.WriteAllText(configPath, $"[Workspaces]\r\nPath1={root}\r\n");
        BrokerConfiguration configuration = BrokerConfiguration.Load(configPath);
        Assert(configuration.WorkspaceRoots.Count == 1, "Configured workspace was not loaded.");

        File.WriteAllText(configPath, $"[Workspaces]\r\nPath01={root}\r\n");
        AssertThrows<ToolException>(() => BrokerConfiguration.Load(configPath));

        string id = NewId();
        byte[] request = QueueProtocol.SerializeRequest(new StatusRequest(root), id);
        CommandRequest parsed = QueueProtocol.ParseRequest(request, id, root);
        Assert(parsed is StatusRequest status && status.Workspace == root, "Request was not bound to the trusted workspace.");

        var changesets = new ChangesetDiffsRequest(root, 1, 2, DiffAlgorithm.Histogram, false);
        byte[] changesetRequest = QueueProtocol.SerializeRequest(changesets, id);
        Assert(
            QueueProtocol.ParseRequest(changesetRequest, id, root) is ChangesetDiffsRequest { FindRenames: false },
            "A non-rename changeset request did not round-trip.");

        ToolException commandRenameFailure = AssertThrows<ToolException>(() => CommandLine.Parse(
            ["changeset-diffs", "--from", "1", "--to", "2", "--find-renames", "--workspace", root]));
        Assert(commandRenameFailure.ExitCode == ExitCodes.InvalidArguments, "CLI rename rejection used the wrong exit code.");

        byte[] renameRequest = Encoding.UTF8.GetBytes(
            $"{{\"protocol\":1,\"id\":\"{id}\",\"tool\":\"changeset-diffs\",\"arguments\":{{\"from\":1,\"to\":2,\"algorithm\":\"histogram\",\"findRenames\":true}}}}");
        ToolException protocolRenameFailure = AssertThrows<ToolException>(
            () => QueueProtocol.ParseRequest(renameRequest, id, root));
        Assert(protocolRenameFailure.ExitCode == ExitCodes.InvalidArguments, "Protocol rename rejection used the wrong exit code.");

        byte[] unknownMember = Encoding.UTF8.GetBytes(
            $"{{\"protocol\":1,\"id\":\"{id}\",\"tool\":\"status\",\"arguments\":{{}},\"workspace\":\"C:\\\\escape\"}}");
        AssertThrows<ToolException>(() => QueueProtocol.ParseRequest(unknownMember, id, root));
        return Task.CompletedTask;
    }

    static Task TestPendingFileNameFiltersAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "filename-filters", "workspace")).FullName;

        var unfiltered = new PendingChangesDiffsRequest(root, []);
        Assert(unfiltered.MatchesFileName(@"Assets\Scripts\Anything.cs"),
            "An unfiltered request excluded a filename.");

        var exact = new PendingChangesDiffsRequest(root, ["AnimalController.cs"]);
        Assert(exact.MatchesFileName(@"Assets\Scripts\animalcontroller.CS"),
            "Exact filename matching was not case-insensitive or basename-only.");
        Assert(!exact.MatchesFileName(@"Assets\Scripts\AnimalView.cs"),
            "An exact filename filter matched a different filename.");

        var wildcard = new PendingChangesDiffsRequest(root, ["Animal*.cs", "Shader??.compute"]);
        Assert(wildcard.MatchesFileName(@"One\AnimalView.cs") &&
               wildcard.MatchesFileName(@"Two\Shader01.compute"),
            "Repeated filename filters did not use OR semantics.");
        Assert(!wildcard.MatchesFileName(@"Two\Shader1.compute"),
            "The question-mark wildcard did not match exactly one character.");

        var cli = (PendingChangesDiffsRequest)CommandLine.Parse(
            ["pending-changes-diffs", "--file-filter", "Animal*.cs", "--workspace", root,
             "--file-filter", "Shader??.compute"]);
        Assert(cli.Workspace == root && cli.FileNameFilters.Count == 2 &&
               cli.FileNameFilters[0] == "Animal*.cs" && cli.FileNameFilters[1] == "Shader??.compute",
            "The CLI did not preserve repeated filename filters.");

        string id = NewId();
        byte[] filteredJson = QueueProtocol.SerializeRequest(cli, id);
        string filteredText = Encoding.UTF8.GetString(filteredJson);
        Assert(filteredText.Contains("\"fileNameFilters\"", StringComparison.Ordinal),
            "A filtered request omitted its queue argument.");
        var filteredRoundTrip = (PendingChangesDiffsRequest)QueueProtocol.ParseRequest(filteredJson, id, root);
        Assert(filteredRoundTrip.FileNameFilters.Count == 2 &&
               filteredRoundTrip.MatchesFileName(@"Duplicate\AnimalView.cs"),
            "A filtered request did not survive the queue round trip.");

        byte[] unfilteredJson = QueueProtocol.SerializeRequest(unfiltered, id);
        Assert(!Encoding.UTF8.GetString(unfilteredJson).Contains("fileNameFilters", StringComparison.Ordinal),
            "An unfiltered request changed the version 1 queue payload.");
        Assert(QueueProtocol.ParseRequest(unfilteredJson, id, root) is PendingChangesDiffsRequest
               { FileNameFilters.Count: 0 },
            "An unfiltered queue request did not round-trip.");

        AssertThrows<ToolException>(() => new PendingChangesDiffsRequest(root, [""]));
        AssertThrows<ToolException>(() => new PendingChangesDiffsRequest(root, ["Folder/Animal.cs"]));
        AssertThrows<ToolException>(() => new PendingChangesDiffsRequest(root, [@"Folder\Animal.cs"]));
        AssertThrows<ToolException>(() => new PendingChangesDiffsRequest(root, new string?[33]));
        AssertThrows<ToolException>(() => new PendingChangesDiffsRequest(root, [new string('a', 256)]));
        AssertThrows<ToolException>(() => new PendingChangesDiffsRequest(root,
            [new string('a', 205), new string('b', 205), new string('c', 205),
             new string('d', 205), new string('e', 205)]));

        byte[] nullFilter = Encoding.UTF8.GetBytes(
            $"{{\"protocol\":1,\"id\":\"{id}\",\"tool\":\"pending-changes-diffs\",\"arguments\":{{\"fileNameFilters\":[null]}}}}");
        AssertThrows<ToolException>(() => QueueProtocol.ParseRequest(nullFilter, id, root));

        byte[] statusFilter = Encoding.UTF8.GetBytes(
            $"{{\"protocol\":1,\"id\":\"{id}\",\"tool\":\"status\",\"arguments\":{{\"fileNameFilters\":[\"*.cs\"]}}}}");
        AssertThrows<ToolException>(() => QueueProtocol.ParseRequest(statusFilter, id, root));

        byte[] changesetFilter = Encoding.UTF8.GetBytes(
            $"{{\"protocol\":1,\"id\":\"{id}\",\"tool\":\"changeset-diffs\",\"arguments\":{{\"from\":1,\"to\":2,\"algorithm\":\"histogram\",\"findRenames\":false,\"fileNameFilters\":[\"*.cs\"]}}}}");
        AssertThrows<ToolException>(() => QueueProtocol.ParseRequest(changesetFilter, id, root));
        return Task.CompletedTask;
    }

    static async Task TestOrderedParallelPipelineAsync()
    {
        int[] source = CreateIndices(64);
        int activePreparation = 0;
        int maximumPreparation = 0;
        int completedPreparation = 0;
        int activeTransform = 0;
        int maximumTransform = 0;
        int overlapped = 0;

        string[] results = await OrderedParallelPipeline.RunAsync<int, int, string>(
            source,
            async (_, value, cancellationToken) =>
            {
                int active = Interlocked.Increment(ref activePreparation);
                UpdateMaximum(ref maximumPreparation, active);
                try
                {
                    await Task.Delay(40 + value % 5, cancellationToken);
                    Interlocked.Increment(ref completedPreparation);
                    return value;
                }
                finally
                {
                    Interlocked.Decrement(ref activePreparation);
                }
            },
            async (_, value, cancellationToken) =>
            {
                if (Volatile.Read(ref completedPreparation) < source.Length)
                    Interlocked.Exchange(ref overlapped, 1);
                int active = Interlocked.Increment(ref activeTransform);
                UpdateMaximum(ref maximumTransform, active);
                try
                {
                    await Task.Delay(40 + (source.Length - value) % 7, cancellationToken);
                    return $"result-{value}";
                }
                finally
                {
                    Interlocked.Decrement(ref activeTransform);
                }
            },
            CancellationToken.None);

        Assert(maximumPreparation == OrderedParallelPipeline.MaximumConcurrency,
            $"Expected {OrderedParallelPipeline.MaximumConcurrency} preparation workers, observed {maximumPreparation}.");
        Assert(maximumTransform == OrderedParallelPipeline.MaximumConcurrency,
            $"Expected {OrderedParallelPipeline.MaximumConcurrency} transform workers, observed {maximumTransform}.");
        Assert(overlapped == 1, "Transform work did not overlap preparation.");
        for (int index = 0; index < results.Length; index++)
            Assert(results[index] == $"result-{index}", "Pipeline results were not returned in source order.");
    }

    static async Task TestPipelineBackpressureAndTerminationAsync()
    {
        int[] source = CreateIndices(64);
        int prepared = 0;
        int transforming = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int[]> pipeline = OrderedParallelPipeline.RunAsync<int, int, int>(
            source,
            (_, value, _) =>
            {
                Interlocked.Increment(ref prepared);
                return Task.FromResult(value);
            },
            async (_, value, cancellationToken) =>
            {
                Interlocked.Increment(ref transforming);
                await release.Task.WaitAsync(cancellationToken);
                return value;
            },
            CancellationToken.None);

        await WaitUntilAsync(() =>
            Volatile.Read(ref transforming) == OrderedParallelPipeline.MaximumConcurrency &&
            Volatile.Read(ref prepared) == OrderedParallelPipeline.MaximumConcurrency * 3);
        release.SetResult();
        await pipeline.WaitAsync(TestTimeout);

        int consumerPrepared = 0;
        int consumerTransforming = 0;
        var consumerFailureTrigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task consumerFailure = OrderedParallelPipeline.RunAsync<int, int, int>(
            source,
            (_, value, _) =>
            {
                Interlocked.Increment(ref consumerPrepared);
                return Task.FromResult(value);
            },
            async (_, value, _) =>
            {
                Interlocked.Increment(ref consumerTransforming);
                try
                {
                    await consumerFailureTrigger.Task;
                    if (value == 0)
                        throw new InvalidOperationException("consumer failure");
                    return value;
                }
                finally
                {
                    Interlocked.Decrement(ref consumerTransforming);
                }
            },
            CancellationToken.None);
        await WaitUntilAsync(() =>
            Volatile.Read(ref consumerTransforming) == OrderedParallelPipeline.MaximumConcurrency &&
            Volatile.Read(ref consumerPrepared) == OrderedParallelPipeline.MaximumConcurrency * 3);
        consumerFailureTrigger.SetResult();
        await AssertThrowsAsync<InvalidOperationException>(() => consumerFailure.WaitAsync(TestTimeout));

        int producerPrepared = 0;
        int producerTransforming = 0;
        var producerFailureReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerFailureTrigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task producerFailure = OrderedParallelPipeline.RunAsync<int, int, int>(
            source,
            async (_, value, _) =>
            {
                Interlocked.Increment(ref producerPrepared);
                if (value == OrderedParallelPipeline.MaximumConcurrency * 3 - 1)
                {
                    producerFailureReady.SetResult();
                    await producerFailureTrigger.Task;
                    throw new InvalidOperationException("producer failure");
                }
                return value;
            },
            async (_, value, cancellationToken) =>
            {
                Interlocked.Increment(ref producerTransforming);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return value;
                }
                finally
                {
                    Interlocked.Decrement(ref producerTransforming);
                }
            },
            CancellationToken.None);
        await WaitUntilAsync(() =>
            producerFailureReady.Task.IsCompleted &&
            Volatile.Read(ref producerTransforming) == OrderedParallelPipeline.MaximumConcurrency &&
            Volatile.Read(ref producerPrepared) == OrderedParallelPipeline.MaximumConcurrency * 3);
        producerFailureTrigger.SetResult();
        await AssertThrowsAsync<InvalidOperationException>(() => producerFailure.WaitAsync(TestTimeout));

        int cancellationPrepared = 0;
        int cancellationTransforming = 0;
        using var cancellation = new CancellationTokenSource();
        Task canceled = OrderedParallelPipeline.RunAsync<int, int, int>(
            source,
            (_, value, _) =>
            {
                Interlocked.Increment(ref cancellationPrepared);
                return Task.FromResult(value);
            },
            async (_, value, cancellationToken) =>
            {
                Interlocked.Increment(ref cancellationTransforming);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return value;
                }
                finally
                {
                    Interlocked.Decrement(ref cancellationTransforming);
                }
            },
            cancellation.Token);
        await WaitUntilAsync(() =>
            Volatile.Read(ref cancellationTransforming) == OrderedParallelPipeline.MaximumConcurrency &&
            Volatile.Read(ref cancellationPrepared) == OrderedParallelPipeline.MaximumConcurrency * 3);
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => canceled.WaitAsync(TestTimeout));
    }

    static async Task TestUvcsRunnerAsync()
    {
        int active = 0;
        int maximum = 0;
        int calls = 0;
        using var runner = new UvcsRunner(async (_, _, _, _, cancellationToken, _) =>
        {
            Interlocked.Increment(ref calls);
            int current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            try
            {
                await Task.Delay(15, cancellationToken);
                return new ProcessResult(0, string.Empty, string.Empty);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var executions = new Task<ProcessResult>[32];
        for (int index = 0; index < executions.Length; index++)
            executions[index] = runner.RunAsync(["status"], Environment.CurrentDirectory, TestTimeout, 1024);
        await Task.WhenAll(executions);
        Assert(calls == executions.Length && maximum == 1,
            $"UVCS runner started {maximum} processes concurrently across {calls} calls.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waitingCalls = 0;
        using var waitingRunner = new UvcsRunner(async (_, _, _, _, cancellationToken, _) =>
        {
            Interlocked.Increment(ref waitingCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new ProcessResult(0, string.Empty, string.Empty);
        });
        Task<ProcessResult> first = waitingRunner.RunAsync(
            ["first"], Environment.CurrentDirectory, TestTimeout, 1024);
        await entered.Task.WaitAsync(TestTimeout);
        using var waitingCancellation = new CancellationTokenSource();
        Task<ProcessResult> waiting = waitingRunner.RunAsync(
            ["waiting"], Environment.CurrentDirectory, TestTimeout, 1024, waitingCancellation.Token);
        waitingCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => waiting);
        Assert(waitingCalls == 1, "A canceled UVCS gate waiter started a process.");
        release.SetResult();
        await first;

        int releaseCalls = 0;
        using var failureRunner = new UvcsRunner((_, _, _, _, _, _) =>
        {
            if (Interlocked.Increment(ref releaseCalls) == 1)
                throw new InvalidOperationException("expected failure");
            return Task.FromResult(new ProcessResult(0, "11.0.16.8411", string.Empty));
        });
        await AssertThrowsAsync<InvalidOperationException>(() => failureRunner.RunAsync(
            ["fail"], Environment.CurrentDirectory, TestTimeout, 1024));
        Version accepted = await failureRunner.RequireMinimumVersionAsync(Environment.CurrentDirectory);
        Assert(accepted == UvcsRunner.MinimumVersion && releaseCalls == 2,
            "A failed UVCS process did not release the global gate.");

        Assert(UvcsRunner.ParseVersion("Plastic SCM version 11.0.16.8411") == UvcsRunner.MinimumVersion,
            "The minimum UVCS version was not parsed.");
        Assert(UvcsRunner.ParseVersion("11.0.16.9999") > UvcsRunner.MinimumVersion,
            "A newer UVCS version was not parsed.");
        AssertThrows<ToolException>(() => UvcsRunner.ParseVersion("unknown"));

        using var oldRunner = new UvcsRunner((_, _, _, _, _, _) =>
            Task.FromResult(new ProcessResult(0, "11.0.16.8410", string.Empty)));
        ToolException oldVersion = await AssertThrowsAsync<ToolException>(() =>
            oldRunner.RequireMinimumVersionAsync(Environment.CurrentDirectory));
        Assert(oldVersion.ExitCode == ExitCodes.DependencyOrWorkspaceFailure,
            "An old UVCS client did not return the dependency exit code.");
    }

    static Task TestChangesetMetadataAndBatchesAsync(string runRoot)
    {
        const char separator = '\u001f';
        string metadata = string.Join(Environment.NewLine,
            $"C{separator}F{separator}/src/Changed.cs{separator}{separator}",
            $"A{separator}B{separator}/src/Added.shader{separator}{separator}",
            $"D{separator}S{separator}/src/Deleted.uxml{separator}{separator}",
            $"M{separator}F{separator}/unused{separator}/src/Old.cs{separator}/src/New.cs",
            $"M{separator}F{separator}/unused{separator}/src/Case.cs{separator}/src/case.cs",
            $"A{separator}D{separator}/src/Directory.cs{separator}{separator}",
            $"A{separator}X{separator}/src/Xlink.cs{separator}{separator}",
            $"A{separator}F{separator}/src/Ignored.txt{separator}{separator}");
        List<ChangesetDiffsCommand.LogicalChange> changes = ChangesetDiffsCommand.ParseChanges(metadata);

        Assert(changes.Count == 7, $"Expected 7 logical changes, found {changes.Count}.");
        Assert(changes.Exists(change => change == new ChangesetDiffsCommand.LogicalChange("src/Changed.cs", true, true)),
            "Changed metadata was not mapped to an old/new pair.");
        Assert(changes.Exists(change => change == new ChangesetDiffsCommand.LogicalChange("src/Added.shader", false, true)),
            "Added metadata was not mapped to a new side.");
        Assert(changes.Exists(change => change == new ChangesetDiffsCommand.LogicalChange("src/Deleted.uxml", true, false)),
            "Deleted metadata was not mapped to an old side.");
        Assert(changes.Exists(change => change == new ChangesetDiffsCommand.LogicalChange("src/Old.cs", true, false)) &&
               changes.Exists(change => change == new ChangesetDiffsCommand.LogicalChange("src/New.cs", false, true)),
            "A move was not split into delete/add logical changes.");
        Assert(changes.Exists(change => change.RepositoryPath == "src/Case.cs") &&
               changes.Exists(change => change.RepositoryPath == "src/case.cs"),
            "A case-only move collapsed distinct logical paths.");

        AssertThrows<ToolException>(() => ChangesetDiffsCommand.ParseChanges(
            $"C{separator}F{separator}/src/Conflict.cs{separator}{separator}{Environment.NewLine}" +
            $"D{separator}F{separator}/src/Conflict.cs{separator}{separator}"));
        AssertThrows<ToolException>(() => ChangesetDiffsCommand.ParseChanges(
            $"M{separator}F{separator}/unused{separator}{separator}/src/New.cs"));
        AssertThrows<ToolException>(() => ChangesetDiffsCommand.ParseChanges(
            $"C{separator}Q{separator}/src/Unknown.cs{separator}{separator}"));
        AssertThrows<ToolException>(() => ChangesetDiffsCommand.ParseChanges(
            $"Q{separator}F{separator}/src/Unknown.cs{separator}{separator}"));
        AssertThrows<ToolException>(() => ChangesetDiffsCommand.ParseChanges(
            $"Q{separator}D{separator}/src/Unknown.cs{separator}{separator}"));
        ToolException movedDirectory = AssertThrows<ToolException>(() => ChangesetDiffsCommand.ParseChanges(
            $"M{separator}D{separator}/unused{separator}/src/Old{separator}/src/New"));
        Assert(movedDirectory.Message.Contains("Moved directory", StringComparison.Ordinal),
            "A moved directory was silently discarded.");
        AssertThrows<ToolException>(() => ChangesetDiffsCommand.ParseChanges(
            $"C{separator}F{separator}/../Unsafe.cs{separator}{separator}"));

        string root = Directory.CreateDirectory(Path.Combine(runRoot, "changeset-batches")).FullName;
        string oldRoot = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        string newRoot = Directory.CreateDirectory(Path.Combine(root, "b")).FullName;
        var many = new List<ChangesetDiffsCommand.LogicalChange>();
        for (int index = 0; index < 9; index++)
            many.Add(new ChangesetDiffsCommand.LogicalChange($"src/File{index:00}.cs", true, true));
        List<ChangesetDiffsCommand.DownloadBatch> batches = ChangesetDiffsCommand.BuildDownloadBatches(
            many, oldRoot, newRoot, 10, 20);
        Assert(batches.Count == 2 && batches[0].Downloads.Length == 16 && batches[1].Downloads.Length == 2,
            "Revision downloads were not packed to the 16-entry limit.");
        Assert(batches[0].Changes.Length == 8 && batches[1].Changes.Length == 1,
            "A two-sided logical change was split across download batches.");
        Assert(batches.TrueForAll(batch => batch.Downloads.Length <= 16),
            "A changeset download batch exceeded 16 revisions.");

        var semicolon = new[]
        {
            new ChangesetDiffsCommand.LogicalChange("src/Semi;colon.cs", true, true)
        };
        List<ChangesetDiffsCommand.DownloadBatch> semicolonBatches = ChangesetDiffsCommand.BuildDownloadBatches(
            semicolon, oldRoot, newRoot, 10, 20);
        Assert(semicolonBatches.Count == 1, "A semicolon path produced more than one logical batch.");
        ChangesetDiffsCommand.DownloadBatch semicolonBatch = semicolonBatches[0];
        Assert(!semicolonBatch.UseCollection && semicolonBatch.Downloads.Length == 2,
            "A semicolon path did not select the safe single-download form.");

        var longPathBuilder = new StringBuilder();
        for (int index = 0; index < 70; index++)
            longPathBuilder.Append(new string('a', 200)).Append('/');
        string longPath = longPathBuilder.Append("File.cs").ToString();
        var oversized = new[]
        {
            new ChangesetDiffsCommand.LogicalChange(longPath, false, true)
        };
        List<ChangesetDiffsCommand.DownloadBatch> oversizedBatches = ChangesetDiffsCommand.BuildDownloadBatches(
            oversized, oldRoot, newRoot, 10, 20);
        Assert(oversizedBatches.Count == 1, "An oversized path produced more than one logical batch.");
        ChangesetDiffsCommand.DownloadBatch oversizedBatch = oversizedBatches[0];
        Assert(!oversizedBatch.UseCollection,
            "An oversized collection argument did not select the single-download form.");
        return Task.CompletedTask;
    }

    static async Task TestChangesetBatchCommandAsync(string runRoot)
    {
        const char separator = '\u001f';
        string successMetadata = string.Join(Environment.NewLine,
            $"A{separator}F{separator}/src/Added.cs{separator}{separator}",
            $"C{separator}F{separator}/src/Changed.cs{separator}{separator}",
            $"D{separator}F{separator}/src/Deleted.cs{separator}{separator}",
            $"C{separator}F{separator}/src/Semi;colon.cs{separator}{separator}");
        int collectionCalls = 0;
        int collectionDownloads = 0;
        int singleCalls = 0;
        CommandOutcome success = await ExecuteAsync("success", successMetadata, arguments =>
        {
            Assert(arguments[arguments.Count - 1] == "--raw", "A changeset download omitted --raw.");
            if (arguments.Count >= 3 && arguments[2].StartsWith("--file=", StringComparison.Ordinal))
            {
                singleCalls++;
                WriteDownload(arguments[1], arguments[2]["--file=".Length..]);
            }
            else
            {
                collectionCalls++;
                for (int index = 1; index < arguments.Count - 1; index++)
                {
                    int delimiter = arguments[index].IndexOf(';');
                    Assert(delimiter > 0 && delimiter < arguments[index].Length - 1,
                        "A collection download did not contain one revision/destination pair.");
                    collectionDownloads++;
                    WriteDownload(arguments[index][..delimiter], arguments[index][(delimiter + 1)..]);
                }
            }
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        Assert(success.ExitCode == ExitCodes.Success, "A complete changeset batch command failed.");
        Assert(collectionCalls == 1 && collectionDownloads == 4,
            $"Expected one four-revision collection, observed {collectionCalls} calls and {collectionDownloads} revisions.");
        Assert(singleCalls == 2, $"Expected two semicolon-safe single downloads, observed {singleCalls}.");
        Assert(success.Output.Contains("src/Added.cs", StringComparison.Ordinal) &&
               success.Output.Contains("src/Changed.cs", StringComparison.Ordinal) &&
               success.Output.Contains("src/Deleted.cs", StringComparison.Ordinal) &&
               success.Output.Contains("src/Semi;colon.cs", StringComparison.Ordinal),
            "The complete changeset batch did not produce every expected patch section.");

        string missingMetadata = $"C{separator}F{separator}/src/Missing.cs{separator}{separator}";
        ToolException missing = await AssertThrowsAsync<ToolException>(() =>
            ExecuteAsync("missing", missingMetadata, arguments =>
            {
                int delimiter = arguments[1].IndexOf(';');
                WriteDownload(arguments[1][..delimiter], arguments[1][(delimiter + 1)..]);
                return new ProcessResult(0, "download stdout", "download stderr");
            }));
        Assert(missing.ExitCode == ExitCodes.InternalFailure &&
               missing.Message.Contains("src/Missing.cs (new", StringComparison.Ordinal) &&
               missing.Message.Contains("download stdout", StringComparison.Ordinal) &&
               missing.Message.Contains("download stderr", StringComparison.Ordinal),
            "An incomplete changeset batch did not report the missing endpoint and both diagnostic streams.");

        string failedMetadata = $"C{separator}F{separator}/src/Failed.cs{separator}{separator}";
        ToolException failed = await AssertThrowsAsync<ToolException>(() =>
            ExecuteAsync("failed", failedMetadata, _ =>
                new ProcessResult(7, "failure stdout", "failure stderr")));
        Assert(failed.ExitCode == ExitCodes.ExternalCommandFailure &&
               failed.Message.Contains("stdout: failure stdout", StringComparison.Ordinal) &&
               failed.Message.Contains("stderr: failure stderr", StringComparison.Ordinal),
            "A failed changeset batch did not preserve both child diagnostic streams.");

        async Task<CommandOutcome> ExecuteAsync(
            string caseName,
            string metadata,
            Func<IReadOnlyList<string>, ProcessResult> download)
        {
            string workspaceRoot = Directory.CreateDirectory(
                Path.Combine(runRoot, "changeset-command", caseName)).FullName;
            using var uvcs = new UvcsRunner((arguments, _, _, _, _, _) =>
            {
                ProcessResult result = arguments[0] switch
                {
                    "getworkspacefrompath" => new ProcessResult(
                        0,
                        workspaceRoot + Environment.NewLine,
                        string.Empty),
                    "diff" => new ProcessResult(0, metadata, string.Empty),
                    "getfile" => download(arguments),
                    _ => throw new InvalidOperationException($"Unexpected fake UVCS command '{arguments[0]}'.")
                };
                return Task.FromResult(result);
            });
            var command = new ChangesetDiffsCommand(
                new ProcessRunner(),
                uvcs,
                new WorkspaceResolver(uvcs),
                TextWriter.Null);
            return await command.ExecuteAsync(new ChangesetDiffsRequest(
                workspaceRoot,
                10,
                20,
                DiffAlgorithm.Histogram,
                false));
        }

        static void WriteDownload(string revision, string destination)
        {
            string content = revision.EndsWith("#cs:10", StringComparison.Ordinal)
                ? "old content\n"
                : "new content\n";
            File.WriteAllText(destination, content);
        }
    }

    static async Task TestOrderedBatchPipelineAsync()
    {
        int[][] batches =
        [
            [0, 1, 2, 3, 4, 5, 6, 7],
            [8, 9, 10, 11, 12, 13, 14, 15],
            [16, 17, 18, 19, 20, 21, 22, 23],
            [24, 25, 26, 27, 28, 29, 30, 31]
        ];
        int activePreparation = 0;
        int maximumPreparation = 0;
        int preparedBatches = 0;
        int activeTransform = 0;
        int maximumTransform = 0;
        int overlapped = 0;

        string[] results = await OrderedBatchPipeline.RunAsync<int[], int, string>(
            batches,
            32,
            async (batch, cancellationToken) =>
            {
                int active = Interlocked.Increment(ref activePreparation);
                UpdateMaximum(ref maximumPreparation, active);
                try
                {
                    await Task.Delay(60, cancellationToken);
                    var items = new IndexedItem<int>[batch.Length];
                    for (int index = 0; index < batch.Length; index++)
                        items[index] = new IndexedItem<int>(batch[index], batch[index]);
                    Interlocked.Increment(ref preparedBatches);
                    return items;
                }
                finally
                {
                    Interlocked.Decrement(ref activePreparation);
                }
            },
            async (_, value, cancellationToken) =>
            {
                if (Volatile.Read(ref preparedBatches) < batches.Length)
                    Interlocked.Exchange(ref overlapped, 1);
                int active = Interlocked.Increment(ref activeTransform);
                UpdateMaximum(ref maximumTransform, active);
                try
                {
                    await Task.Delay(80 + value % 5, cancellationToken);
                    return $"result-{value}";
                }
                finally
                {
                    Interlocked.Decrement(ref activeTransform);
                }
            },
            CancellationToken.None);

        Assert(maximumPreparation == 1, $"Expected one batch producer, observed {maximumPreparation}.");
        Assert(maximumTransform == OrderedBatchPipeline.MaximumConcurrency,
            $"Expected {OrderedBatchPipeline.MaximumConcurrency} batch consumers, observed {maximumTransform}.");
        Assert(overlapped == 1, "Batch downloads did not overlap local transforms.");
        for (int index = 0; index < results.Length; index++)
            Assert(results[index] == $"result-{index}", "Batch pipeline results were not returned in source order.");

        var transformed = new List<int>();
        InvalidOperationException duplicate = await AssertThrowsAsync<InvalidOperationException>(() =>
            OrderedBatchPipeline.RunAsync<int, int, int>(
                [0, 1],
                2,
                (batch, _) => Task.FromResult<IReadOnlyList<IndexedItem<int>>>(batch == 0
                    ? [new IndexedItem<int>(0, 0)]
                    : [new IndexedItem<int>(1, 1), new IndexedItem<int>(1, 1)]),
                (_, value, _) =>
                {
                    lock (transformed)
                        transformed.Add(value);
                    return Task.FromResult(value);
                },
                CancellationToken.None));
        Assert(duplicate.Message.Contains("more than once", StringComparison.Ordinal),
            "Duplicate batch indices did not fail explicitly.");
        lock (transformed)
            Assert(!transformed.Contains(1), "An incomplete batch published an item before validation.");

        await AssertThrowsAsync<InvalidOperationException>(() => OrderedBatchPipeline.RunAsync<int, int, int>(
            [0],
            2,
            (_, _) => Task.FromResult<IReadOnlyList<IndexedItem<int>>>([new IndexedItem<int>(0, 0)]),
            (_, value, _) => Task.FromResult(value),
            CancellationToken.None));
        await AssertThrowsAsync<InvalidOperationException>(() => OrderedBatchPipeline.RunAsync<int, int, int>(
            [0],
            1,
            (_, _) => Task.FromResult<IReadOnlyList<IndexedItem<int>>>([new IndexedItem<int>(1, 1)]),
            (_, value, _) => Task.FromResult(value),
            CancellationToken.None));
    }

    static async Task TestIncrementalProcessOutputLimitsAsync(string runRoot)
    {
        const string endlessOutput = "for /L %i in (1,1,2147483647) do @echo 012345678901234567890123456789";
        var processes = new ProcessRunner();
        ToolException processFailure = await AssertThrowsAsync<ToolException>(() => processes.RunAsync(
            "cmd.exe",
            ["/d", "/c", endlessOutput],
            runRoot,
            TimeSpan.FromMinutes(1),
            1024).WaitAsync(TestTimeout));
        Assert(processFailure.Message.Contains("output limit", StringComparison.Ordinal),
            "The process was not stopped by its incremental output limit.");

        var sharedBudget = new OutputBudget(1024, "Shared output limit exceeded.");
        Task<ProcessResult> first = processes.RunAsync(
            "cmd.exe",
            ["/d", "/c", endlessOutput],
            runRoot,
            TimeSpan.FromMinutes(1),
            1024 * 1024,
            default,
            sharedBudget);
        Task<ProcessResult> second = processes.RunAsync(
            "cmd.exe",
            ["/d", "/c", endlessOutput],
            runRoot,
            TimeSpan.FromMinutes(1),
            1024 * 1024,
            default,
            sharedBudget);
        ToolException sharedFailure = await AssertThrowsAsync<ToolException>(
            () => Task.WhenAll(first, second).WaitAsync(TestTimeout));
        Assert(sharedBudget.IsExceeded && sharedFailure.Message == "Shared output limit exceeded.",
            "Parallel processes did not enforce their shared output budget.");

        ToolException repeatedFailure = AssertThrows<ToolException>(sharedBudget.ThrowIfExceeded);
        Assert(
            repeatedFailure.ExitCode == ExitCodes.ExternalCommandFailure &&
            repeatedFailure.Message == sharedFailure.Message,
            "Shared output budget exhaustion was not deterministic for concurrent workers.");
    }

    static async Task TestWorkspaceReadBoundaryAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "read-boundary", "workspace")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(runRoot, "read-boundary", "outside")).FullName;
        string insideTarget = Path.Combine(root, "inside-target.cs");
        string outsideTarget = Path.Combine(outside, "outside-target.cs");
        string insideLink = Path.Combine(root, "inside-link.cs");
        string outsideLink = Path.Combine(root, "outside-link.cs");
        await File.WriteAllTextAsync(insideTarget, "inside");
        await File.WriteAllTextAsync(outsideTarget, "outside");
        File.CreateSymbolicLink(insideLink, insideTarget);
        File.CreateSymbolicLink(outsideLink, outsideTarget);

        using var boundary = new WorkspaceReadBoundary(root);
        await using (FileStream stream = boundary.OpenRead(insideLink, 4096))
        using (var reader = new StreamReader(stream))
            Assert(await reader.ReadToEndAsync() == "inside", "An in-workspace link was rejected.");

        ToolException failure = AssertThrows<ToolException>(() =>
        {
            using FileStream _ = boundary.OpenRead(outsideLink, 4096);
        });
        Assert(
            failure.ExitCode == ExitCodes.DependencyOrWorkspaceFailure &&
            failure.Message.Contains("outside the configured workspace", StringComparison.Ordinal),
            "A link resolving outside the workspace was not rejected.");
    }

    static Task TestWorkspaceQueueBoundaryAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "queue-boundary", "workspace")).FullName;
        string movedResults = Path.Combine(root, "moved-results");
        var queue = WorkspaceQueue.CreateForBroker(root);
        try
        {
            bool moveBlocked = false;
            try
            {
                Directory.Move(queue.Results, movedResults);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                moveBlocked = true;
            }
            Assert(moveBlocked, "A pinned broker queue directory could be renamed.");
        }
        finally
        {
            queue.Dispose();
        }

        Directory.Move(queue.Results, movedResults);
        Directory.Move(movedResults, queue.Results);

        string maliciousRoot = Directory.CreateDirectory(
            Path.Combine(runRoot, "queue-boundary", "malicious-workspace")).FullName;
        string outside = Directory.CreateDirectory(
            Path.Combine(runRoot, "queue-boundary", "outside")).FullName;
        Directory.CreateSymbolicLink(
            Path.Combine(maliciousRoot, BrokerPaths.CoordinationDirectoryName),
            outside);

        ToolException failure = AssertThrows<ToolException>(() =>
        {
            WorkspaceQueue maliciousQueue = WorkspaceQueue.CreateForBroker(maliciousRoot);
            maliciousQueue.Dispose();
        });
        Assert(
            failure.ExitCode == ExitCodes.DependencyOrWorkspaceFailure &&
            failure.Message.Contains("reparse point", StringComparison.Ordinal),
            "A broker queue rooted at a filesystem link was not rejected.");
        return Task.CompletedTask;
    }

    static async Task TestPerFileGitEquivalenceAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "git-equivalence")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.WriteAllText(Path.Combine(root, "a", "delete.cs"), "old delete\n");
        File.WriteAllText(Path.Combine(root, "a", "modify.cs"), "old value\n");
        File.WriteAllText(Path.Combine(root, "a", "same.cs"), "same value\n");
        File.WriteAllText(Path.Combine(root, "b", "add.cs"), "new add\n");
        File.WriteAllText(Path.Combine(root, "b", "modify.cs"), "new value\n");
        File.WriteAllText(Path.Combine(root, "b", "same.cs"), "same value\n");

        var processes = new ProcessRunner();
        ProcessResult combined = await processes.RunAsync(
            "git",
            ChangesetDiffsCommand.BuildGitArguments(DiffAlgorithm.Histogram, false, "a", "b"),
            root,
            TestTimeout,
            1024 * 1024);
        Assert(combined.ExitCode is 0 or 1, "Combined Git fixture failed.");

        string[] paths = ["add.cs", "delete.cs", "modify.cs", "same.cs"];
        var perFile = new StringBuilder();
        for (int index = 0; index < paths.Length; index++)
        {
            string path = paths[index];
            string oldArgument = File.Exists(Path.Combine(root, "a", path)) ? $"a/{path}" : "/dev/null";
            string newArgument = File.Exists(Path.Combine(root, "b", path)) ? $"b/{path}" : "/dev/null";
            ProcessResult diff = await processes.RunAsync(
                "git",
                ChangesetDiffsCommand.BuildGitArguments(
                    DiffAlgorithm.Histogram,
                    false,
                    oldArgument,
                    newArgument),
                root,
                TestTimeout,
                1024 * 1024);
            Assert(diff.ExitCode is 0 or 1, $"Per-file Git fixture failed for {path}.");
            perFile.Append(diff.StandardOutput);
        }

        Assert(perFile.ToString() == combined.StandardOutput,
            $"Per-file Git output did not match the combined --no-renames patch.\nCOMBINED:\n{combined.StandardOutput}\nPER FILE:\n{perFile}");
    }

    static async Task TestPreCanceledProcessAsync(string runRoot)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new ProcessRunner().RunAsync(
                "NV-AITools-test-command-that-must-not-start.exe",
                [],
                runRoot,
                TimeSpan.FromSeconds(1),
                1024,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("A pre-canceled command attempted to start a process.");
    }

    static async Task TestClientRoundTripAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "client", "workspace")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        using WorkspaceQueue queue = WorkspaceQueue.CreateForBroker(root);
        Task broker = Task.Run(async () =>
        {
            string requestPath = await WaitForSingleFileAsync(queue.Requests, "*.request");
            string id = Path.GetFileNameWithoutExtension(requestPath);
            string processingPath = Path.Combine(queue.Processing, Path.GetFileName(requestPath));
            File.Move(requestPath, processingPath);
            QueueProtocol.ParseRequest(await File.ReadAllBytesAsync(processingPath), id, root);
            await File.WriteAllTextAsync(Path.Combine(queue.Results, $"{id}.stdout"), "round-trip");
            await File.WriteAllTextAsync(Path.Combine(queue.Results, $"{id}.stderr"), "diagnostic");
            await File.WriteAllBytesAsync(
                Path.Combine(queue.Results, $"{id}.result"),
                QueueProtocol.SerializeResult(id, ExitCodes.Success));
        });

        CommandOutcome outcome = await QueueClient.ExecuteAsync(new StatusRequest(child));
        await broker;
        Assert(outcome.Output == "round-trip", "Client did not return broker stdout.");
        Assert(outcome.Diagnostics == "diagnostic", "Client did not return broker stderr.");
        Assert(Directory.GetFiles(queue.Results).Length == 0, "Client did not clean consumed result files.");
    }

    static async Task TestWorkspaceConcurrencyAsync(string runRoot)
    {
        var capacity = new SemaphoreSlim(4, 4);
        var log = new BrokerLog();
        var tracker = new ExecutionTracker();
        var runtimes = new List<WorkspaceRuntime>();
        var queues = new List<WorkspaceQueue>();
        var requestIds = new List<(WorkspaceQueue Queue, string Id)>();

        try
        {
            for (int index = 0; index < 5; index++)
            {
                string root = Directory.CreateDirectory(Path.Combine(runRoot, "concurrency", $"workspace-{index}")).FullName;
                WorkspaceQueue queue = WorkspaceQueue.CreateForBroker(root);
                var runtime = new WorkspaceRuntime(
                    root,
                    capacity,
                    tracker.ExecuteAsync,
                    log,
                    _ => { },
                    _ => { },
                    () => { });
                runtime.Prepare();
                runtime.Activate();
                queues.Add(queue);
                runtimes.Add(runtime);
            }

            for (int workspace = 0; workspace < queues.Count; workspace++)
            {
                for (int request = 0; request < 2; request++)
                {
                    string id = Publish(queues[workspace], new StatusRequest(queues[workspace].WorkspaceRoot));
                    requestIds.Add((queues[workspace], id));
                }
            }

            await WaitUntilAsync(() => requestIds.All(item =>
                File.Exists(Path.Combine(item.Queue.Results, $"{item.Id}.result"))));
            Assert(tracker.MaximumGlobal == 4, $"Expected global concurrency 4, observed {tracker.MaximumGlobal}.");
            Assert(tracker.MaximumByWorkspace.Values.All(value => value == 1), "A workspace executed more than one request concurrently.");
        }
        finally
        {
            for (int index = 0; index < runtimes.Count; index++)
                await runtimes[index].DisposeAsync();
            for (int index = 0; index < queues.Count; index++)
                queues[index].Dispose();
            capacity.Dispose();
        }
    }

    static async Task TestShutdownCompletionAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "shutdown", "workspace")).FullName;
        using WorkspaceQueue queue = WorkspaceQueue.CreateForBroker(root);
        var capacity = new SemaphoreSlim(4, 4);
        var runtime = new WorkspaceRuntime(
            root,
            capacity,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new CommandOutcome();
            },
            new BrokerLog(),
            _ => { },
            _ => { },
            () => { });

        runtime.Prepare();
        runtime.Activate();
        string first = Publish(queue, new StatusRequest(root));
        string second = Publish(queue, new StatusRequest(root));
        await WaitUntilAsync(() => Directory.GetFiles(queue.Processing, "*.request").Length == 2);
        await runtime.DisposeAsync();

        AssertResultExit(queue, first, ExitCodes.ExternalCommandFailure);
        AssertResultExit(queue, second, ExitCodes.ExternalCommandFailure);
        capacity.Dispose();
    }

    static async Task TestCrashRecoveryAsync(string runRoot)
    {
        string root = Directory.CreateDirectory(Path.Combine(runRoot, "recovery", "workspace")).FullName;
        using WorkspaceQueue queue = WorkspaceQueue.CreateForBroker(root);
        string id = NewId();
        string processingPath = Path.Combine(queue.Processing, $"{id}.request");
        await File.WriteAllBytesAsync(
            processingPath,
            QueueProtocol.SerializeRequest(new StatusRequest(root), id));
        await File.WriteAllTextAsync(Path.Combine(queue.Results, $"{id}.stdout"), "partial");

        var capacity = new SemaphoreSlim(4, 4);
        var runtime = new WorkspaceRuntime(
            root,
            capacity,
            (_, _) => Task.FromResult(new CommandOutcome("recovered")),
            new BrokerLog(),
            _ => { },
            _ => { },
            () => { });
        try
        {
            runtime.Prepare();
            runtime.Activate();
            string resultPath = Path.Combine(queue.Results, $"{id}.result");
            await WaitUntilAsync(() => File.Exists(resultPath));
            Assert(
                File.ReadAllText(Path.Combine(queue.Results, $"{id}.stdout")) == "recovered",
                "Recovered execution retained partial output.");
            Assert(!File.Exists(processingPath), "Recovered processing marker was not removed.");
        }
        finally
        {
            await runtime.DisposeAsync();
            capacity.Dispose();
        }
    }

    static string Publish(WorkspaceQueue queue, CommandRequest request)
    {
        string id = NewId();
        string temporaryPath = Path.Combine(queue.Requests, $"{id}.tmp");
        File.WriteAllBytes(temporaryPath, QueueProtocol.SerializeRequest(request, id));
        File.Move(temporaryPath, Path.Combine(queue.Requests, $"{id}.request"));
        return id;
    }

    static void AssertResultExit(WorkspaceQueue queue, string id, int expectedExit)
    {
        string path = Path.Combine(queue.Results, $"{id}.result");
        Assert(File.Exists(path), $"Request {id} did not receive a shutdown result.");
        QueueResult result = QueueProtocol.ParseResult(File.ReadAllBytes(path), id);
        Assert(result.ExitCode == expectedExit, $"Request {id} returned exit {result.ExitCode}.");
    }

    static async Task<string> WaitForSingleFileAsync(string directory, string pattern)
    {
        string? found = null;
        await WaitUntilAsync(() =>
        {
            found = Directory.GetFiles(directory, pattern).SingleOrDefault();
            return found is not null;
        });
        return found!;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    static string CreateRunRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "NV-AITools", "Artifacts", "TestRuns", NewId()));
        string allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "NV-AITools", "Artifacts", "TestRuns")) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test directory escaped the repository.");
        Directory.CreateDirectory(root);
        return root;
    }

    static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    static int[] CreateIndices(int count)
    {
        var values = new int[count];
        for (int index = 0; index < values.Length; index++)
            values[index] = index;
        return values;
    }

    static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref maximum)) &&
               Interlocked.CompareExchange(ref maximum, value, observed) != observed)
        {
        }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    static TException AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    sealed class ExecutionTracker
    {
        readonly object gate = new();
        readonly Dictionary<string, int> activeByWorkspace = new(StringComparer.OrdinalIgnoreCase);
        int activeGlobal;

        public int MaximumGlobal { get; private set; }
        public Dictionary<string, int> MaximumByWorkspace { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task<CommandOutcome> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                activeGlobal++;
                activeByWorkspace.TryGetValue(request.Workspace, out int active);
                activeByWorkspace[request.Workspace] = ++active;
                MaximumGlobal = Math.Max(MaximumGlobal, activeGlobal);
                MaximumByWorkspace.TryGetValue(request.Workspace, out int maximum);
                MaximumByWorkspace[request.Workspace] = Math.Max(maximum, active);
            }

            try
            {
                await Task.Delay(250, cancellationToken);
                return new CommandOutcome("ok");
            }
            finally
            {
                lock (gate)
                {
                    activeGlobal--;
                    activeByWorkspace[request.Workspace]--;
                }
            }
        }
    }
}
