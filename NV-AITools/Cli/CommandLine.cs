using System.Globalization;

namespace UvcsTools.Cli;

abstract record CommandRequest(string Workspace);

sealed record StatusRequest(string Workspace) : CommandRequest(Workspace);

sealed record PendingChangesDiffsRequest(string Workspace) : CommandRequest(Workspace);

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
        "  UvcsTools.exe status [--workspace <path>]\n" +
        "  UvcsTools.exe pending-changes-diffs [--workspace <path>]\n" +
        "  UvcsTools.exe changeset-diffs --from <changeset> --to <changeset> " +
        "[--workspace <path>] [--algorithm histogram|patience|default|minimal] [--find-renames]";

    public static CommandRequest Parse(string[] args)
    {
        if (args.Length == 0)
            throw Invalid("A command is required.");

        return args[0] switch
        {
            "status" => new StatusRequest(ParseWorkspaceOnly(args)),
            "pending-changes-diffs" => new PendingChangesDiffsRequest(ParseWorkspaceOnly(args)),
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

    static ChangesetDiffsRequest ParseChangesetDiffs(string[] args)
    {
        string? workspace = null;
        int? from = null;
        int? to = null;
        DiffAlgorithm algorithm = DiffAlgorithm.Histogram;
        bool algorithmSet = false;
        bool findRenames = false;

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
                    RejectDuplicate(findRenames, option);
                    findRenames = true;
                    break;
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
            findRenames);
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
