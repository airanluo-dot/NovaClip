using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaClip.Core;

namespace NovaClip.Infrastructure;

public sealed record SignedUpdateManifest(
    string Version,
    string Channel,
    IReadOnlyList<SignedUpdateManifestAsset> Assets,
    string? KeyId = null);

public sealed record SignedUpdateManifestAsset(
    string Name,
    string Sha256,
    long Size,
    string RuntimeIdentifier,
    string PackageType);

public static class SignedUpdateManifestVerifier
{
    private const int MaxManifestBytes = 1_000_000;
    private const int MaxAssets = 16;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool Verify(
        string manifestJson,
        string signatureBase64,
        string publicKeyPem,
        out SignedUpdateManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        if (string.IsNullOrWhiteSpace(manifestJson) || Encoding.UTF8.GetByteCount(manifestJson) > MaxManifestBytes)
        {
            error = "The signed update manifest is missing or too large.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(signatureBase64) || signatureBase64.Length > 16_384)
        {
            error = "The signed update signature is missing or too large.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(publicKeyPem) || publicKeyPem.Length > 32_768)
        {
            error = "The update verification key is missing or too large.";
            return false;
        }

        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(manifestJson), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                error = "The signed update manifest signature is invalid.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(manifestJson, JsonOptions);
            if (manifest is null || !SemanticVersion.TryParse(manifest.Version, out _))
            {
                error = "The signed update manifest version is invalid.";
                manifest = null;
                return false;
            }
            if (string.IsNullOrWhiteSpace(manifest.KeyId) || manifest.KeyId.Length > 128 ||
                manifest.Channel is not ("stable" or "preview") || manifest.Assets is null || manifest.Assets.Count == 0 || manifest.Assets.Count > MaxAssets)
            {
                error = "The signed update manifest channel or asset list is invalid.";
                manifest = null;
                return false;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in manifest.Assets)
            {
                if (asset is null || string.IsNullOrWhiteSpace(asset.Name) || Path.GetFileName(asset.Name) != asset.Name || asset.Name.IndexOfAny(['/', '\\', '\0']) >= 0 ||
                    !names.Add(asset.Name) || asset.Size <= 0 || asset.RuntimeIdentifier != "win-x64" ||
                    asset.PackageType is not ("setup" or "portable") || GitHubReleaseUpdateService.ParseSha256Digest("sha256:" + asset.Sha256) is null)
                {
                    error = "The signed update manifest contains an invalid asset.";
                    manifest = null;
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or CryptographicException or JsonException)
        {
            error = exception.Message;
            manifest = null;
            return false;
        }
    }
}
