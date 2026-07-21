using System.Text.Json;
using UvcsTools.Cli;
using UvcsTools.Infrastructure;
using UvcsTools.Models;

namespace UvcsTools.Commands;

sealed class StatusCommand(ProcessRunner processes, WorkspaceResolver workspaces)
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<CommandOutcome> ExecuteAsync(StatusRequest request)
    {
        Console.Error.WriteLine("Resolving UVCS workspace...");
        string workspaceRoot = await workspaces.ResolveAsync(request.Workspace);
        Console.Error.WriteLine("Reading workspace status...");
        List<StatusEntry> entries = await new StatusReader(processes).ReadAsync(workspaceRoot);

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
