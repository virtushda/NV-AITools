using UvcsTools.Cli;
using UvcsTools.Commands;
using UvcsTools.Infrastructure;

namespace UvcsTools;

static class Application
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            CommandRequest request = CommandLine.Parse(args);
            var processes = new ProcessRunner();
            var workspaces = new WorkspaceResolver(processes);

            CommandOutcome outcome = request switch
            {
                StatusRequest status => await new StatusCommand(processes, workspaces).ExecuteAsync(status),
                PendingChangesDiffsRequest pending => await new PendingChangesDiffsCommand(processes, workspaces).ExecuteAsync(pending),
                ChangesetDiffsRequest changesets => await new ChangesetDiffsCommand(processes, workspaces).ExecuteAsync(changesets),
                _ => throw new InvalidOperationException("Unknown command request type.")
            };

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

readonly record struct CommandOutcome(string Output, int ExitCode = ExitCodes.Success);
