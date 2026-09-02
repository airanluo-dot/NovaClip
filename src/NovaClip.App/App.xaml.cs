using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NovaClip.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    private Window? _startupFailureWindow;
    private bool _resourcesFailed;

    public App()
    {
        StartupDiagnostics.Info("App.Start");
        try
        {
            InitializeComponent();
            StartupDiagnostics.Info("Resources.Ready");
        }
        catch (Exception exception)
        {
            // Keep a resources/XAML failure visible instead of allowing the process to exit with a blank window.
            _resourcesFailed = true;
            StartupDiagnostics.Error("APP_RESOURCES_FAILED", exception);
            ShowStartupFailure(exception);
        }
        UnhandledException += (_, args) =>
        {
            StartupDiagnostics.Error("WINUI_UNHANDLED_EXCEPTION", args.Exception);
            args.Handled = true;
            if (MainWindow is null) ShowStartupFailure(args.Exception);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_resourcesFailed) return;
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
        var fallbackTitle = "NovaClip startup failed";
        var fallbackMessage = $"NovaClip could not complete startup.\n\n{exception.Message}\n\nDiagnostic log:\n{StartupDiagnostics.LogPath}";
        try
        {
            var text = new LocalizationService();
            fallbackTitle = text.GetString("StartupFailure_Title") is { Length: > 0 } localizedTitle ? localizedTitle : fallbackTitle;
            fallbackMessage = text.Format("StartupFailure_Message", exception.Message, StartupDiagnostics.LogPath);
        }
        catch (Exception localizationException)
        {
            StartupDiagnostics.Error("STARTUP_FAILURE_LOCALIZATION_FAILED", localizationException);
        }

        try
        {
            _startupFailureWindow = new Window
            {
                Title = fallbackTitle,
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = fallbackMessage,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(24)
                    }
                }
            };
            _startupFailureWindow.Activate();
        }
        catch (Exception windowException)
        {
            StartupDiagnostics.Error("STARTUP_FAILURE_UI_FAILED", windowException);
        }
    }
}
