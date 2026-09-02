using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NovaClip.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    private Window? _startupFailureWindow;

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
            if (Environment.GetEnvironmentVariable("NOVACLIP_CI_SMOKE") == "1")
            {
                await Pages.BrowserPage.VerifyEnvironmentAsync();
            }
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
            ShowStartupFailure(exception);
        }
    }

    private void ShowStartupFailure(Exception exception)
    {
        try
        {
            var details = $"NovaClip 无法完成启动。\n\n{exception.Message}\n\n诊断日志：\n{StartupDiagnostics.LogPath}";
            _startupFailureWindow = new Window
            {
                Title = "NovaClip 启动失败",
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = details,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(24)
                    }
                }
            };
            _startupFailureWindow.Activate();
        }
        catch (Exception fallbackException)
        {
            StartupDiagnostics.Error("STARTUP_FAILURE_UI_FAILED", fallbackException);
        }
    }
}
