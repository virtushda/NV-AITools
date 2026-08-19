using System.Text.Json;
using NVAITools.Cli;
using NVAITools.Infrastructure;
using NVAITools.Models;

namespace NVAITools.Commands;

sealed class StatusCommand(
    ProcessRunner processes,
    WorkspaceResolver workspaces,
    TextWriter diagnostics)
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<CommandOutcome> ExecuteAsync(
        StatusRequest request,
        CancellationToken cancellationToken = default)
    {
        await diagnostics.WriteLineAsync("Resolving UVCS workspace...");
        string workspaceRoot = await workspaces.ResolveAsync(request.Workspace, cancellationToken);
        await diagnostics.WriteLineAsync("Reading workspace status...");
        List<StatusEntry> entries = await new StatusReader(processes).ReadAsync(
            workspaceRoot,
            cancellationToken);

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < entries.Count; index++)
        {
            string code = entries[index].StatusCode;
            counts.TryGetValue(code, out int count);
            counts[code] = count + 1;
        }

        var result = new StatusResult(1, workspaceRoot, entries, counts);
        return new CommandOutcome(JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine);
    }
}
