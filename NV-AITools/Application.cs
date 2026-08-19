using NVAITools.Cli;
using NVAITools.Broker;

namespace NVAITools;

static class Application
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "broker-start")
                return await BrokerProcess.StartAsync();
            if (args.Length == 1 && args[0] == "broker-stop")
                return await BrokerProcess.StopAsync();

            CommandRequest request = CommandLine.Parse(args);
            CommandOutcome outcome = await QueueClient.ExecuteAsync(request);

            if (outcome.Diagnostics.Length > 0)
                await Console.Error.WriteAsync(outcome.Diagnostics);
            await Console.Out.WriteAsync(outcome.Output);
            return outcome.ExitCode;
        }
        catch (ToolException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Internal error: {exception.Message}");
            return ExitCodes.InternalFailure;
        }
    }
}

static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int DependencyOrWorkspaceFailure = 3;
    public const int ExternalCommandFailure = 4;
    public const int InternalFailure = 5;
}

sealed class ToolException(string message, int exitCode, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int ExitCode { get; } = exitCode;
}

readonly record struct CommandOutcome(
    string Output,
    int ExitCode = ExitCodes.Success,
    string Diagnostics = "");
