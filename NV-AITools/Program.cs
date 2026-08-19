using NVAITools;
using NVAITools.Broker;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "broker-run")
            return BrokerApplication.Run(args);

        return NVAITools.Application.RunAsync(args).GetAwaiter().GetResult();
    }
}
