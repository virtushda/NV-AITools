using System.Globalization;
using System.IO.Enumeration;

namespace NVAITools.Cli;

abstract record CommandRequest(string Workspace);

sealed record StatusRequest(string Workspace) : CommandRequest(Workspace);

sealed record PendingChangesDiffsRequest : CommandRequest
{
    const int MaximumFilterCount = 32;
    const int MaximumFilterLength = 255;
    const int MaximumTotalFilterCharacters = 1024;
    static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

    public PendingChangesDiffsRequest(string workspace, IReadOnlyList<string?> fileNameFilters)
        : base(workspace)
    {
        if (fileNameFilters.Count > MaximumFilterCount)
            throw Invalid($"A maximum of {MaximumFilterCount} file filters is supported.");

        var validatedFilters = new string[fileNameFilters.Count];
        int totalCharacters = 0;
        for (int index = 0; index < fileNameFilters.Count; index++)
        {
            string? filter = fileNameFilters[index];
            if (string.IsNullOrWhiteSpace(filter))
                throw Invalid("File filters cannot be empty or whitespace.");
            if (filter.Length > MaximumFilterLength)
                throw Invalid($"File filters cannot exceed {MaximumFilterLength} characters.");

            for (int characterIndex = 0; characterIndex < filter.Length; characterIndex++)
            {
                char character = filter[characterIndex];
                if (character is not '*' and not '?' &&
                    Array.IndexOf(InvalidFileNameCharacters, character) >= 0)
                {
                    throw Invalid($"File filter '{filter}' contains an invalid filename character.");
                }
            }

            totalCharacters += filter.Length;
            if (totalCharacters > MaximumTotalFilterCharacters)
                throw Invalid($"File filters cannot exceed {MaximumTotalFilterCharacters} total characters.");

            validatedFilters[index] = filter;
        }

        FileNameFilters = validatedFilters;
    }

    public IReadOnlyList<string> FileNameFilters { get; }

    public bool MatchesFileName(string relativePath)
    {
        if (FileNameFilters.Count == 0)
            return true;

        ReadOnlySpan<char> fileName = Path.GetFileName(relativePath.AsSpan());
        for (int index = 0; index < FileNameFilters.Count; index++)
        {
            if (FileSystemName.MatchesSimpleExpression(
                FileNameFilters[index].AsSpan(),
                fileName,
                ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    static ToolException Invalid(string message) => new(message, ExitCodes.InvalidArguments);
}

sealed record ChangesetDiffsRequest(
    string Workspace,
    int From,
    int To,
    DiffAlgorithm Algorithm,
    bool FindRenames) : CommandRequest(Workspace);

enum DiffAlgorithm
{
    Histogram,
    Patience,
    Default,
    Minimal
}

static class CommandLine
{
    const string Usage = "Usage:\n" +
        "  NV-AITools.exe status [--workspace <path>]\n" +
        "  NV-AITools.exe pending-changes-diffs [--workspace <path>] [--file-filter <glob>]...\n" +
        "  NV-AITools.exe changeset-diffs --from <changeset> --to <changeset> " +
        "[--workspace <path>] [--algorithm histogram|patience|default|minimal]";

    public static CommandRequest Parse(string[] args)
    {
        if (args.Length == 0)
            throw Invalid("A command is required.");

        return args[0] switch
        {
            "status" => new StatusRequest(ParseWorkspaceOnly(args)),
            "pending-changes-diffs" => ParsePendingChangesDiffs(args),
            "changeset-diffs" => ParseChangesetDiffs(args),
            _ => throw Invalid($"Unknown command '{args[0]}'.")
        };
    }

    static string ParseWorkspaceOnly(string[] args)
    {
        string? workspace = null;
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (option != "--workspace")
                throw Invalid($"Unknown option '{option}'.");
            if (workspace is not null)
                throw Invalid("Duplicate option '--workspace'.");

            workspace = ReadValue(args, ref index, option);
        }

        return ResolveInputPath(workspace);
    }

    static PendingChangesDiffsRequest ParsePendingChangesDiffs(string[] args)
    {
        string? workspace = null;
        var fileNameFilters = new List<string>();
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--workspace":
                    RejectDuplicate(workspace is not null, option);
                    workspace = ReadValue(args, ref index, option);
                    break;
                case "--file-filter":
                    fileNameFilters.Add(ReadValue(args, ref index, option));
                    break;
                default:
                    throw Invalid($"Unknown option '{option}'.");
            }
        }

        return new PendingChangesDiffsRequest(ResolveInputPath(workspace), fileNameFilters);
    }

    static ChangesetDiffsRequest ParseChangesetDiffs(string[] args)
    {
        string? workspace = null;
        int? from = null;
        int? to = null;
        DiffAlgorithm algorithm = DiffAlgorithm.Histogram;
        bool algorithmSet = false;

        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--workspace":
                    RejectDuplicate(workspace is not null, option);
                    workspace = ReadValue(args, ref index, option);
                    break;
                case "--from":
                    RejectDuplicate(from.HasValue, option);
                    from = ParseChangeset(ReadValue(args, ref index, option), option);
                    break;
                case "--to":
                    RejectDuplicate(to.HasValue, option);
                    to = ParseChangeset(ReadValue(args, ref index, option), option);
                    break;
                case "--algorithm":
                    RejectDuplicate(algorithmSet, option);
                    algorithm = ParseAlgorithm(ReadValue(args, ref index, option));
                    algorithmSet = true;
                    break;
                case "--find-renames":
                    throw Invalid("Option '--find-renames' is disabled because parallel per-file diffs cannot detect cross-file renames.");
                default:
                    throw Invalid($"Unknown option '{option}'.");
            }
        }

        if (!from.HasValue || !to.HasValue)
            throw Invalid("Both '--from' and '--to' are required.");
        if (from.Value > to.Value)
            throw Invalid("'--from' must be less than or equal to '--to'.");

        return new ChangesetDiffsRequest(
            ResolveInputPath(workspace),
            from.Value,
            to.Value,
            algorithm,
            false);
    }

    static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw Invalid($"Option '{option}' requires a value.");

        return args[index];
    }

    static int ParseChangeset(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int changeset))
            throw Invalid($"Option '{option}' requires a non-negative integer changeset.");

        return changeset;
    }

    static DiffAlgorithm ParseAlgorithm(string value) => value switch
    {
        "histogram" => DiffAlgorithm.Histogram,
        "patience" => DiffAlgorithm.Patience,
        "default" => DiffAlgorithm.Default,
        "minimal" => DiffAlgorithm.Minimal,
        _ => throw Invalid($"Unsupported diff algorithm '{value}'.")
    };

    static string ResolveInputPath(string? workspace)
    {
        try
        {
            return Path.GetFullPath(workspace ?? Environment.CurrentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid($"Invalid workspace path: {exception.Message}");
        }
    }

    static void RejectDuplicate(bool duplicate, string option)
    {
        if (duplicate)
            throw Invalid($"Duplicate option '{option}'.");
    }

    static ToolException Invalid(string message) =>
        new($"{message}{Environment.NewLine}{Usage}", ExitCodes.InvalidArguments);
}
