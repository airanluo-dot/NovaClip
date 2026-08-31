using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace NovaClip.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        StartupDiagnostics.Info("App.Start");
        StartupDiagnostics.Info("Resources.Ready");
        UnhandledException += (_, args) => StartupDiagnostics.Error("WINUI_UNHANDLED_EXCEPTION", args.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await AppServices.InitializeAsync();
            StartupDiagnostics.Info("Services.Ready");
            MainWindow = new MainWindow();
            MainWindow.Activate();
            StartupDiagnostics.Info("App.StartupCompleted");
            _ = AppServices.UpdateCoordinator.CheckSilentlyAsync();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("APP_STARTUP_FAILED", exception);
            throw;
        }
    }

    [STAThread]
    public static void Main()
    {
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App();
            GC.KeepAlive(app);
        });
    }
}
