using System.Text.Json;
using System.Text.Json.Serialization;
using NVAITools.Cli;

namespace NVAITools.Broker;

sealed record QueueRequest(
    int Protocol,
    string Id,
    string Tool,
    QueueArguments Arguments);

sealed record QueueArguments(
    int? From = null,
    int? To = null,
    string? Algorithm = null,
    bool? FindRenames = null,
    string?[]? FileNameFilters = null);

sealed record QueueResult(int Protocol, string Id, int ExitCode);

static class QueueProtocol
{
    public const int Version = 1;
    public const int MaximumRequestBytes = 16 * 1024;
    public const int MaximumResultBytes = 4 * 1024;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static byte[] SerializeRequest(CommandRequest request, string id)
    {
        QueueRequest queued = request switch
        {
            StatusRequest => new QueueRequest(Version, id, "status", new QueueArguments()),
            PendingChangesDiffsRequest pending => new QueueRequest(
                Version,
                id,
                "pending-changes-diffs",
                new QueueArguments(FileNameFilters:
                    pending.FileNameFilters.Count == 0 ? null : [.. pending.FileNameFilters])),
            ChangesetDiffsRequest changesets => new QueueRequest(
                Version,
                id,
                "changeset-diffs",
                new QueueArguments(
                    changesets.From,
                    changesets.To,
                    FormatAlgorithm(changesets.Algorithm),
                    changesets.FindRenames)),
            _ => throw new InvalidOperationException("Unknown command request type.")
        };

        return JsonSerializer.SerializeToUtf8Bytes(queued, JsonOptions);
    }

    public static CommandRequest ParseRequest(
        ReadOnlySpan<byte> json,
        string expectedId,
        string trustedWorkspaceRoot)
    {
        QueueRequest request;
        try
        {
            request = JsonSerializer.Deserialize<QueueRequest>(json, JsonOptions) ??
                throw Invalid("Request JSON is empty.");
        }
        catch (JsonException exception)
        {
            throw new ToolException(
                $"Invalid request JSON: {exception.Message}",
                ExitCodes.InvalidArguments,
                exception);
        }

        if (request.Protocol != Version)
            throw Invalid($"Unsupported request protocol '{request.Protocol}'.");
        if (request.Id != expectedId || !IsRequestId(request.Id))
            throw Invalid("Request ID does not match its filename.");
        if (request.Arguments is null)
            throw Invalid("Request arguments are required.");

        return request.Tool switch
        {
            "status" when HasNoArguments(request.Arguments) => new StatusRequest(trustedWorkspaceRoot),
            "pending-changes-diffs" => ParsePendingChangesDiffs(request.Arguments, trustedWorkspaceRoot),
            "changeset-diffs" => ParseChangesetDiffs(request.Arguments, trustedWorkspaceRoot),
            "status" =>
                throw Invalid($"Tool '{request.Tool}' does not accept arguments."),
            _ => throw Invalid($"Unknown tool '{request.Tool}'.")
        };
    }

    public static byte[] SerializeResult(string id, int exitCode) =>
        JsonSerializer.SerializeToUtf8Bytes(new QueueResult(Version, id, exitCode), JsonOptions);

    public static QueueResult ParseResult(ReadOnlySpan<byte> json, string expectedId)
    {
        QueueResult result;
        try
        {
            result = JsonSerializer.Deserialize<QueueResult>(json, JsonOptions) ??
                throw Invalid("Result JSON is empty.");
        }
        catch (JsonException exception)
        {
            throw new ToolException(
                $"Invalid broker result JSON: {exception.Message}",
                ExitCodes.InternalFailure,
                exception);
        }

        if (result.Protocol != Version || result.Id != expectedId || !IsRequestId(result.Id))
            throw new ToolException(
                "Broker result identity does not match the request.",
                ExitCodes.InternalFailure);
        if (result.ExitCode is not ExitCodes.Success and
            not ExitCodes.InvalidArguments and
            not ExitCodes.DependencyOrWorkspaceFailure and
            not ExitCodes.ExternalCommandFailure and
            not ExitCodes.InternalFailure)
            throw new ToolException(
                $"Broker returned unsupported exit code '{result.ExitCode}'.",
                ExitCodes.InternalFailure);

        return result;
    }

    public static bool IsRequestId(string? value)
    {
        if (value is null || value.Length != 32)
            return false;

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    static CommandRequest ParseChangesetDiffs(QueueArguments arguments, string workspaceRoot)
    {
        if (arguments.FileNameFilters is not null)
            throw Invalid("Tool 'changeset-diffs' does not accept fileNameFilters.");
        if (!arguments.From.HasValue || !arguments.To.HasValue ||
            arguments.Algorithm is null || !arguments.FindRenames.HasValue)
            throw Invalid("Changeset requests require from, to, algorithm, and findRenames.");
        if (arguments.From.Value < 0 || arguments.To.Value < 0 ||
            arguments.From.Value > arguments.To.Value)
            throw Invalid("Changeset values must be non-negative and from must not exceed to.");
        if (arguments.FindRenames.Value)
            throw Invalid("Rename detection is disabled because parallel per-file diffs cannot detect cross-file renames.");

        DiffAlgorithm algorithm = arguments.Algorithm switch
        {
            "histogram" => DiffAlgorithm.Histogram,
            "patience" => DiffAlgorithm.Patience,
            "default" => DiffAlgorithm.Default,
            "minimal" => DiffAlgorithm.Minimal,
            _ => throw Invalid($"Unsupported diff algorithm '{arguments.Algorithm}'.")
        };

        return new ChangesetDiffsRequest(
            workspaceRoot,
            arguments.From.Value,
            arguments.To.Value,
            algorithm,
            arguments.FindRenames.Value);
    }

    static CommandRequest ParsePendingChangesDiffs(QueueArguments arguments, string workspaceRoot)
    {
        if (arguments.From.HasValue || arguments.To.HasValue ||
            arguments.Algorithm is not null || arguments.FindRenames.HasValue)
        {
            throw Invalid("Tool 'pending-changes-diffs' accepts only fileNameFilters.");
        }

        return new PendingChangesDiffsRequest(workspaceRoot, arguments.FileNameFilters ?? []);
    }

    static bool HasNoArguments(QueueArguments arguments) =>
        !arguments.From.HasValue &&
        !arguments.To.HasValue &&
        arguments.Algorithm is null &&
        !arguments.FindRenames.HasValue &&
        arguments.FileNameFilters is null;

    static string FormatAlgorithm(DiffAlgorithm algorithm) => algorithm switch
    {
        DiffAlgorithm.Histogram => "histogram",
        DiffAlgorithm.Patience => "patience",
        DiffAlgorithm.Default => "default",
        DiffAlgorithm.Minimal => "minimal",
        _ => throw new InvalidOperationException("Unknown diff algorithm.")
    };

    static ToolException Invalid(string message) =>
        new(message, ExitCodes.InvalidArguments);
}
