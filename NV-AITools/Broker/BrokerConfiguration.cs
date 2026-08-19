namespace NVAITools.Broker;

sealed record BrokerConfiguration(IReadOnlyList<string> WorkspaceRoots)
{
    const string DefaultContents =
        "; Add absolute UVCS workspace roots below, then use Reload Configuration from the tray.\r\n" +
        "[Workspaces]\r\n" +
        "; Path1=X:\\Path\\To\\Workspace\r\n";

    public static void EnsureDefaultExists(string path)
    {
        if (File.Exists(path))
            return;

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.Write(DefaultContents);
    }

    public static BrokerConfiguration Load(string path)
    {
        string[] lines = File.ReadAllLines(path);
        var workspaces = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inWorkspaces = false;
        bool foundWorkspaces = false;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                    throw Invalid(index, "Section header is missing its closing bracket.");

                string section = line[1..^1].Trim();
                if (!section.Equals("Workspaces", StringComparison.OrdinalIgnoreCase))
                    throw Invalid(index, $"Unsupported section '[{section}]'.");
                if (foundWorkspaces)
                    throw Invalid(index, "Duplicate [Workspaces] section.");
                foundWorkspaces = true;
                inWorkspaces = true;
                continue;
            }

            if (!inWorkspaces)
                throw Invalid(index, "Workspace paths must appear under [Workspaces].");

            int equals = line.IndexOf('=');
            if (equals <= 0)
                throw Invalid(index, "Expected a numbered Path entry such as Path1=C:\\Workspace.");

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (!IsPathKey(key))
                throw Invalid(index, $"Unsupported key '{key}'. Use numbered keys such as Path1.");
            if (!keys.Add(key))
                throw Invalid(index, $"Duplicate key '{key}'.");
            if (value.Length == 0)
                throw Invalid(index, $"'{key}' requires an absolute workspace path.");
            if (!Path.IsPathFullyQualified(value))
                throw Invalid(index, $"'{key}' must use an absolute path.");

            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw Invalid(index, $"'{key}' contains an invalid path: {exception.Message}");
            }

            if (!Directory.Exists(fullPath))
                throw Invalid(index, $"Workspace directory does not exist: {fullPath}");
            if (!paths.Add(fullPath))
                throw Invalid(index, $"Duplicate workspace path: {fullPath}");

            workspaces.Add(fullPath);
        }

        return new BrokerConfiguration(workspaces);
    }

    static bool IsPathKey(string key)
    {
        if (!key.StartsWith("Path", StringComparison.OrdinalIgnoreCase) || key.Length == 4)
            return false;

        for (int index = 4; index < key.Length; index++)
        {
            if (!char.IsAsciiDigit(key[index]))
                return false;
        }
        return key[4] != '0';
    }

    static ToolException Invalid(int zeroBasedLine, string message) =>
        new($"Invalid config.ini line {zeroBasedLine + 1}: {message}", ExitCodes.DependencyOrWorkspaceFailure);
}
