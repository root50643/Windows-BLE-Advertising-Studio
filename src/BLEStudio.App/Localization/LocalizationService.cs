using System.Globalization;
using System.Windows;

namespace BLEStudio.App.Localization;

public static class LocalizationService
{
    public static string CurrentCulture { get; private set; } = "en-US";
    public static bool TrySetCulture(string cultureName)
    {
        // A translation is an embedded dictionary with identical UI_* keys.
        // Missing languages leave the working English UI intact.
        var culture = CultureInfo.GetCultureInfo(cultureName);
        ResourceDictionary dictionary;
        try { dictionary = new() { Source = new Uri($"Localization/Strings.{culture.Name}.xaml", UriKind.Relative) }; }
        catch (System.IO.IOException) { return false; }
        Application.Current.Resources.MergedDictionaries[1] = dictionary;
        CurrentCulture = culture.Name;
        return true;
    }
}
