using System.Globalization;
using System.Text;
using UvcsTools.Cli;
using UvcsTools.Infrastructure;
using UvcsTools.Models;

namespace UvcsTools.Commands;

sealed class PendingChangesDiffsCommand(ProcessRunner processes, WorkspaceResolver workspaces)
{
    const int MaximumOutputCharacters = 64 * 1024 * 1024;
    static readonly TimeSpan CmTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan FcTimeout = TimeSpan.FromMinutes(2);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".shader", ".cginc", ".compute"
    };

    public async Task<CommandOutcome> ExecuteAsync(PendingChangesDiffsRequest request)
    {
        Console.Error.WriteLine("Resolving UVCS workspace...");
        string workspaceRoot = await workspaces.ResolveAsync(request.Workspace);
        Console.Error.WriteLine("Reading pending changes...");
        List<StatusEntry> entries = await new StatusReader(processes).ReadAsync(workspaceRoot);

        var report = new StringBuilder();
        int processed = 0;
        int skipped = 0;
        int failed = 0;
        int failureExitCode = ExitCodes.Success;

        using var temporary = new TempDirectory();
        for (int index = 0; index < entries.Count; index++)
        {
            StatusEntry entry = entries[index];
            string baseCode = StatusReader.GetBaseCode(entry.StatusCode);
            if (entry.IsDirectory || !Extensions.Contains(Path.GetExtension(entry.Path)) ||
                baseCode is "MV" or "LM" or "CO")
            {
                skipped++;
                continue;
            }

            Console.Error.WriteLine($"Comparing {entry.Path}...");
            try
            {
                string itemDirectory = Path.Combine(temporary.Root, index.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(itemDirectory);
                string baselinePath = Path.Combine(itemDirectory, "baseline.txt");
                string workingPath = Path.Combine(itemDirectory, "working.txt");

                if (baseCode is "AD" or "PR")
                {
                    await File.WriteAllBytesAsync(baselinePath, []);
                }
                else
                {
                    string changeset = await GetRevisionChangesetAsync(entry.FullPath, workspaceRoot);
                    await GetBaselineAsync(entry.FullPath, changeset, baselinePath, workspaceRoot);
                }

                if (File.Exists(entry.FullPath) && baseCode is not "DE" and not "LD")
                    File.Copy(entry.FullPath, workingPath);
                else
                    await File.WriteAllBytesAsync(workingPath, []);

                ProcessResult comparison = await processes.RunAsync(
                    "fc.exe",
                    ["/n", baselinePath, workingPath],
                    workspaceRoot,
                    FcTimeout,
                    MaximumOutputCharacters);

                if (comparison.ExitCode > 1)
                    throw ExternalFailure("fc.exe", comparison);

                AppendSection(report, entry, comparison.StandardOutput, baselinePath, workingPath);
                processed++;
            }
            catch (ToolException exception)
            {
                Console.Error.WriteLine($"Failed {entry.Path}: {exception.Message}");
                AppendFailure(report, entry, exception.Message);
                failureExitCode = Math.Max(failureExitCode, exception.ExitCode);
                failed++;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Failed {entry.Path}: {exception.Message}");
                AppendFailure(report, entry, exception.Message);
                failureExitCode = Math.Max(failureExitCode, ExitCodes.InternalFailure);
                failed++;
            }
        }

        report.AppendLine("================================================================================");
        report.AppendLine($"Processed: {processed}");
        report.AppendLine($"Skipped: {skipped}");
        report.AppendLine($"Failed: {failed}");
        return new CommandOutcome(report.ToString(), failureExitCode);
    }

    async Task<string> GetRevisionChangesetAsync(string fullPath, string workspaceRoot)
    {
        ProcessResult result = await processes.RunAsync(
            "cm",
            ["fileinfo", fullPath, "--format={RevisionChangeset}"],
            workspaceRoot,
            CmTimeout,
            32 * 1024);

        if (result.ExitCode != 0)
            throw ExternalFailure("cm fileinfo", result);

        string changeset = result.StandardOutput.Trim();
        if (!int.TryParse(changeset, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new ToolException(
                $"UVCS returned an invalid RevisionChangeset for '{fullPath}'.",
                ExitCodes.InternalFailure);

        return changeset;
    }

    async Task GetBaselineAsync(string fullPath, string changeset, string destination, string workspaceRoot)
    {
        ProcessResult result = await processes.RunAsync(
            "cm",
            ["getfile", $"{fullPath}#cs:{changeset}", $"--file={destination}", "--raw"],
            workspaceRoot,
            CmTimeout,
            MaximumOutputCharacters);

        if (result.ExitCode != 0 || !File.Exists(destination))
            throw ExternalFailure("cm getfile", result);
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

    static ToolException ExternalFailure(string command, ProcessResult result)
    {
        string diagnostics = result.StandardError.Trim();
        string message = diagnostics.Length == 0
            ? $"'{command}' failed with exit code {result.ExitCode}."
            : $"'{command}' failed with exit code {result.ExitCode}: {diagnostics}";
        return new ToolException(message, ExitCodes.ExternalCommandFailure);
    }
}
