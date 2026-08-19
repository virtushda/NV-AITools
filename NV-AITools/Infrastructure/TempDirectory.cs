namespace NVAITools.Infrastructure;

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), $"NV-AITools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
        catch (Exception exception)
        {
            throw new ToolException(
                $"Could not remove temporary directory '{Root}': {exception.Message}",
                ExitCodes.InternalFailure,
                exception);
        }
    }
}
