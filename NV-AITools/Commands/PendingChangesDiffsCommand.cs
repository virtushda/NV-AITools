using System.Globalization;
using System.Text;
using NVAITools.Cli;
using NVAITools.Infrastructure;
using NVAITools.Models;

namespace NVAITools.Commands;

sealed class PendingChangesDiffsCommand(
    ProcessRunner processes,
    WorkspaceResolver workspaces,
    TextWriter diagnostics)
{
    const int MaximumComparisonCharacters = 64 * 1024 * 1024;
    const int MaximumReportCharacters = 64 * 1024 * 1024;
    static readonly TimeSpan CmTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan FcTimeout = TimeSpan.FromMinutes(2);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".shader", ".cginc", ".compute"
    };

    public async Task<CommandOutcome> ExecuteAsync(
        PendingChangesDiffsRequest request,
        CancellationToken cancellationToken = default)
    {
        var outputBudget = new OutputBudget(
            MaximumReportCharacters,
            "Pending changes report exceeded the configured output limit.");
        await diagnostics.WriteLineAsync("Resolving UVCS workspace...");
        string workspaceRoot = await workspaces.ResolveAsync(request.Workspace, cancellationToken, outputBudget);
        using var workspaceFiles = new WorkspaceReadBoundary(workspaceRoot);
        await diagnostics.WriteLineAsync("Reading pending changes...");
        List<StatusEntry> entries = await new StatusReader(processes).ReadAsync(
            workspaceRoot,
            cancellationToken,
            outputBudget);

        var items = new List<PendingItem>(entries.Count);
        int skipped = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            StatusEntry entry = entries[index];
            string baseCode = StatusReader.GetBaseCode(entry.StatusCode);
            if (entry.IsDirectory || !Extensions.Contains(Path.GetExtension(entry.Path)) ||
                !request.MatchesFileName(entry.Path) ||
                baseCode is "MV" or "LM" or "CO")
            {
                skipped++;
                continue;
            }

            items.Add(new PendingItem(entry, baseCode));
        }

        using var temporary = new TempDirectory();
        PendingResult[] results = await OrderedParallelPipeline.RunAsync<PendingItem, PendingPrepared, PendingResult>(
            items,
            PrepareAsync,
            CompareAsync,
            cancellationToken);

        var report = new StringBuilder();
        int processed = 0;
        int failed = 0;
        int failureExitCode = ExitCodes.Success;
        for (int index = 0; index < results.Length; index++)
        {
            PendingResult result = results[index];
            await diagnostics.WriteLineAsync($"Comparing {result.Prepared.Item.Entry.Path}...");
            if (result.Failure is { } failure)
            {
                await diagnostics.WriteLineAsync($"Failed {result.Prepared.Item.Entry.Path}: {failure.Message}");
                AppendFailure(report, result.Prepared.Item.Entry, failure.Message);
                failureExitCode = Math.Max(failureExitCode, failure.ExitCode);
                failed++;
            }
            else
            {
                AppendSection(
                    report,
                    result.Prepared.Item.Entry,
                    result.Comparison,
                    result.Prepared.BaselinePath,
                    result.Prepared.WorkingPath);
                processed++;
            }

            EnsureReportLimit(report.Length);
        }

        report.AppendLine("================================================================================");
        report.AppendLine($"Processed: {processed}");
        report.AppendLine($"Skipped: {skipped}");
        report.AppendLine($"Failed: {failed}");
        EnsureReportLimit(report.Length);
        return new CommandOutcome(report.ToString(), failureExitCode);

        async Task<PendingPrepared> PrepareAsync(
            int index,
            PendingItem item,
            CancellationToken token)
        {
            try
            {
                string itemDirectory = Path.Combine(temporary.Root, index.ToString(CultureInfo.InvariantCulture));
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(itemDirectory);
                string baselinePath = Path.Combine(itemDirectory, "baseline.txt");
                string workingPath = Path.Combine(itemDirectory, "working.txt");

                if (item.BaseCode is "AD" or "PR")
                {
                    await File.WriteAllBytesAsync(baselinePath, [], token);
                }
                else
                {
                    string changeset = await GetRevisionChangesetAsync(
                        item.Entry.FullPath,
                        workspaceRoot,
                        token,
                        outputBudget);
                    await GetBaselineAsync(
                        item.Entry.FullPath,
                        changeset,
                        baselinePath,
                        workspaceRoot,
                        token,
                        outputBudget);
                }

                if (File.Exists(item.Entry.FullPath) && item.BaseCode is not "DE" and not "LD")
                    await CopyFileAsync(workspaceFiles, item.Entry.FullPath, workingPath, token);
                else
                    await File.WriteAllBytesAsync(workingPath, [], token);

                return new PendingPrepared(item, baselinePath, workingPath, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                outputBudget.ThrowIfExceeded();
                return new PendingPrepared(item, string.Empty, string.Empty, GetFailure(exception));
            }
        }

        async Task<PendingResult> CompareAsync(
            int _,
            PendingPrepared prepared,
            CancellationToken token)
        {
            if (prepared.Failure is { } preparationFailure)
                return new PendingResult(prepared, string.Empty, preparationFailure);

            ProcessResult comparison;
            try
            {
                comparison = await processes.RunAsync(
                    "fc.exe",
                    ["/n", prepared.BaselinePath, prepared.WorkingPath],
                    workspaceRoot,
                    FcTimeout,
                    MaximumComparisonCharacters,
                    token,
                    outputBudget);

                if (comparison.ExitCode > 1)
                    throw ExternalFailure("fc.exe", comparison);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                outputBudget.ThrowIfExceeded();
                return new PendingResult(prepared, string.Empty, GetFailure(exception));
            }

            return new PendingResult(prepared, comparison.StandardOutput, null);
        }
    }

    async Task<string> GetRevisionChangesetAsync(
        string fullPath,
        string workspaceRoot,
        CancellationToken cancellationToken,
        OutputBudget outputBudget)
    {
        ProcessResult result = await processes.RunAsync(
            "cm",
            ["fileinfo", fullPath, "--format={RevisionChangeset}"],
            workspaceRoot,
            CmTimeout,
            32 * 1024,
            cancellationToken,
            outputBudget);

        if (result.ExitCode != 0)
            throw ExternalFailure("cm fileinfo", result);

        string changeset = result.StandardOutput.Trim();
        if (!int.TryParse(changeset, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new ToolException(
                $"UVCS returned an invalid RevisionChangeset for '{fullPath}'.",
                ExitCodes.InternalFailure);

        return changeset;
    }

    async Task GetBaselineAsync(
        string fullPath,
        string changeset,
        string destination,
        string workspaceRoot,
        CancellationToken cancellationToken,
        OutputBudget outputBudget)
    {
        ProcessResult result = await processes.RunAsync(
            "cm",
            ["getfile", $"{fullPath}#cs:{changeset}", $"--file={destination}", "--raw"],
            workspaceRoot,
            CmTimeout,
            MaximumComparisonCharacters,
            cancellationToken,
            outputBudget);

        if (result.ExitCode != 0 || !File.Exists(destination))
            throw ExternalFailure("cm getfile", result);
    }

    static async Task CopyFileAsync(
        WorkspaceReadBoundary workspaceFiles,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 64 * 1024;
        await using var source = workspaceFiles.OpenRead(sourcePath, BufferSize);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, BufferSize, cancellationToken);
    }

    static void AppendSection(
        StringBuilder report,
        StatusEntry entry,
        string comparison,
        string baselinePath,
        string workingPath)
    {
        report.AppendLine("================================================================================");
        report.AppendLine($"Path: {entry.Path}");
        report.AppendLine($"Status: {entry.Status} ({entry.StatusCode})");
        report.AppendLine($"Full path: {entry.FullPath}");
        report.AppendLine("--------------------------------------------------------------------------------");
        string cleanComparison = comparison
            .Replace(baselinePath, $"BASELINE:{entry.Path}", StringComparison.OrdinalIgnoreCase)
            .Replace(workingPath, $"WORKING:{entry.Path}", StringComparison.OrdinalIgnoreCase);
        report.Append(cleanComparison);
        if (cleanComparison.Length > 0 && cleanComparison[^1] != '\n')
            report.AppendLine();
    }

    static void AppendFailure(StringBuilder report, StatusEntry entry, string message)
    {
        report.AppendLine("================================================================================");
        report.AppendLine($"Path: {entry.Path}");
        report.AppendLine($"Status: {entry.Status} ({entry.StatusCode})");
        report.AppendLine($"Full path: {entry.FullPath}");
        report.AppendLine($"ERROR: {message}");
    }

    static PendingFailure GetFailure(Exception exception) => exception is ToolException tool
        ? new PendingFailure(tool.Message, tool.ExitCode)
        : new PendingFailure(exception.Message, ExitCodes.InternalFailure);

    static void EnsureReportLimit(int length)
    {
        if (length > MaximumReportCharacters)
            throw new ToolException(
                "Pending changes report exceeded the configured output limit.",
                ExitCodes.ExternalCommandFailure);
    }

    static ToolException ExternalFailure(string command, ProcessResult result)
    {
        string diagnostics = result.StandardError.Trim();
        string message = diagnostics.Length == 0
            ? $"'{command}' failed with exit code {result.ExitCode}."
            : $"'{command}' failed with exit code {result.ExitCode}: {diagnostics}";
        return new ToolException(message, ExitCodes.ExternalCommandFailure);
    }

    readonly record struct PendingItem(StatusEntry Entry, string BaseCode);

    readonly record struct PendingPrepared(
        PendingItem Item,
        string BaselinePath,
        string WorkingPath,
        PendingFailure? Failure);

    readonly record struct PendingResult(
        PendingPrepared Prepared,
        string Comparison,
        PendingFailure? Failure);

    readonly record struct PendingFailure(string Message, int ExitCode);
}
