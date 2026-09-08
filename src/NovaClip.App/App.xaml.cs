using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NovaClip.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    private Window? _startupFailureWindow;
    private SingleInstanceCoordinator? _singleInstance;
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
            _resourcesFailed = true;
            StartupDiagnostics.Error("APP_RESOURCES_FAILED", exception);
            ShowStartupFailure(exception);
        }

        UnhandledException += (_, args) =>
        {
            StartupDiagnostics.Error("WINUI_UNHANDLED_EXCEPTION", args.Exception);
            var recoverable = AppExceptionPolicy.IsRecoverable(args.Exception);
            args.Handled = recoverable;
            if (!recoverable) AppServices.BeginShutdown();
            if (MainWindow is null || recoverable) ShowStartupFailure(args.Exception);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_resourcesFailed) return;
        try
        {
            _singleInstance = await SingleInstanceCoordinator.AcquireOrForwardAsync();
            if (_singleInstance is null) return;
            _singleInstance.ActivateRequested += SingleInstance_ActivateRequested;

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
            AppServices.StartBackgroundUpdateCheck();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("APP_STARTUP_FAILED", exception);
            AppServices.BeginShutdown();
            ShowStartupFailure(exception);
        }
    }

    private void SingleInstance_ActivateRequested(object? sender, EventArgs e)
    {
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow?.Activate();
            Pages.BrowserPage.Current?.FocusAddressBar();
        });
    }

    private void ShowStartupFailure(Exception exception)
    {
        var fallbackTitle = "NovaClip startup failed";
        var fallbackMessage = "NovaClip could not complete startup.\n\n" + exception.Message + "\n\nDiagnostic log:\n" + StartupDiagnostics.LogPath;
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

internal static class AppExceptionPolicy
{
    public static bool IsRecoverable(Exception exception) =>
        exception is OperationCanceledException or
        IOException or
        UnauthorizedAccessException;
}
