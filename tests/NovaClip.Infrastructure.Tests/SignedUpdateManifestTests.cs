using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaClip.Infrastructure;
using Xunit;

namespace NovaClip.Infrastructure.Tests;

public sealed class SignedUpdateManifestTests
{
    [Fact]
    public void VerifiesManifestWithTrustedPublicKey()
    {
        using var rsa = RSA.Create(2048);
        var json = JsonSerializer.Serialize(new
        {
            Version = "1.0.0-beta.7",
            Channel = "preview",
            Assets = new[]
            {
                new
                {
                    Name = "NovaClip-win-x64-portable.zip",
                    Sha256 = new string('a', 64),
                    Size = 128L,
                    RuntimeIdentifier = "win-x64",
                    PackageType = "portable"
                }
            },
            KeyId = "test-key"
        });
        var signature = Convert.ToBase64String(
            rsa.SignData(
                Encoding.UTF8.GetBytes(json),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));

        Assert.True(SignedUpdateManifestVerifier.Verify(
            json,
            signature,
            rsa.ExportSubjectPublicKeyInfoPem(),
            out var manifest,
            out var error));
        Assert.Null(error);
        Assert.Equal("1.0.0-beta.7", manifest!.Version);
        Assert.Single(manifest.Assets);
    }

    [Fact]
    public void RejectsTamperedManifest()
    {
        using var rsa = RSA.Create(2048);
        const string json = """{"Version":"1.0.0-beta.7","Channel":"preview","Assets":[]}""";
        var signature = Convert.ToBase64String(
            rsa.SignData(
                Encoding.UTF8.GetBytes(json),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));

        Assert.False(SignedUpdateManifestVerifier.Verify(
            json.Replace("beta.7", "beta.8", StringComparison.Ordinal),
            signature,
            rsa.ExportSubjectPublicKeyInfoPem(),
            out _,
            out var error));
        Assert.NotNull(error);
    }
}
