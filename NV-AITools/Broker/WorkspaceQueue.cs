using NVAITools.Infrastructure;

namespace NVAITools.Broker;

sealed class WorkspaceQueue : IDisposable
{
    readonly PinnedDirectory[]? pinnedDirectories;

    WorkspaceQueue(string workspaceRoot, PinnedDirectory[]? pinnedDirectories = null)
    {
        WorkspaceRoot = workspaceRoot;
        Root = Path.Combine(workspaceRoot, BrokerPaths.CoordinationDirectoryName);
        Requests = Path.Combine(Root, BrokerPaths.RequestsDirectoryName);
        Processing = Path.Combine(Root, BrokerPaths.ProcessingDirectoryName);
        Results = Path.Combine(Root, BrokerPaths.ResultsDirectoryName);
        this.pinnedDirectories = pinnedDirectories;
    }

    public string WorkspaceRoot { get; }
    public string Root { get; }
    public string Requests { get; }
    public string Processing { get; }
    public string Results { get; }

    public static WorkspaceQueue CreateForBroker(string workspaceRoot)
    {
        var queue = new WorkspaceQueue(workspaceRoot);
        var pins = new List<PinnedDirectory>(5);
        try
        {
            var workspace = new PinnedDirectory(queue.WorkspaceRoot);
            pins.Add(workspace);

            Directory.CreateDirectory(queue.Root);
            var root = new PinnedDirectory(
                queue.Root,
                Path.Combine(workspace.FinalPath, BrokerPaths.CoordinationDirectoryName));
            pins.Add(root);

            Directory.CreateDirectory(queue.Requests);
            pins.Add(new PinnedDirectory(
                queue.Requests,
                Path.Combine(root.FinalPath, BrokerPaths.RequestsDirectoryName)));

            Directory.CreateDirectory(queue.Processing);
            pins.Add(new PinnedDirectory(
                queue.Processing,
                Path.Combine(root.FinalPath, BrokerPaths.ProcessingDirectoryName)));

            Directory.CreateDirectory(queue.Results);
            pins.Add(new PinnedDirectory(
                queue.Results,
                Path.Combine(root.FinalPath, BrokerPaths.ResultsDirectoryName)));
            return new WorkspaceQueue(workspaceRoot, pins.ToArray());
        }
        catch
        {
            for (int index = pins.Count - 1; index >= 0; index--)
                pins[index].Dispose();
            throw;
        }
    }

    public static WorkspaceQueue DiscoverForClient(string requestedPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException(
                $"Invalid workspace path: {exception.Message}",
                ExitCodes.DependencyOrWorkspaceFailure,
                exception);
        }

        if (File.Exists(fullPath))
            fullPath = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(fullPath))
            throw new ToolException(
                $"Workspace path does not exist: {fullPath}",
                ExitCodes.DependencyOrWorkspaceFailure);

        var current = new DirectoryInfo(fullPath);
        while (current is not null)
        {
            var queue = new WorkspaceQueue(Path.TrimEndingDirectorySeparator(current.FullName));
            if (Directory.Exists(queue.Root))
            {
                if (!Directory.Exists(queue.Requests) ||
                    !Directory.Exists(queue.Processing) ||
                    !Directory.Exists(queue.Results))
                    throw new ToolException(
                        $"NV-AITools queue setup is incomplete under '{queue.Root}'. Restart or reload the broker.",
                        ExitCodes.DependencyOrWorkspaceFailure);
                queue.ValidateSafeDirectories();
                return queue;
            }
            current = current.Parent;
        }

        throw new ToolException(
            $"No configured NV-AITools workspace was found from '{requestedPath}'. Add the workspace root to config.ini and reload the broker.",
            ExitCodes.DependencyOrWorkspaceFailure);
    }

    public void Dispose()
    {
        if (pinnedDirectories is null)
            return;

        for (int index = pinnedDirectories.Length - 1; index >= 0; index--)
            pinnedDirectories[index].Dispose();
    }

    void ValidateSafeDirectories()
    {
        RejectReparsePoint(Root);
        RejectReparsePoint(Requests);
        RejectReparsePoint(Processing);
        RejectReparsePoint(Results);
    }

    static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ToolException(
                $"NV-AITools queue directory cannot be a filesystem reparse point: {path}",
                ExitCodes.DependencyOrWorkspaceFailure);
    }
}
