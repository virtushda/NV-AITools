using NVAITools.Cli;
using NVAITools.Commands;
using NVAITools.Infrastructure;

namespace NVAITools.Broker;

sealed class CommandDispatcher
{
    public async Task<CommandOutcome> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        using var diagnostics = new StringWriter();
        try
        {
            var processes = new ProcessRunner();
            var workspaces = new WorkspaceResolver(processes);
            CommandOutcome outcome = request switch
            {
                StatusRequest status => await new StatusCommand(processes, workspaces, diagnostics)
                    .ExecuteAsync(status, cancellationToken),
                PendingChangesDiffsRequest pending => await new PendingChangesDiffsCommand(processes, workspaces, diagnostics)
                    .ExecuteAsync(pending, cancellationToken),
                ChangesetDiffsRequest changesets => await new ChangesetDiffsCommand(processes, workspaces, diagnostics)
                    .ExecuteAsync(changesets, cancellationToken),
                _ => throw new InvalidOperationException("Unknown command request type.")
            };

            return outcome with { Diagnostics = diagnostics.ToString() };
        }
        catch (OperationCanceledException)
        {
            diagnostics.WriteLine("Broker stopped before the command completed.");
            return new CommandOutcome(
                string.Empty,
                ExitCodes.ExternalCommandFailure,
                diagnostics.ToString());
        }
        catch (ToolException exception)
        {
            diagnostics.WriteLine(exception.Message);
            return new CommandOutcome(
                string.Empty,
                exception.ExitCode,
                diagnostics.ToString());
        }
        catch (Exception exception)
        {
            diagnostics.WriteLine($"Internal error: {exception.Message}");
            return new CommandOutcome(
                string.Empty,
                ExitCodes.InternalFailure,
                diagnostics.ToString());
        }
    }
}
