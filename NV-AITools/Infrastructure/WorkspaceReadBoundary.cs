using System.ComponentModel;

namespace NVAITools.Infrastructure;

sealed class WorkspaceReadBoundary : IDisposable
{
    readonly PinnedDirectory workspace;
    readonly string finalWorkspacePath;

    public WorkspaceReadBoundary(string workspacePath)
    {
        workspace = new PinnedDirectory(workspacePath);
        finalWorkspacePath = workspace.FinalPath;
    }

    public FileStream OpenRead(string path, int bufferSize)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ValidationFailure(path, exception);
        }

        try
        {
            string finalPath = PinnedDirectory.GetFinalPath(stream.SafeFileHandle);
            if (!IsWithinWorkspace(finalPath))
                throw new ToolException(
                    $"Refusing to read '{path}' because it resolves outside the configured workspace.",
                    ExitCodes.DependencyOrWorkspaceFailure);
            return stream;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception)
        {
            stream.Dispose();
            throw ValidationFailure(path, exception);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => workspace.Dispose();

    bool IsWithinWorkspace(string path) =>
        path.StartsWith(finalWorkspacePath, StringComparison.OrdinalIgnoreCase) &&
        (Path.EndsInDirectorySeparator(finalWorkspacePath) ||
         path.Length > finalWorkspacePath.Length &&
         path[finalWorkspacePath.Length] is '\\' or '/');

    static ToolException ValidationFailure(string path, Exception exception) => new(
        $"Could not validate workspace file '{path}': {exception.Message}",
        ExitCodes.DependencyOrWorkspaceFailure,
        exception);
}
