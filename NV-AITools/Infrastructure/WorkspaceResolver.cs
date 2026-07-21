namespace UvcsTools.Infrastructure;

sealed class WorkspaceResolver(ProcessRunner processes)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<string> ResolveAsync(string requestedPath)
    {
        if (!Directory.Exists(requestedPath))
            throw new ToolException(
                $"Workspace path does not exist: {requestedPath}",
                ExitCodes.DependencyOrWorkspaceFailure);

        ProcessResult result = await processes.RunAsync(
            "cm",
            ["getworkspacefrompath", requestedPath, "--format={wkpath}"],
            requestedPath,
            Timeout,
            32 * 1024);

        if (result.ExitCode != 0)
            throw new ToolException(
                $"Could not resolve a UVCS workspace from '{requestedPath}'.{FormatDiagnostics(result.StandardError)}",
                ExitCodes.DependencyOrWorkspaceFailure);

        string value = result.StandardOutput.Trim();
        if (value.Length == 0)
            throw new ToolException("UVCS returned an empty workspace path.", ExitCodes.InternalFailure);

        string workspaceRoot;
        try
        {
            workspaceRoot = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException(
                $"UVCS returned an invalid workspace path: {exception.Message}",
                ExitCodes.InternalFailure,
                exception);
        }

        if (!Directory.Exists(workspaceRoot))
            throw new ToolException(
                $"Resolved UVCS workspace does not exist: {workspaceRoot}",
                ExitCodes.DependencyOrWorkspaceFailure);

        return Path.TrimEndingDirectorySeparator(workspaceRoot);
    }

    static string FormatDiagnostics(string diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : $" {diagnostics.Trim()}";
}
