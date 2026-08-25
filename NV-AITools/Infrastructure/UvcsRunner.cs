namespace NVAITools.Infrastructure;

delegate Task<ProcessResult> UvcsProcess(
    IReadOnlyList<string> arguments,
    string workingDirectory,
    TimeSpan timeout,
    int maximumOutputCharacters,
    CancellationToken cancellationToken,
    OutputBudget? outputBudget);

sealed class UvcsRunner : IDisposable
{
    internal static readonly Version MinimumVersion = new(11, 0, 16, 8411);

    readonly UvcsProcess execute;
    readonly SemaphoreSlim gate = new(1, 1);

    public UvcsRunner(ProcessRunner processes)
        : this((arguments, workingDirectory, timeout, maximumOutputCharacters, cancellationToken, outputBudget) =>
            processes.RunAsync(
                "cm",
                arguments,
                workingDirectory,
                timeout,
                maximumOutputCharacters,
                cancellationToken,
                outputBudget))
    {
    }

    internal UvcsRunner(UvcsProcess execute) => this.execute = execute;

    public async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maximumOutputCharacters,
        CancellationToken cancellationToken = default,
        OutputBudget? outputBudget = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await execute(
                arguments,
                workingDirectory,
                timeout,
                maximumOutputCharacters,
                cancellationToken,
                outputBudget);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Version> RequireMinimumVersionAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(
            ["version"],
            workingDirectory,
            TimeSpan.FromSeconds(30),
            32 * 1024,
            cancellationToken);
        if (result.ExitCode != 0)
            throw DependencyFailure(result);

        Version version = ParseVersion(result.StandardOutput);
        if (version < MinimumVersion)
            throw new ToolException(
                $"Unity Version Control {MinimumVersion} or newer is required. Installed version: {version}.",
                ExitCodes.DependencyOrWorkspaceFailure);
        return version;
    }

    internal static Version ParseVersion(string output)
    {
        string[] tokens = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index].Trim().TrimEnd(',', ';', '.');
            if (Version.TryParse(token, out Version? version) && version.Revision >= 0)
                return version;
        }

        throw new ToolException(
            $"Could not determine the Unity Version Control version from: {output.Trim()}",
            ExitCodes.DependencyOrWorkspaceFailure);
    }

    public void Dispose() => gate.Dispose();

    static ToolException DependencyFailure(ProcessResult result)
    {
        string diagnostics = result.StandardError.Trim();
        if (diagnostics.Length == 0)
            diagnostics = result.StandardOutput.Trim();
        string detail = diagnostics.Length == 0 ? string.Empty : $" {diagnostics}";
        return new ToolException(
            $"Could not read the Unity Version Control version.{detail}",
            ExitCodes.DependencyOrWorkspaceFailure);
    }
}
