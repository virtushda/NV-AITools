using System.Diagnostics;

namespace NVAITools.Broker;

static class BrokerProcess
{
    static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> StartAsync()
    {
        if (IsReady())
            return ExitCodes.Success;
        if (StartupFailed())
            throw StartupFailure();

        Process? process = null;
        if (!IsRunning())
        {
            var startInfo = new ProcessStartInfo(BrokerPaths.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = BrokerPaths.ApplicationDirectory
            };
            startInfo.ArgumentList.Add("broker-run");
            process = Process.Start(startInfo) ??
                throw new ToolException("Could not start the NV-AITools broker.", ExitCodes.InternalFailure);
        }

        using (process)
        {
            DateTime deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (IsReady())
                    return ExitCodes.Success;
                if (StartupFailed())
                    throw StartupFailure();
                if (process is not null && process.HasExited && !IsRunning())
                    throw new ToolException(
                        $"NV-AITools broker exited during startup with code {process.ExitCode}.",
                        ExitCodes.DependencyOrWorkspaceFailure);
                if (process is null && !IsRunning())
                    throw new ToolException(
                        "NV-AITools broker exited during startup.",
                        ExitCodes.DependencyOrWorkspaceFailure);
                await Task.Delay(50);
            }
        }

        throw new ToolException(
            "NV-AITools broker did not finish startup within five minutes.",
            ExitCodes.DependencyOrWorkspaceFailure);
    }

    public static async Task<int> StopAsync()
    {
        if (!EventWaitHandle.TryOpenExisting(BrokerPaths.StopEventName, out EventWaitHandle? stopEvent))
            return ExitCodes.Success;

        using (stopEvent)
            stopEvent.Set();

        DateTime deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning())
                return ExitCodes.Success;
            await Task.Delay(100);
        }

        throw new ToolException(
            "NV-AITools broker did not stop within fifteen seconds.",
            ExitCodes.ExternalCommandFailure);
    }

    static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(BrokerPaths.MutexName, out Mutex? mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    static bool IsReady()
    {
        return IsRunning() && IsSignaled(BrokerPaths.ReadyEventName);
    }

    static bool StartupFailed()
    {
        return IsRunning() && IsSignaled(BrokerPaths.StartupFailedEventName);
    }

    static bool IsSignaled(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? signal))
            return false;
        using (signal)
            return signal.WaitOne(0);
    }

    static ToolException StartupFailure() => new(
        $"NV-AITools broker started but could not load its configuration. Review '{BrokerPaths.LogPath}' and reload config.ini from the tray.",
        ExitCodes.DependencyOrWorkspaceFailure);
}
