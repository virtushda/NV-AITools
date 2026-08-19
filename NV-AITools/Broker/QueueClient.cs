using System.Diagnostics;
using System.Security.Cryptography;
using NVAITools.Cli;

namespace NVAITools.Broker;

static class QueueClient
{
    static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan CompletionTimeout = TimeSpan.FromHours(12);

    public static async Task<CommandOutcome> ExecuteAsync(CommandRequest request)
    {
        WorkspaceQueue queue = WorkspaceQueue.DiscoverForClient(request.Workspace);
        PublishedRequest published = await PublishAsync(queue, request);
        string resultPath = Path.Combine(queue.Results, $"{published.Id}.result");

        if (!await WaitForClaimAsync(
            published.RequestPath,
            published.ProcessingPath,
            resultPath))
            throw new ToolException(
                $"NV-AITools broker did not claim the request. The broker is not running or '{queue.WorkspaceRoot}' is not configured.",
                ExitCodes.DependencyOrWorkspaceFailure);

        if (!await WaitForFileAsync(resultPath, CompletionTimeout, TimeSpan.FromMilliseconds(250)))
            throw new ToolException(
                $"NV-AITools broker did not complete request {published.Id} within {CompletionTimeout.TotalHours:0} hours.",
                ExitCodes.ExternalCommandFailure);

        string stdoutPath = Path.Combine(queue.Results, $"{published.Id}.stdout");
        string stderrPath = Path.Combine(queue.Results, $"{published.Id}.stderr");
        if (!File.Exists(stdoutPath) || !File.Exists(stderrPath))
            throw new ToolException(
                $"Broker result {published.Id} is incomplete.",
                ExitCodes.InternalFailure);

        byte[] resultBytes = await ReadLimitedAsync(resultPath, QueueProtocol.MaximumResultBytes);
        QueueResult result = QueueProtocol.ParseResult(resultBytes, published.Id);
        string output = await File.ReadAllTextAsync(stdoutPath);
        string diagnostics = await File.ReadAllTextAsync(stderrPath);
        string cleanupDiagnostic = Cleanup(published, stdoutPath, stderrPath, resultPath);
        if (cleanupDiagnostic.Length > 0)
            diagnostics += cleanupDiagnostic;

        return new CommandOutcome(output, result.ExitCode, diagnostics);
    }

    static async Task<PublishedRequest> PublishAsync(
        WorkspaceQueue queue,
        CommandRequest request)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            string temporaryPath = Path.Combine(queue.Requests, $"{id}.tmp");
            string requestPath = Path.Combine(queue.Requests, $"{id}.request");
            string processingPath = Path.Combine(queue.Processing, $"{id}.request");
            byte[] payload = QueueProtocol.SerializeRequest(request, id);

            FileStream stream;
            try
            {
                stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(temporaryPath))
            {
                continue;
            }

            await using (stream)
            {
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                stream.Flush(true);
            }

            try
            {
                File.Move(temporaryPath, requestPath);
                return new PublishedRequest(id, requestPath, processingPath);
            }
            catch (IOException) when (File.Exists(requestPath))
            {
                File.Delete(temporaryPath);
            }
        }

        throw new ToolException(
            "Could not allocate a unique NV-AITools request ID.",
            ExitCodes.InternalFailure);
    }

    static async Task<bool> WaitForClaimAsync(
        string requestPath,
        string processingPath,
        string resultPath)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ClaimTimeout)
        {
            if (File.Exists(processingPath) || File.Exists(resultPath))
                return true;
            if (!File.Exists(requestPath))
            {
                await Task.Delay(50);
                return File.Exists(processingPath) || File.Exists(resultPath);
            }
            await Task.Delay(50);
        }

        try
        {
            File.Delete(requestPath);
            return File.Exists(processingPath) || File.Exists(resultPath);
        }
        catch (FileNotFoundException)
        {
            await Task.Delay(50);
            return File.Exists(processingPath) || File.Exists(resultPath);
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    static async Task<bool> WaitForFileAsync(
        string path,
        TimeSpan timeout,
        TimeSpan interval)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (File.Exists(path))
                return true;
            await Task.Delay(interval);
        }
        return File.Exists(path);
    }

    static async Task<byte[]> ReadLimitedAsync(string path, int maximumBytes)
    {
        byte[] buffer = new byte[maximumBytes + 1];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            true);
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
                return buffer[..total];
            total += read;
        }
        throw new ToolException(
            $"Broker metadata exceeded the {maximumBytes}-byte limit.",
            ExitCodes.InternalFailure);
    }

    static string Cleanup(
        PublishedRequest published,
        string stdoutPath,
        string stderrPath,
        string resultPath)
    {
        string[] paths =
        [
            published.RequestPath,
            published.ProcessingPath,
            stdoutPath,
            stderrPath,
            resultPath
        ];

        var diagnostics = new System.Text.StringBuilder();
        for (int index = 0; index < paths.Length; index++)
        {
            try
            {
                File.Delete(paths[index]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.AppendLine($"Warning: Could not remove queue file '{paths[index]}': {exception.Message}");
            }
        }
        return diagnostics.ToString();
    }

    readonly record struct PublishedRequest(
        string Id,
        string RequestPath,
        string ProcessingPath);
}
