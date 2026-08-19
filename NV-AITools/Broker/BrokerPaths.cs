using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace NVAITools.Broker;

static class BrokerPaths
{
    public const string CoordinationDirectoryName = ".nv-ai-tools";
    public const string RequestsDirectoryName = "requests";
    public const string ProcessingDirectoryName = "processing";
    public const string ResultsDirectoryName = "results";

    public static string ApplicationDirectory { get; } = AppContext.BaseDirectory;
    public static string ConfigurationPath { get; } = Path.Combine(ApplicationDirectory, "config.ini");
    public static string LogPath { get; } = Path.Combine(ApplicationDirectory, "broker.log");
    public static string PreviousLogPath { get; } = Path.Combine(ApplicationDirectory, "broker.log.old");

    public static string ExecutablePath => Environment.ProcessPath ??
        throw new InvalidOperationException("Could not determine the NV-AITools executable path.");

    public static string MutexName { get; } = BuildObjectName("Mutex");
    public static string StopEventName { get; } = BuildObjectName("Stop");
    public static string ReadyEventName { get; } = BuildObjectName("Ready");
    public static string StartupFailedEventName { get; } = BuildObjectName("StartupFailed");

    static string BuildObjectName(string kind)
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string installation = Path.GetFullPath(ApplicationDirectory).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(installation));
        string suffix = Convert.ToHexString(hash.AsSpan(0, 8));
        return $"Local\\NV-AITools.{kind}.{user}.{suffix}";
    }
}
