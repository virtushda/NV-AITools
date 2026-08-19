using System.Text;
using NVAITools.Cli;
using NVAITools.Infrastructure;

namespace NVAITools.Commands;

sealed class ChangesetDiffsCommand(
    ProcessRunner processes,
    WorkspaceResolver workspaces,
    TextWriter diagnostics)
{
    const char FieldSeparator = '\u001f';
    const int MaximumMetadataCharacters = 64 * 1024 * 1024;
    const int MaximumCommandOutputCharacters = 64 * 1024 * 1024;
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
        List<string> candidates = await GetCandidatesAsync(
            request.From,
            request.To,
            workspaceRoot,
            cancellationToken,
            outputBudget);
        if (candidates.Count == 0)
            return new CommandOutcome(string.Empty);

        using var temporary = new TempDirectory();
        string oldRoot = Path.Combine(temporary.Root, "a");
        string newRoot = Path.Combine(temporary.Root, "b");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);

        ChangeResult[] results = await OrderedParallelPipeline.RunAsync<string, PreparedChange, ChangeResult>(
            candidates,
            PrepareAsync,
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

        async Task<PreparedChange> PrepareAsync(
            int _,
            string repositoryPath,
            CancellationToken token)
        {
            try
            {
                string oldPath = GetSafeEndpointPath(oldRoot, repositoryPath);
                string newPath = GetSafeEndpointPath(newRoot, repositoryPath);
                bool hasOld = await TryExportAsync(
                    repositoryPath,
                    request.From,
                    oldPath,
                    workspaceRoot,
                    token,
                    outputBudget);
                bool hasNew = await TryExportAsync(
                    repositoryPath,
                    request.To,
                    newPath,
                    workspaceRoot,
                    token,
                    outputBudget);
                return new PreparedChange(repositoryPath, oldPath, newPath, hasOld, hasNew);
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
                    $"Failed to export '{repositoryPath}': {toolException.Message}",
                    toolException.ExitCode,
                    toolException);
            }
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

    async Task<List<string>> GetCandidatesAsync(
        int from,
        int to,
        string workspaceRoot,
        CancellationToken cancellationToken,
        OutputBudget outputBudget)
    {
        int lowerBound = from == 0 ? 0 : from - 1;
        string itemFormat = $"{{shortstatus}}{FieldSeparator}{{path}}{{newline}}";
        ProcessResult result = await processes.RunAsync(
            "cm",
            [
                "log",
                $"cs:{to}",
                $"--from=cs:{lowerBound}",
                "--csformat={items}",
                $"--itemformat={itemFormat}",
                "--repositorypaths"
            ],
            workspaceRoot,
            CmTimeout,
            MaximumMetadataCharacters,
            cancellationToken,
            outputBudget);

        if (result.ExitCode != 0)
            throw ExternalFailure("cm log", result);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(result.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int separator = line.IndexOf(FieldSeparator);
            if (separator <= 0 || separator == line.Length - 1)
                throw new ToolException($"Could not parse UVCS log line: {line}", ExitCodes.InternalFailure);

            string status = line[..separator].Trim();
            if (status is not "A" and not "D" and not "M" and not "C")
                throw new ToolException($"Unknown UVCS log status '{status}'.", ExitCodes.InternalFailure);

            string path = NormalizeRepositoryPath(line[(separator + 1)..]);
            if (Extensions.Contains(Path.GetExtension(path)))
                candidates.Add(path);
        }

        var sorted = candidates.ToList();
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    async Task<bool> TryExportAsync(
        string repositoryPath,
        int changeset,
        string destination,
        string workspaceRoot,
        CancellationToken cancellationToken,
        OutputBudget outputBudget)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string revision = $"serverpath:/{repositoryPath}#cs:{changeset}";
        ProcessResult result = await processes.RunAsync(
            "cm",
            ["cat", revision, $"--file={destination}", "--raw"],
            workspaceRoot,
            CmTimeout,
            MaximumMetadataCharacters,
            cancellationToken,
            outputBudget);

        if (result.ExitCode == 0)
        {
            if (!File.Exists(destination))
                throw new ToolException($"UVCS did not create the exported file for '{repositoryPath}'.", ExitCodes.InternalFailure);
            return true;
        }

        string diagnostics = $"{result.StandardError}\n{result.StandardOutput}";
        if (IsMissingRevision(diagnostics))
        {
            File.Delete(destination);
            return false;
        }

        throw ExternalFailure("cm cat", result);
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

    static bool IsMissingRevision(string diagnostics)
    {
        string[] indicators =
        [
            "not found", "cannot find", "couldn't find", "does not exist", "doesn't exist",
            "no revision", "not a file", "not exist"
        ];
        for (int index = 0; index < indicators.Length; index++)
        {
            if (diagnostics.Contains(indicators[index], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
        string diagnostics = result.StandardError.Trim();
        if (diagnostics.Length == 0)
            diagnostics = result.StandardOutput.Trim();
        string message = diagnostics.Length == 0
            ? $"'{command}' failed with exit code {result.ExitCode}."
            : $"'{command}' failed with exit code {result.ExitCode}: {diagnostics}";
        return new ToolException(message, ExitCodes.ExternalCommandFailure);
    }

    static ToolException PatchLimitExceeded() => new(
        "Changeset patch exceeded the configured output limit.",
        ExitCodes.ExternalCommandFailure);

    readonly record struct PreparedChange(
        string RepositoryPath,
        string OldPath,
        string NewPath,
        bool HasOld,
        bool HasNew);

    readonly record struct ChangeResult(
        PreparedChange Prepared,
        string Patch,
        string GitDiagnostics,
        bool IsBinary);
}
