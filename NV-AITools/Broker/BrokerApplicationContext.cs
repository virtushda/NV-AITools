using System.Diagnostics;
using System.Drawing;

namespace NVAITools.Broker;

sealed class BrokerApplicationContext : ApplicationContext
{
    readonly EventWaitHandle stopEvent;
    readonly EventWaitHandle readyEvent;
    readonly EventWaitHandle startupFailedEvent;
    readonly BrokerLog log;
    readonly BrokerHost host;
    readonly NotifyIcon trayIcon;
    readonly ToolStripMenuItem statusItem;
    readonly ToolStripMenuItem reloadItem;
    SynchronizationContext? uiContext;
    RegisteredWaitHandle? stopRegistration;
    bool exiting;

    public BrokerApplicationContext(
        EventWaitHandle stopEvent,
        EventWaitHandle readyEvent,
        EventWaitHandle startupFailedEvent,
        BrokerLog log)
    {
        this.stopEvent = stopEvent;
        this.readyEvent = readyEvent;
        this.startupFailedEvent = startupFailedEvent;
        this.log = log;
        host = new BrokerHost(log);
        host.StatusChanged += OnStatusChanged;

        statusItem = new ToolStripMenuItem("NV-AITools - Starting") { Enabled = false };
        reloadItem = new ToolStripMenuItem("Reload Configuration");
        reloadItem.Click += ReloadConfiguration;

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config.ini", null, (_, _) => OpenFile(BrokerPaths.ConfigurationPath));
        menu.Items.Add(reloadItem);
        menu.Items.Add("Open Log", null, (_, _) => OpenFile(BrokerPaths.LogPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => RequestExit());

        trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = Icon.ExtractAssociatedIcon(BrokerPaths.ExecutablePath) ?? SystemIcons.Application,
            Text = "NV-AITools - Starting",
            Visible = true
        };

        System.Windows.Forms.Application.Idle += StartBroker;
    }

    async void StartBroker(object? sender, EventArgs args)
    {
        System.Windows.Forms.Application.Idle -= StartBroker;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        stopRegistration = ThreadPool.RegisterWaitForSingleObject(
            stopEvent,
            (_, _) => uiContext.Post(_ => RequestExit(), null),
            null,
            Timeout.Infinite,
            true);

        SetStartupState(await host.StartAsync());
    }

    async void ReloadConfiguration(object? sender, EventArgs args)
    {
        reloadItem.Enabled = false;
        try
        {
            await host.ReloadAsync();
            SetStartupState(true);
            ShowNotification("Configuration reloaded", "Workspace monitoring has been updated.", ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            ShowNotification("Configuration error", exception.Message, ToolTipIcon.Error);
        }
        finally
        {
            if (!exiting)
                reloadItem.Enabled = true;
        }
    }

    void OnStatusChanged(BrokerStatus status)
    {
        uiContext?.Post(_ =>
        {
            string workspaceLabel = status.WorkspaceCount == 1 ? "1 workspace" : $"{status.WorkspaceCount} workspaces";
            statusItem.Text = $"NV-AITools - {status.Text} - {workspaceLabel}";
            trayIcon.Text = statusItem.Text.Length <= 63
                ? statusItem.Text
                : $"NV-AITools - {status.Text}";
            if (status.IsError)
                ShowNotification("NV-AITools needs attention", "Open the broker log for details.", ToolTipIcon.Warning);
        }, null);
    }

    void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            log.Write($"Could not open '{path}': {exception.Message}");
            ShowNotification("Could not open file", exception.Message, ToolTipIcon.Error);
        }
    }

    void ShowNotification(string title, string message, ToolTipIcon icon)
    {
        trayIcon.BalloonTipTitle = title;
        trayIcon.BalloonTipText = message;
        trayIcon.BalloonTipIcon = icon;
        trayIcon.ShowBalloonTip(5000);
    }

    void SetStartupState(bool ready)
    {
        if (ready)
        {
            startupFailedEvent.Reset();
            readyEvent.Set();
        }
        else
        {
            readyEvent.Reset();
            startupFailedEvent.Set();
        }
    }

    async void RequestExit()
    {
        if (exiting)
            return;

        exiting = true;
        reloadItem.Enabled = false;
        trayIcon.Visible = false;
        stopRegistration?.Unregister(null);
        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception)
        {
            log.Write($"Broker shutdown failed: {exception.Message}");
        }
        finally
        {
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            System.Windows.Forms.Application.Idle -= StartBroker;
            stopRegistration?.Unregister(null);
            trayIcon.Dispose();
            statusItem.Dispose();
            reloadItem.Dispose();
        }
        base.Dispose(disposing);
    }
}
