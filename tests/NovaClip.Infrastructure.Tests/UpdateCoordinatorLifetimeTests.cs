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
            var coordinator = CreateCoordinator(service);
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
            using var coordinator = CreateCoordinator(service);
            var checks = Enumerable.Range(0, 8).Select(_ => coordinator.CheckAsync()).ToArray();
            coordinator.Dispose();
            service.Finish.SetResult(null);
            await Task.WhenAll(checks);
        }

        [Fact]
        public async Task CheckUsesInjectedApplicationVersion()
        {
            var service = new HeldUpdateService();
            using var coordinator = CreateCoordinator(service);
            var pending = coordinator.CheckAsync();
            Assert.Equal("1.0.0-beta.9", service.Version);
            service.Finish.SetResult(null);
            Assert.Null(await pending);
        }

        private static WindowsUpdateCoordinator CreateCoordinator(IUpdateService service) =>
            new(service, new WindowsSettingsStore(), "1.0.0-beta.9", true,
                _ => Task.CompletedTask, () => { });

        private sealed class HeldUpdateService : IUpdateService
        {
            public readonly TaskCompletionSource<AppUpdateInfo?> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken Token;
            public string? Version;
            public Task<AppUpdateInfo?> CheckForUpdateAsync(string version, UpdateChannel channel, CancellationToken cancellationToken = default)
            { Token = cancellationToken; Version = version; return Finish.Task; }
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
        public string? FfmpegPath { get; set; }
    }
    internal static class StartupDiagnostics
    {
        public static void Warning(string message, Exception ex) { }
        public static void Info(string message) { }
    }
}
