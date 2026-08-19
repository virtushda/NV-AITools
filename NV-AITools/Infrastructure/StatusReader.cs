using NVAITools.Models;

namespace NVAITools.Infrastructure;

sealed class StatusReader(ProcessRunner processes)
{
    const char FieldSeparator = '\u001f';
    const int MaximumOutputCharacters = 64 * 1024 * 1024;
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<List<StatusEntry>> ReadAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default,
        OutputBudget? outputBudget = null)
    {
        string[] arguments =
        [
            "status",
            workspaceRoot,
            "--noheader",
            "--nomergesinfo",
            "--machinereadable",
            $"--fieldseparator={FieldSeparator}",
            "--iscochanged",
            "--added",
            "--changed",
            "--deleted",
            "--localdeleted",
            "--moved",
            "--localmoved",
            "--checkout",
            "--private"
        ];

        ProcessResult result = await processes.RunAsync(
            "cm",
            arguments,
            workspaceRoot,
            Timeout,
            MaximumOutputCharacters,
            cancellationToken,
            outputBudget);

        if (result.ExitCode != 0)
            throw ExternalFailure("cm status", result);

        var entries = new List<StatusEntry>();
        using var reader = new StringReader(result.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] fields = line.Split(FieldSeparator);
            if (fields.Length < 2)
                throw new ToolException($"Could not parse UVCS status line: {line}", ExitCodes.InternalFailure);

            string code = fields[0].Trim();
            if (code == "STATUS")
                continue;

            string status = GetStatusLabel(code);
            string fullPath = ResolveWorkspacePath(workspaceRoot, fields[1]);
            string relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
            bool isDirectory = fields.Length >= 3 && bool.TryParse(fields[2], out bool directory) && directory;
            entries.Add(new StatusEntry(relativePath, code, status, fullPath, isDirectory));
        }

        entries.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
        return entries;
    }

    public static string GetBaseCode(string code)
    {
        string[] parts = code.Split('+');
        if (parts.Length >= 2 && parts[0] == "CO" && Array.IndexOf(parts, "CH") >= 0)
            return "CH";
        return parts[0];
    }

    static string GetStatusLabel(string code) => GetBaseCode(code) switch
    {
        "CH" => "Changed",
        "AD" => "Added",
        "DE" => "Deleted",
        "LD" => "LocalDeleted",
        "MV" => "Moved",
        "LM" => "LocalMoved",
        "CO" => "Checkout",
        "PR" => "Private",
        _ => throw new ToolException($"Unknown UVCS status code '{code}'.", ExitCodes.InternalFailure)
    };

    static string ResolveWorkspacePath(string workspaceRoot, string rawPath)
    {
        string candidate = rawPath.Trim();
        if (!Path.IsPathRooted(candidate))
            candidate = Path.Combine(workspaceRoot, candidate);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException(
                $"UVCS returned an invalid item path: {exception.Message}",
                ExitCodes.InternalFailure,
                exception);
        }

        string relative = Path.GetRelativePath(workspaceRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new ToolException(
                $"UVCS returned a path outside the workspace: {fullPath}",
                ExitCodes.InternalFailure);

        return fullPath;
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
