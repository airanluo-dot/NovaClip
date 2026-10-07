namespace NovaClip.Core;

/// <summary>Owns package name and MIME validation for release selection and updater handoff.</summary>
public static class UpdatePackagePolicy
{
    public static bool IsExpectedAsset(AppUpdateAsset asset, bool portable)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsSafeAssetName(asset.Name) ||
            !asset.Name.EndsWith(portable ? "-portable.zip" : "-setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(asset.ContentType)) return true;
        var contentType = asset.ContentType.Trim();
        return contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            (portable
                ? contentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase) ||
                  contentType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase)
                : contentType.Equals("application/x-msdownload", StringComparison.OrdinalIgnoreCase) ||
                  contentType.Equals("application/x-msdos-program", StringComparison.OrdinalIgnoreCase) ||
                  contentType.Equals("application/vnd.microsoft.portable-executable", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeAssetName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name is not "." and not ".." &&
        name.IndexOfAny(['/', '\\', '\0', '<', '>', ':', '"', '|', '?', '*']) < 0 &&
        Path.GetFileName(name) == name;
}
