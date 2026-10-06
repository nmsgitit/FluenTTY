using System.Windows;
using System.Windows.Threading;
using FluenTTY.Services;
using FluenTTY.Views;

namespace FluenTTY;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Diagnostics.Log($"fatal: {ex.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, ex) => { Diagnostics.Log($"unobserved: {ex.Exception}"); ex.SetObserved(); };
        // Errors inside hosted WinForms/ActiveX controls (the RDP client) come here instead of WinForms' own dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, ex) => Dispatcher.BeginInvoke(() => ShowError(ex.Exception));

        // A portable copy that just updated itself: wait until the old process has saved and exited
        // before touching the data folder or the single-instance mutex.
        var args = Updater.HandleRelaunch(e.Args);

        SettingsStore.Load();

        if (SettingsStore.Settings.Interface.SingleInstance)
        {
            _instance = new SingleInstance();
            if (!_instance.IsFirstInstance && SingleInstance.SendToExisting(args))
            {
                Shutdown();
                return;
            }
        }

#if DEBUG
        // Diagnostic aid: FLUENTTY_DUMP_KEYS=<file> writes every theme resource key to a file.
        var dump = Environment.GetEnvironmentVariable("FLUENTTY_DUMP_KEYS");
        if (!string.IsNullOrEmpty(dump))
        {
            var keys = new List<string>();
            void Collect(ResourceDictionary d)
            {
                foreach (var k in d.Keys) keys.Add(k.ToString() ?? "");
                foreach (var m in d.MergedDictionaries) Collect(m);
            }
            Collect(Resources);
            File.WriteAllLines(dump, keys.Order());
        }
#endif

        ThemeService.Initialize();
        Screensaver.Initialize();
        AppInfo.CleanupTemp();

        var window = new MainWindow(args);
        MainWindow = window;
        if (_instance != null)
        {
            _instance.ArgsReceived += args => Dispatcher.BeginInvoke(() => window.OpenFromCommandLine(args, fromOtherInstance: true));
            _instance.StartListening();
        }
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.FlushAll();
        AppInfo.CleanupTemp();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowError(e.Exception);
    }

    private static void ShowError(Exception ex)
    {
        Diagnostics.Log($"unhandled: {ex}");
        try
        {
            FluentDialog.Info(Current?.MainWindow, "Something went wrong",
                $"{ex.Message}\n\nFluenTTY will keep running. If this keeps happening, set FLUENTTY_LOG to a file path to capture details.");
        }
        catch
        {
            MessageBox.Show(ex.Message, "FluenTTY", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
