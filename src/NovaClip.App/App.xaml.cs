using Microsoft.UI.Xaml;

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
            if (Environment.GetEnvironmentVariable("NOVACLIP_CI_SMOKE") == "1")
            {
                MainWindow.DispatcherQueue.TryEnqueue(MainWindow.RunSmokeNavigation);
            }
            StartupDiagnostics.Info("App.StartupCompleted");
            _ = AppServices.UpdateCoordinator.CheckSilentlyAsync();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("APP_STARTUP_FAILED", exception);
            throw;
        }
    }
}
