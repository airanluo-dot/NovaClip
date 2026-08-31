using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliNative.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    private static Window? _failureWindow;

    public App()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("App XAML initialization failed.", exception);
            throw;
        }

        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                StartupDiagnostics.Error("Unhandled AppDomain exception.", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            StartupDiagnostics.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Info("NovaClip launch started.");
        try
        {
            await AppServices.InitializeAsync();
            MainWindow = new MainWindow();
            MainWindow.Activate();
            StartupDiagnostics.Info("Main window activated.");
            _ = AppServices.UpdateCoordinator.CheckSilentlyAsync();
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Error("NovaClip startup failed.", exception);
            ShowStartupFailure(exception);
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        StartupDiagnostics.Error("Unhandled WinUI exception.", args.Exception);
    }

    private static void ShowStartupFailure(Exception exception)
    {
        try
        {
            var message = new TextBlock
            {
                Text = "NovaClip 无法完成启动。错误详情已经写入启动日志。\n\n" + exception.Message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            var logPath = new TextBlock
            {
                Text = StartupDiagnostics.LogPath,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Opacity = 0.7
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = "NovaClip 启动失败", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(message);
            panel.Children.Add(new TextBlock { Text = "启动日志：" });
            panel.Children.Add(logPath);

            _failureWindow = new Window
            {
                Title = "NovaClip 启动失败",
                Content = new Border
                {
                    Padding = new Thickness(24),
                    Child = new ScrollViewer { Content = panel }
                }
            };
            _failureWindow.Activate();
        }
        catch (Exception displayException)
        {
            StartupDiagnostics.Error("Failed to show startup failure window.", displayException);
        }
    }
}
