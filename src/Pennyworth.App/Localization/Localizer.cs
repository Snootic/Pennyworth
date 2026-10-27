using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.Core.Wording;

namespace Pennyworth.App.Localization;

public enum LanguagePreference
{
    System,
    English,
    Portuguese,
}

/// <summary>
/// The app's current language. Views bind through <see cref="Tr"/>; view models listen to
/// <see cref="LanguageChanged"/> and refresh their formatted text. Strings that name the operating
/// system's things carry <c>{@Name}</c> tokens, and every read replaces them with the words of the
/// platform in <see cref="Wording"/>: read those through <see cref="this[string]"/> or <see cref="Resolve"/>.
/// </summary>
public sealed class Localizer
{
    public static Localizer Instance { get; } = new();

    private Localizer()
    {
        Culture = Resolve(LanguagePreference.System);
        Apply(Culture);
    }

    public CultureInfo Culture { get; private set; }

    public LanguagePreference Preference { get; private set; }

    public bool IsPortuguese => Culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

    public event EventHandler? LanguageChanged;

    /// <summary>Windows' wording until the composition root says otherwise.</summary>
    public IPlatformText Wording { get; private set; } = WindowsText.Instance;

    public string this[string key] => Strings.ResourceManager.GetString(key, Culture) is { } raw ? Resolve(raw) : key;

    /// <summary>The operating system whose words the strings use.</summary>
    public void UseWording(IPlatformText wording)
    {
        Wording = wording;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One of the platform's words in the current language.</summary>
    public string Text(PlatformText text) => Wording.Text(text, IsPortuguese);

    /// <summary>A <c>Strings.*</c> value with its <c>{@Name}</c> tokens replaced; leaves <c>{0}</c> placeholders alone.</summary>
    public string Resolve(string raw) => PlatformTokens.Resolve(raw, Wording, IsPortuguese);

    public void SetLanguage(LanguagePreference preference)
    {
        Preference = preference;
        CultureInfo culture = Resolve(preference);
        if (culture.Name == Culture.Name)
        {
            return;
        }

        Culture = culture;
        Apply(culture);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The text for <paramref name="key"/> now and after every language change.</summary>
    public IObservable<string> Observe(string key, bool upper = false) => new LocalizedText(this, key, upper);

    /// <summary>"System" follows the Windows display language: Portuguese for any pt-*, otherwise English.</summary>
    public static CultureInfo Resolve(LanguagePreference preference) => preference switch
    {
        LanguagePreference.English => CultureInfo.GetCultureInfo("en-US"),
        LanguagePreference.Portuguese => CultureInfo.GetCultureInfo("pt-BR"),
        _ => CultureInfo.InstalledUICulture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("pt-BR")
            : CultureInfo.GetCultureInfo("en-US"),
    };

    private static void Apply(CultureInfo culture)
    {
        // Strings, number formats ("1,5 GB") and knowledge-base text all follow these.
        Strings.Culture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private sealed class LocalizedText(Localizer localizer, string key, bool upper) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            void Push(object? sender, EventArgs e) => observer.OnNext(Text());

            observer.OnNext(Text());
            localizer.LanguageChanged += Push;
            return new Unsubscriber(() => localizer.LanguageChanged -= Push);
        }

        private string Text()
        {
            string text = localizer[key];
            return upper ? text.ToUpper(localizer.Culture) : text;
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
