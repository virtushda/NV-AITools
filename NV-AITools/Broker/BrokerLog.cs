using System.Diagnostics;
using System.Text;

namespace NVAITools.Broker;

sealed class BrokerLog
{
    const long MaximumBytes = 5L * 1024 * 1024;
    readonly object gate = new();

    public BrokerLog()
    {
        using var stream = new FileStream(
            BrokerPaths.LogPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }

    public void Write(string message)
    {
        string cleanMessage = message.Replace("\r", string.Empty).Replace("\n", " | ");
        byte[] entry = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} {cleanMessage}{Environment.NewLine}");

        lock (gate)
        {
            try
            {
                RotateIfNeeded(entry.Length);
                using var stream = new FileStream(
                    BrokerPaths.LogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(entry);
                stream.Flush(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"NV-AITools logging failed: {exception.Message}");
            }
        }
    }

    static void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(BrokerPaths.LogPath) ||
            new FileInfo(BrokerPaths.LogPath).Length + incomingBytes <= MaximumBytes)
            return;

        File.Delete(BrokerPaths.PreviousLogPath);
        File.Move(BrokerPaths.LogPath, BrokerPaths.PreviousLogPath);
    }
}
