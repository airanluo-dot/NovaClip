using NovaClip.Core;
using NovaClip.App;
using Xunit;

namespace NovaClip.Infrastructure.Tests
{
    public sealed class UpdateCoordinatorLifetimeTests
    {
        [Fact]
        public async Task DisposeDuringCheckKeepsCancellationSourceAliveUntilCompletion()
        {
            var service = new HeldUpdateService();
            var coordinator = new WindowsUpdateCoordinator(service, new WindowsSettingsStore());
            var pending = coordinator.CheckAsync();
            coordinator.Dispose();
            coordinator.Stop();
            Assert.True(service.Token.IsCancellationRequested);
            service.Finish.SetResult(null);
            Assert.Null(await pending);
            coordinator.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.CheckAsync());
        }

        [Fact]
        public async Task ConcurrentChecksDrainAfterDisposal()
        {
            var service = new HeldUpdateService();
            var coordinator = new WindowsUpdateCoordinator(service, new WindowsSettingsStore());
            var checks = Enumerable.Range(0, 8).Select(_ => coordinator.CheckAsync()).ToArray();
            coordinator.Dispose();
            service.Finish.SetResult(null);
            await Task.WhenAll(checks);
        }

        private sealed class HeldUpdateService : IUpdateService
        {
            public readonly TaskCompletionSource<AppUpdateInfo?> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken Token;
            public Task<AppUpdateInfo?> CheckForUpdateAsync(string version, UpdateChannel channel, CancellationToken cancellationToken = default)
            { Token = cancellationToken; return Finish.Task; }
            public Task<string> DownloadAssetAsync(AppUpdateAsset asset, string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}

// Compile the actual coordinator with narrow host stubs; no WinUI runtime is required
// to reproduce its semaphore/CTS shutdown race.
namespace NovaClip.App
{
    public sealed class WindowsSettingsStore
    {
        public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Preview;
        public bool AutoCheckUpdates { get; set; } = true;
    }
    internal static class AppServices
    {
        public static string CurrentVersion => "1.0.0";
        public static bool IsPortableInstall => true;
        public static Task PrepareForUpdateAsync(CancellationToken token) => Task.CompletedTask;
    }
    internal static class StartupDiagnostics
    {
        public static void Warning(string message, Exception ex) { }
        public static void Info(string message) { }
    }
    internal static class App { public static WindowStub? MainWindow => null; }
    internal sealed class WindowStub
    {
        public DispatcherStub DispatcherQueue { get; } = new();
        public bool IsClosed { get; private set; }
        public void Close() { IsClosed = true; }
    }
    internal sealed class DispatcherStub { public bool Enabled { get; set; }
        public bool TryEnqueue(Action action) => Enabled; }
}
