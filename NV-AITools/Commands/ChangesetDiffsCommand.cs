using System.Text;
using NVAITools.Cli;
using NVAITools.Infrastructure;

namespace NVAITools.Commands;

sealed class ChangesetDiffsCommand(
    ProcessRunner processes,
    UvcsRunner uvcs,
    WorkspaceResolver workspaces,
    TextWriter diagnostics)
{
    const char FieldSeparator = '\u001f';
    const int MaximumMetadataCharacters = 64 * 1024 * 1024;
    const int MaximumCommandOutputCharacters = 64 * 1024 * 1024;
    const int MaximumBatchEntries = 16;
    const int MaximumBatchCommandCharacters = 24 * 1024;
    const int BatchCommandBaseCharacters = 64;
    static readonly TimeSpan CmTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);
    // Keep this disabled path: one Git comparison over both complete trees is the correct way to restore cross-file rename detection.
    static readonly bool EnableCombinedTreeRenameDiff = false;
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".asmdef", ".asmref", ".shader", ".hlsl", ".compute", ".cginc", ".uxml", ".uss"
    };

    public async Task<CommandOutcome> ExecuteAsync(
        ChangesetDiffsRequest request,
        CancellationToken cancellationToken = default)
    {
        var outputBudget = new OutputBudget(
            MaximumCommandOutputCharacters,
            "Changeset patch exceeded the configured output limit.");
        await diagnostics.WriteLineAsync("Resolving UVCS workspace...");
        string workspaceRoot = await workspaces.ResolveAsync(request.Workspace, cancellationToken, outputBudget);
        await RequireGitAsync(workspaceRoot, cancellationToken, outputBudget);

        await diagnostics.WriteLineAsync($"Finding changes from cs:{request.From} through cs:{request.To}...");
        List<LogicalChange> changes = await GetChangesAsync(
            request.From,
            request.To,
            workspaceRoot,
            cancellationToken,
            outputBudget);
        if (changes.Count == 0)
            return new CommandOutcome(string.Empty);

        using var temporary = new TempDirectory();
        string oldRoot = Path.Combine(temporary.Root, "a");
        string newRoot = Path.Combine(temporary.Root, "b");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);

        List<DownloadBatch> batches = BuildDownloadBatches(
            changes,
            oldRoot,
            newRoot,
            request.From,
            request.To);
        ChangeResult[] results = await OrderedBatchPipeline.RunAsync<DownloadBatch, PreparedChange, ChangeResult>(
            batches,
            changes.Count,
            PrepareBatchAsync,
            CompareAsync,
            cancellationToken);

        int exported = 0;
        for (int index = 0; index < results.Length; index++)
        {
            ChangeResult result = results[index];
            await diagnostics.WriteLineAsync($"Exporting {result.Prepared.RepositoryPath}...");
            if (result.IsBinary)
            {
                await diagnostics.WriteLineAsync($"Skipping binary-like content: {result.Prepared.RepositoryPath}");
                continue;
            }

            if (result.Prepared.HasOld || result.Prepared.HasNew)
                exported++;
        }

        if (exported == 0)
            return new CommandOutcome(string.Empty);

        await diagnostics.WriteLineAsync("Generating unified patch...");
        if (EnableCombinedTreeRenameDiff)
            return await GenerateCombinedTreeDiffAsync(request, temporary.Root, cancellationToken, outputBudget);

        var patch = new StringBuilder();
        for (int index = 0; index < results.Length; index++)
        {
            ChangeResult result = results[index];
            if (!string.IsNullOrWhiteSpace(result.GitDiagnostics))
                await diagnostics.WriteAsync(result.GitDiagnostics);
            patch.Append(result.Patch);
        }
        if (patch.Length > MaximumCommandOutputCharacters)
            throw PatchLimitExceeded();

        return new CommandOutcome(patch.ToString());

        async Task<IReadOnlyList<IndexedItem<PreparedChange>>> PrepareBatchAsync(
            DownloadBatch batch,
            CancellationToken token)
        {
            for (int index = 0; index < batch.Downloads.Length; index++)
                Directory.CreateDirectory(Path.GetDirectoryName(batch.Downloads[index].Destination)!);

            var childDiagnostics = new StringBuilder();
            if (batch.UseCollection)
            {
                var arguments = new List<string>(batch.Downloads.Length + 2) { "getfile" };
                for (int index = 0; index < batch.Downloads.Length; index++)
                {
                    DownloadEntry download = batch.Downloads[index];
                    arguments.Add($"{download.Revision};{download.Destination}");
                }
                arguments.Add("--raw");

                ProcessResult result = await uvcs.RunAsync(
                    arguments,
                    workspaceRoot,
                    CmTimeout,
                    MaximumMetadataCharacters,
                    token,
                    outputBudget);
                if (result.ExitCode != 0)
                    throw ExternalFailure("cm getfile", result);
                AppendDiagnostics(childDiagnostics, result);
            }
            else
            {
                for (int index = 0; index < batch.Downloads.Length; index++)
                {
                    DownloadEntry download = batch.Downloads[index];
                    ProcessResult result = await uvcs.RunAsync(
                        ["getfile", download.Revision, $"--file={download.Destination}", "--raw"],
                        workspaceRoot,
                        CmTimeout,
                        MaximumMetadataCharacters,
                        token,
                        outputBudget);
                    if (result.ExitCode != 0)
                        throw ExternalFailure("cm getfile", result);
                    AppendDiagnostics(childDiagnostics, result);
                }
            }

            ValidateBatchOutputs(batch, childDiagnostics);
            var prepared = new IndexedItem<PreparedChange>[batch.Changes.Length];
            for (int index = 0; index < batch.Changes.Length; index++)
                prepared[index] = new IndexedItem<PreparedChange>(
                    batch.Changes[index].Index,
                    batch.Changes[index].Prepared);
            return prepared;
        }

        async Task<ChangeResult> CompareAsync(
            int _,
            PreparedChange prepared,
            CancellationToken token)
        {
            try
            {
                if (!prepared.HasOld && !prepared.HasNew)
                    return new ChangeResult(prepared, string.Empty, string.Empty, false);

                bool binary = prepared.HasOld && await IsBinaryLikeAsync(prepared.OldPath, token) ||
                    prepared.HasNew && await IsBinaryLikeAsync(prepared.NewPath, token);
                if (binary)
                {
                    token.ThrowIfCancellationRequested();
                    if (prepared.HasOld)
                        File.Delete(prepared.OldPath);
                    if (prepared.HasNew)
                        File.Delete(prepared.NewPath);
                    return new ChangeResult(prepared, string.Empty, string.Empty, true);
                }

                if (EnableCombinedTreeRenameDiff)
                    return new ChangeResult(prepared, string.Empty, string.Empty, false);

                ProcessResult diff = await processes.RunAsync(
                    "git",
                    BuildPerFileGitArguments(request, prepared, temporary.Root),
                    temporary.Root,
                    GitTimeout,
                    MaximumCommandOutputCharacters,
                    token,
                    outputBudget);
                if (diff.ExitCode is not 0 and not 1)
                    throw ExternalFailure("git diff --no-index", diff);

                return new ChangeResult(prepared, diff.StandardOutput, diff.StandardError, false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                outputBudget.ThrowIfExceeded();
                if (exception is not ToolException toolException)
                    throw;
                throw new ToolException(
                    $"Failed to compare '{prepared.RepositoryPath}': {toolException.Message}",
                    toolException.ExitCode,
                    toolException);
            }
        }
    }

    async Task RequireGitAsync(
        string workspaceRoot,
        CancellationToken cancellationToken,
        OutputBudget outputBudget)
    {
        ProcessResult result = await processes.RunAsync(
            "git",
            ["--version"],
            workspaceRoot,
            TimeSpan.FromSeconds(30),
            32 * 1024,
            cancellationToken,
            outputBudget);
        if (result.ExitCode != 0)
            throw new ToolException("Git is unavailable.", ExitCodes.DependencyOrWorkspaceFailure);
    }

    async Task<List<LogicalChange>> GetChangesAsync(
        int from,
        int to,
        string workspaceRoot,
        CancellationToken cancellationToken,
        OutputBudget outputBudget)
    {
        string format = $"{{status}}{FieldSeparator}{{type}}{FieldSeparator}{{path}}" +
            $"{FieldSeparator}{{srccmpath}}{FieldSeparator}{{dstcmpath}}{{newline}}";
        ProcessResult result = await uvcs.RunAsync(
            [
                "diff",
                $"cs:{from}",
                $"cs:{to}",
                "--repositorypaths",
                $"--format={format}"
            ],
            workspaceRoot,
            CmTimeout,
            MaximumMetadataCharacters,
            cancellationToken,
            outputBudget);

        if (result.ExitCode != 0)
            throw ExternalFailure("cm diff", result);

        return ParseChanges(result.StandardOutput);
    }

    internal static List<LogicalChange> ParseChanges(string output)
    {
        var candidates = new Dictionary<string, LogicalChange>(StringComparer.Ordinal);
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] fields = line.Split(FieldSeparator);
            if (fields.Length != 5)
                throw new ToolException($"Could not parse UVCS diff line: {line}", ExitCodes.InternalFailure);

            string status = fields[0].Trim();
            string type = fields[1].Trim();
            if (status is not "A" and not "C" and not "D" and not "M")
                throw new ToolException($"Unknown UVCS diff status '{status}'.", ExitCodes.InternalFailure);
            if (type == "D")
            {
                if (status == "M")
                {
                    string source = NormalizeRepositoryPath(fields[3]);
                    string destination = NormalizeRepositoryPath(fields[4]);
                    throw new ToolException(
                        $"Moved directory '{source}' to '{destination}' cannot be represented safely as per-file differences.",
                        ExitCodes.InternalFailure);
                }
                continue;
            }
            if (type == "X")
                continue;
            if (type is not "F" and not "B" and not "S")
                throw new ToolException($"Unknown UVCS diff item type '{type}'.", ExitCodes.InternalFailure);

            switch (status)
            {
                case "C":
                    Add(fields[2], true, true);
                    break;
                case "A":
                    Add(fields[2], false, true);
                    break;
                case "D":
                    Add(fields[2], true, false);
                    break;
                case "M":
                    Add(fields[3], true, false);
                    Add(fields[4], false, true);
                    break;
            }
        }

        var sorted = new List<LogicalChange>(candidates.Values);
        sorted.Sort(static (left, right) =>
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.RepositoryPath, right.RepositoryPath);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.RepositoryPath, right.RepositoryPath);
        });
        return sorted;

        void Add(string rawPath, bool hasOld, bool hasNew)
        {
            string repositoryPath = NormalizeRepositoryPath(rawPath);
            if (!Extensions.Contains(Path.GetExtension(repositoryPath)))
                return;

            var candidate = new LogicalChange(repositoryPath, hasOld, hasNew);
            if (!candidates.TryAdd(repositoryPath, candidate) && candidates[repositoryPath] != candidate)
                throw new ToolException(
                    $"UVCS returned conflicting changes for '{repositoryPath}'.",
                    ExitCodes.InternalFailure);
        }
    }

    internal static List<DownloadBatch> BuildDownloadBatches(
        IReadOnlyList<LogicalChange> changes,
        string oldRoot,
        string newRoot,
        int from,
        int to)
    {
        var batches = new List<DownloadBatch>();
        var currentChanges = new List<BatchChange>();
        var currentDownloads = new List<DownloadEntry>();
        int currentCharacters = BatchCommandBaseCharacters;

        for (int index = 0; index < changes.Count; index++)
        {
            LogicalChange change = changes[index];
            string oldPath = GetSafeEndpointPath(oldRoot, change.RepositoryPath);
            string newPath = GetSafeEndpointPath(newRoot, change.RepositoryPath);
            var batchChange = new BatchChange(
                index,
                new PreparedChange(change.RepositoryPath, oldPath, newPath, change.HasOld, change.HasNew));
            var downloads = new List<DownloadEntry>(2);
            if (change.HasOld)
                downloads.Add(CreateDownload(change.RepositoryPath, "old", from, oldPath));
            if (change.HasNew)
                downloads.Add(CreateDownload(change.RepositoryPath, "new", to, newPath));

            int itemCharacters = 0;
            bool useSingle = false;
            for (int downloadIndex = 0; downloadIndex < downloads.Count; downloadIndex++)
            {
                DownloadEntry download = downloads[downloadIndex];
                useSingle |= download.Revision.Contains(';') || download.Destination.Contains(';');
                itemCharacters += EstimateArgumentCharacters($"{download.Revision};{download.Destination}");
            }
            useSingle |= BatchCommandBaseCharacters + itemCharacters > MaximumBatchCommandCharacters;

            if (useSingle)
            {
                Flush();
                batches.Add(new DownloadBatch([batchChange], downloads.ToArray(), false));
                continue;
            }

            if (currentDownloads.Count + downloads.Count > MaximumBatchEntries ||
                currentCharacters + itemCharacters > MaximumBatchCommandCharacters)
            {
                Flush();
            }

            currentChanges.Add(batchChange);
            currentDownloads.AddRange(downloads);
            currentCharacters += itemCharacters;
        }

        Flush();
        return batches;

        void Flush()
        {
            if (currentChanges.Count == 0)
                return;
            batches.Add(new DownloadBatch(currentChanges.ToArray(), currentDownloads.ToArray(), true));
            currentChanges.Clear();
            currentDownloads.Clear();
            currentCharacters = BatchCommandBaseCharacters;
        }
    }

    static DownloadEntry CreateDownload(
        string repositoryPath,
        string endpoint,
        int changeset,
        string destination) => new(
            repositoryPath,
            endpoint,
            $"serverpath:/{repositoryPath}#cs:{changeset}",
            destination);

    static int EstimateArgumentCharacters(string argument) => checked(argument.Length * 2 + 3);

    static void ValidateBatchOutputs(DownloadBatch batch, StringBuilder childDiagnostics)
    {
        StringBuilder? missing = null;
        for (int index = 0; index < batch.Downloads.Length; index++)
        {
            DownloadEntry download = batch.Downloads[index];
            if (File.Exists(download.Destination))
                continue;

            missing ??= new StringBuilder("UVCS completed without creating every requested file:");
            missing.AppendLine();
            missing.Append("- ")
                .Append(download.RepositoryPath)
                .Append(" (")
                .Append(download.Endpoint)
                .Append(", ")
                .Append(download.Revision)
                .Append(") -> ")
                .Append(download.Destination);
        }

        if (missing is null)
            return;
        if (childDiagnostics.Length > 0)
            missing.AppendLine().Append("UVCS diagnostics: ").Append(childDiagnostics.ToString().Trim());
        throw new ToolException(missing.ToString(), ExitCodes.InternalFailure);
    }

    static void AppendDiagnostics(StringBuilder destination, ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            destination.AppendLine(result.StandardError.Trim());
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            destination.AppendLine(result.StandardOutput.Trim());
    }

    async Task<CommandOutcome> GenerateCombinedTreeDiffAsync(
        ChangesetDiffsRequest request,
        string temporaryRoot,
        CancellationToken cancellationToken,
        OutputBudget outputBudget)
    {
        ProcessResult diff = await processes.RunAsync(
            "git",
            BuildCombinedTreeGitArguments(request),
            temporaryRoot,
            GitTimeout,
            MaximumCommandOutputCharacters,
            cancellationToken,
            outputBudget);
        if (!string.IsNullOrWhiteSpace(diff.StandardError))
            await diagnostics.WriteAsync(diff.StandardError);
        if (diff.ExitCode is not 0 and not 1)
            throw ExternalFailure("git diff --no-index", diff);
        return new CommandOutcome(diff.StandardOutput);
    }

    static IReadOnlyList<string> BuildPerFileGitArguments(
        ChangesetDiffsRequest request,
        PreparedChange prepared,
        string temporaryRoot)
    {
        string oldArgument = prepared.HasOld
            ? GetGitPath(temporaryRoot, prepared.OldPath)
            : "/dev/null";
        string newArgument = prepared.HasNew
            ? GetGitPath(temporaryRoot, prepared.NewPath)
            : "/dev/null";
        return BuildGitArguments(request.Algorithm, false, oldArgument, newArgument);
    }

    static IReadOnlyList<string> BuildCombinedTreeGitArguments(ChangesetDiffsRequest request) =>
        BuildGitArguments(request.Algorithm, true, "a", "b");

    internal static IReadOnlyList<string> BuildGitArguments(
        DiffAlgorithm requestedAlgorithm,
        bool findRenames,
        string oldArgument,
        string newArgument)
    {
        string algorithm = requestedAlgorithm switch
        {
            DiffAlgorithm.Histogram => "histogram",
            DiffAlgorithm.Patience => "patience",
            DiffAlgorithm.Default => "default",
            DiffAlgorithm.Minimal => "minimal",
            _ => throw new InvalidOperationException("Unknown diff algorithm.")
        };

        return
        [
            "-c", "core.autocrlf=false",
            "-c", "core.safecrlf=false",
            "diff",
            "--no-index",
            "--no-ext-diff",
            "--no-textconv",
            "--no-prefix",
            $"--diff-algorithm={algorithm}",
            findRenames ? "--find-renames" : "--no-renames",
            "--",
            oldArgument,
            newArgument
        ];
    }

    static string GetGitPath(string temporaryRoot, string endpointPath) =>
        Path.GetRelativePath(temporaryRoot, endpointPath).Replace('\\', '/');

    static string NormalizeRepositoryPath(string value)
    {
        string path = value.Trim().Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path == ".." || path.StartsWith("../", StringComparison.Ordinal) ||
            path.Contains("/../", StringComparison.Ordinal) || path.Contains('\0'))
            throw new ToolException($"UVCS returned an unsafe repository path: {value}", ExitCodes.InternalFailure);

        return path;
    }

    static string GetSafeEndpointPath(string endpointRoot, string repositoryPath)
    {
        string combined = Path.Combine(endpointRoot, repositoryPath.Replace('/', Path.DirectorySeparatorChar));
        string fullPath = Path.GetFullPath(combined);
        string relative = Path.GetRelativePath(endpointRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new ToolException($"Repository path escaped the temporary endpoint tree: {repositoryPath}", ExitCodes.InternalFailure);
        return fullPath;
    }

    static async Task<bool> IsBinaryLikeAsync(string path, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (buffer.AsSpan(0, read).IndexOf((byte)0) >= 0)
                return true;
        }
        return false;
    }

    static ToolException ExternalFailure(string command, ProcessResult result)
    {
        string standardError = result.StandardError.Trim();
        string standardOutput = result.StandardOutput.Trim();
        var diagnostics = new StringBuilder();
        if (standardError.Length > 0)
            diagnostics.Append("stderr: ").Append(standardError);
        if (standardOutput.Length > 0)
        {
            if (diagnostics.Length > 0)
                diagnostics.AppendLine();
            diagnostics.Append("stdout: ").Append(standardOutput);
        }
        string message = diagnostics.Length == 0
            ? $"'{command}' failed with exit code {result.ExitCode}."
            : $"'{command}' failed with exit code {result.ExitCode}: {diagnostics}";
        return new ToolException(message, ExitCodes.ExternalCommandFailure);
    }

    static ToolException PatchLimitExceeded() => new(
        "Changeset patch exceeded the configured output limit.",
        ExitCodes.ExternalCommandFailure);

    internal readonly record struct LogicalChange(
        string RepositoryPath,
        bool HasOld,
        bool HasNew);

    internal readonly record struct PreparedChange(
        string RepositoryPath,
        string OldPath,
        string NewPath,
        bool HasOld,
        bool HasNew);

    internal readonly record struct BatchChange(int Index, PreparedChange Prepared);

    internal readonly record struct DownloadEntry(
        string RepositoryPath,
        string Endpoint,
        string Revision,
        string Destination);

    internal sealed record DownloadBatch(
        BatchChange[] Changes,
        DownloadEntry[] Downloads,
        bool UseCollection);

    readonly record struct ChangeResult(
        PreparedChange Prepared,
        string Patch,
        string GitDiagnostics,
        bool IsBinary);
}
