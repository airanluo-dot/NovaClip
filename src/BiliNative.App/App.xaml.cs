using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace BiliNative.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await AppServices.InitializeAsync();
        MainWindow = new MainWindow();
        MainWindow.Activate();
        _ = AppServices.UpdateCoordinator.CheckSilentlyAsync();
    }

    [STAThread]
    public static void Main()
    {
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
