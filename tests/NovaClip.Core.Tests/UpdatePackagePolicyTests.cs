using NovaClip.Core;
using Xunit;

namespace NovaClip.Core.Tests;

public sealed class UpdatePackagePolicyTests
{
    [Theory]
    [InlineData("application/x-msdos-program")]
    [InlineData("application/x-msdownload")]
    [InlineData("application/vnd.microsoft.portable-executable")]
    [InlineData("application/octet-stream")]
    public void OfficialSetupMimeTypesAreAccepted(string contentType)
    {
        Assert.True(UpdatePackagePolicy.IsExpectedAsset(Asset("NovaClip-1.0.0-beta.8-win-x64-setup.exe", contentType), portable: false));
    }

    [Theory]
    [InlineData("../NovaClip-setup.exe", "application/x-msdos-program", false)]
    [InlineData("..\\NovaClip-setup.exe", "application/x-msdos-program", false)]
    [InlineData("NovaClip:stream-setup.exe", "application/x-msdos-program", false)]
    [InlineData("NovaClip-setup.exe", "text/html", false)]
    [InlineData("NovaClip-setup.exe", "application/zip", false)]
    [InlineData("NovaClip-portable.zip", "application/x-msdos-program", true)]
    [InlineData("NovaClip-portable.zip", "text/html", true)]
    [InlineData("NovaClip-setup.exe", "application/octet-stream", true)]
    public void UnsafeNamesAndWrongPackageTypesAreRejected(string name, string contentType, bool portable)
    {
        Assert.False(UpdatePackagePolicy.IsExpectedAsset(Asset(name, contentType), portable));
    }

    [Fact]
    public void ReleaseSelectionSkipsInvalidAssetBeforeValidPackage()
    {
        var setup = Asset("NovaClip-1.0.0-beta.9-win-x64-setup.exe", "application/x-msdos-program");
        var portable = Asset("NovaClip-1.0.0-beta.9-win-x64-portable.zip", "application/zip");
        var update = new AppUpdateInfo("1.0.0-beta.9", true, null, null,
        [
            Asset("NovaClip-invalid-setup.exe", "text/html"),
            Asset("../NovaClip-portable.zip", "application/zip"),
            setup,
            portable
        ]);

        Assert.Same(setup, update.SetupAsset);
        Assert.Same(portable, update.PortableAsset);
    }

    private static AppUpdateAsset Asset(string name, string contentType) =>
        new(name, "https://github.com/airanluo-dot/NovaClip/releases/download/v1.0.0-beta.9/" + name,
            1024, contentType, "sha256:" + new string('a', 64));
}
