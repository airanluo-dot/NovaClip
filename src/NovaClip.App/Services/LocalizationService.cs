using Microsoft.Windows.ApplicationModel.Resources;
using NovaClip.Contracts;

namespace NovaClip.App;

public sealed class LocalizationService : ILocalizationService
{
    private readonly ResourceLoader _loader = new();
    public string GetString(string key) => _loader.GetString(key);
    public string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, GetString(key), args);
}
