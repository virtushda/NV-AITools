using System.Text;
using UvcsTools.Cli;
using UvcsTools.Infrastructure;

namespace UvcsTools.Commands;

sealed class ChangesetDiffsCommand(ProcessRunner processes, WorkspaceResolver workspaces)
{
    const char FieldSeparator = '\u001f';
    const int MaximumMetadataCharacters = 64 * 1024 * 1024;
    const int MaximumPatchCharacters = 256 * 1024 * 1024;
    static readonly TimeSpan CmTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".asmdef", ".asmref", ".shader", ".hlsl", ".compute", ".cginc", ".uxml", ".uss"
    };

    public async Task<CommandOutcome> ExecuteAsync(ChangesetDiffsRequest request)
    {
        Console.Error.WriteLine("Resolving UVCS workspace...");
        string workspaceRoot = await workspaces.ResolveAsync(request.Workspace);
        await RequireGitAsync(workspaceRoot);

        Console.Error.WriteLine($"Finding changes from cs:{request.From} through cs:{request.To}...");
        List<string> candidates = await GetCandidatesAsync(request.From, request.To, workspaceRoot);
        if (candidates.Count == 0)
            return new CommandOutcome(string.Empty);

        using var temporary = new TempDirectory();
        string oldRoot = Path.Combine(temporary.Root, "a");
        string newRoot = Path.Combine(temporary.Root, "b");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);

        int exported = 0;
        for (int index = 0; index < candidates.Count; index++)
        {
            string path = candidates[index];
            Console.Error.WriteLine($"Exporting {path}...");
            string oldPath = GetSafeEndpointPath(oldRoot, path);
            string newPath = GetSafeEndpointPath(newRoot, path);
            bool hasOld = await TryExportAsync(path, request.From, oldPath, workspaceRoot);
            bool hasNew = await TryExportAsync(path, request.To, newPath, workspaceRoot);

            if (!hasOld && !hasNew)
                continue;

            if (await IsBinaryLikeAsync(hasOld ? oldPath : newPath) ||
                hasOld && hasNew && await IsBinaryLikeAsync(newPath))
            {
                Console.Error.WriteLine($"Skipping binary-like content: {path}");
                if (hasOld)
                    File.Delete(oldPath);
                if (hasNew)
                    File.Delete(newPath);
                continue;
            }

            exported++;
        }

        if (exported == 0)
            return new CommandOutcome(string.Empty);

        Console.Error.WriteLine("Generating unified patch...");
        ProcessResult diff = await processes.RunAsync(
            "git",
            BuildGitArguments(request),
            temporary.Root,
            GitTimeout,
            MaximumPatchCharacters);

        if (!string.IsNullOrWhiteSpace(diff.StandardError))
            Console.Error.Write(diff.StandardError);
        if (diff.ExitCode is not 0 and not 1)
            throw ExternalFailure("git diff --no-index", diff);

        return new CommandOutcome(diff.StandardOutput);
    }

    async Task RequireGitAsync(string workspaceRoot)
    {
        ProcessResult result = await processes.RunAsync(
            "git",
            ["--version"],
            workspaceRoot,
            TimeSpan.FromSeconds(30),
            32 * 1024);
        if (result.ExitCode != 0)
            throw new ToolException("Git is unavailable.", ExitCodes.DependencyOrWorkspaceFailure);
    }

    async Task<List<string>> GetCandidatesAsync(int from, int to, string workspaceRoot)
    {
        int lowerBound = from == 0 ? 0 : from - 1;
        string itemFormat = $"{{shortstatus}}{FieldSeparator}{{path}}{{newline}}";
        ProcessResult result = await processes.RunAsync(
            "cm",
            [
                "log",
                $"cs:{to}",
                $"--from=cs:{lowerBound}",
                "--csformat={items}",
                $"--itemformat={itemFormat}",
                "--repositorypaths"
            ],
            workspaceRoot,
            CmTimeout,
            MaximumMetadataCharacters);

        if (result.ExitCode != 0)
            throw ExternalFailure("cm log", result);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(result.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int separator = line.IndexOf(FieldSeparator);
            if (separator <= 0 || separator == line.Length - 1)
                throw new ToolException($"Could not parse UVCS log line: {line}", ExitCodes.InternalFailure);

            string status = line[..separator].Trim();
            if (status is not "A" and not "D" and not "M" and not "C")
                throw new ToolException($"Unknown UVCS log status '{status}'.", ExitCodes.InternalFailure);

            string path = NormalizeRepositoryPath(line[(separator + 1)..]);
            if (Extensions.Contains(Path.GetExtension(path)))
                candidates.Add(path);
        }

        var sorted = candidates.ToList();
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    async Task<bool> TryExportAsync(string repositoryPath, int changeset, string destination, string workspaceRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string revision = $"serverpath:/{repositoryPath}#cs:{changeset}";
        ProcessResult result = await processes.RunAsync(
            "cm",
            ["cat", revision, $"--file={destination}", "--raw"],
            workspaceRoot,
            CmTimeout,
            MaximumMetadataCharacters);

        if (result.ExitCode == 0)
        {
            if (!File.Exists(destination))
                throw new ToolException($"UVCS did not create the exported file for '{repositoryPath}'.", ExitCodes.InternalFailure);
            return true;
        }

        string diagnostics = $"{result.StandardError}\n{result.StandardOutput}";
        if (IsMissingRevision(diagnostics))
            return false;

        throw ExternalFailure("cm cat", result);
    }

    static IReadOnlyList<string> BuildGitArguments(ChangesetDiffsRequest request)
    {
        string algorithm = request.Algorithm switch
        {
            DiffAlgorithm.Histogram => "histogram",
            DiffAlgorithm.Patience => "patience",
            DiffAlgorithm.Default => "default",
            DiffAlgorithm.Minimal => "minimal",
            _ => throw new InvalidOperationException("Unknown diff algorithm.")
        };

        return
        [
            "-c", "core.autocrlf=false",
            "-c", "core.safecrlf=false",
            "diff",
            "--no-index",
            "--no-ext-diff",
            "--no-textconv",
            "--no-prefix",
            $"--diff-algorithm={algorithm}",
            request.FindRenames ? "--find-renames" : "--no-renames",
            "--",
            "a",
            "b"
        ];
    }

    static string NormalizeRepositoryPath(string value)
    {
        string path = value.Trim().Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path == ".." || path.StartsWith("../", StringComparison.Ordinal) ||
            path.Contains("/../", StringComparison.Ordinal) || path.Contains('\0'))
            throw new ToolException($"UVCS returned an unsafe repository path: {value}", ExitCodes.InternalFailure);

        return path;
    }

    static string GetSafeEndpointPath(string endpointRoot, string repositoryPath)
    {
        string combined = Path.Combine(endpointRoot, repositoryPath.Replace('/', Path.DirectorySeparatorChar));
        string fullPath = Path.GetFullPath(combined);
        string relative = Path.GetRelativePath(endpointRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new ToolException($"Repository path escaped the temporary endpoint tree: {repositoryPath}", ExitCodes.InternalFailure);
        return fullPath;
    }

    static bool IsMissingRevision(string diagnostics)
    {
        string[] indicators =
        [
            "not found", "cannot find", "couldn't find", "does not exist", "doesn't exist",
            "no revision", "not a file", "not exist"
        ];
        for (int index = 0; index < indicators.Length; index++)
        {
            if (diagnostics.Contains(indicators[index], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static async Task<bool> IsBinaryLikeAsync(string path)
    {
        byte[] buffer = new byte[16 * 1024];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (buffer.AsSpan(0, read).IndexOf((byte)0) >= 0)
                return true;
        }
        return false;
    }

    static ToolException ExternalFailure(string command, ProcessResult result)
    {
        string diagnostics = result.StandardError.Trim();
        if (diagnostics.Length == 0)
            diagnostics = result.StandardOutput.Trim();
        string message = diagnostics.Length == 0
            ? $"'{command}' failed with exit code {result.ExitCode}."
            : $"'{command}' failed with exit code {result.ExitCode}: {diagnostics}";
        return new ToolException(message, ExitCodes.ExternalCommandFailure);
    }
}
