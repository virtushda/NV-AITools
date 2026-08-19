using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NVAITools.Infrastructure;

sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

sealed class OutputBudget(long maximumCharacters, string exceededMessage)
{
    long reservedCharacters;
    int exceeded;

    public bool IsExceeded => Volatile.Read(ref exceeded) != 0;

    public void ThrowIfExceeded()
    {
        if (IsExceeded)
            throw LimitExceeded();
    }

    public void Reserve(int characters)
    {
        while (true)
        {
            long current = Volatile.Read(ref reservedCharacters);
            if (current > maximumCharacters - characters)
            {
                Interlocked.Exchange(ref exceeded, 1);
                throw LimitExceeded();
            }

            if (Interlocked.CompareExchange(
                    ref reservedCharacters,
                    current + characters,
                    current) == current)
                return;
        }
    }

    ToolException LimitExceeded() => new(exceededMessage, ExitCodes.ExternalCommandFailure);
}

sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maximumOutputCharacters,
        CancellationToken cancellationToken = default,
        OutputBudget? outputBudget = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token,
            cancellationToken);
        var processBudget = new OutputBudget(
            maximumOutputCharacters,
            $"'{executable}' exceeded the configured output limit.");
        var outputFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> stdoutTask = ReadOutputAsync(
            process.StandardOutput,
            processBudget,
            outputBudget,
            outputFailure,
            linkedSource.Token);
        Task<string> stderrTask = ReadOutputAsync(
            process.StandardError,
            processBudget,
            outputBudget,
            outputFailure,
            linkedSource.Token);
        Task exitTask = process.WaitForExitAsync(linkedSource.Token);

        try
        {
            Task completed = await Task.WhenAny(exitTask, outputFailure.Task);
            if (completed == outputFailure.Task)
                throw await outputFailure.Task;

            await exitTask;
            string[] output = await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessResult(process.ExitCode, output[0], output[1]);
        }
        catch (OperationCanceledException)
        {
            linkedSource.Cancel();
            Kill(process);
            await ObserveAsync(exitTask, stdoutTask, stderrTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
                throw new ToolException(
                    $"'{executable}' exceeded the {timeout.TotalSeconds:0}-second timeout.",
                    ExitCodes.ExternalCommandFailure);
            throw;
        }
        catch
        {
            linkedSource.Cancel();
            Kill(process);
            await ObserveAsync(exitTask, stdoutTask, stderrTask);
            throw;
        }
    }

    static async Task<string> ReadOutputAsync(
        StreamReader reader,
        OutputBudget processBudget,
        OutputBudget? commandBudget,
        TaskCompletionSource<Exception> outputFailure,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 4096;
        char[] buffer = ArrayPool<char>.Shared.Rent(BufferSize);
        var output = new StringBuilder();
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0)
                    return output.ToString();

                processBudget.Reserve(read);
                commandBudget?.Reserve(read);
                output.Append(buffer, 0, read);
            }
        }
        catch (Exception exception)
        {
            outputFailure.TrySetResult(exception);
            throw;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    static async Task ObserveAsync(Task exitTask, Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(exitTask, stdoutTask, stderrTask);
        }
        catch
        {
        }
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
