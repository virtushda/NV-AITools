namespace NVAITools.Broker;

static class BrokerApplication
{
    public static int Run(string[] args)
    {
        if (args.Length != 1)
            return ExitCodes.InvalidArguments;

        using var mutex = new Mutex(true, BrokerPaths.MutexName, out bool createdNew);
        if (!createdNew)
            return ExitCodes.Success;

        try
        {
            using var stopEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                BrokerPaths.StopEventName);
            using var readyEvent = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                BrokerPaths.ReadyEventName);
            using var startupFailedEvent = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                BrokerPaths.StartupFailedEventName);
            var log = new BrokerLog();

            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            using var context = new BrokerApplicationContext(
                stopEvent,
                readyEvent,
                startupFailedEvent,
                log);
            System.Windows.Forms.Application.Run(context);
            return ExitCodes.Success;
        }
        catch (Exception exception)
        {
            try
            {
                new BrokerLog().Write($"Broker terminated unexpectedly: {exception.Message}");
            }
            catch
            {
            }
            return ExitCodes.InternalFailure;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
