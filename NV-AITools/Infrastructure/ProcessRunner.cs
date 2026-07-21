using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UvcsTools.Infrastructure;

sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maximumOutputCharacters)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        for (int index = 0; index < arguments.Count; index++)
            startInfo.ArgumentList.Add(arguments[index]);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw new ToolException(
                $"Required executable '{executable}' is unavailable.",
                ExitCodes.DependencyOrWorkspaceFailure,
                exception);
        }
        catch (Exception exception)
        {
            throw new ToolException(
                $"Could not start '{executable}': {exception.Message}",
                ExitCodes.ExternalCommandFailure,
                exception);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw new ToolException(
                $"'{executable}' exceeded the {timeout.TotalSeconds:0}-second timeout.",
                ExitCodes.ExternalCommandFailure);
        }

        string[] output = await Task.WhenAll(stdoutTask, stderrTask);
        if ((long)output[0].Length + output[1].Length > maximumOutputCharacters)
            throw new ToolException(
                $"'{executable}' exceeded the configured output limit.",
                ExitCodes.ExternalCommandFailure);

        return new ProcessResult(process.ExitCode, output[0], output[1]);
    }

    static void Kill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
