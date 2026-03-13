using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using FileAccess = Godot.FileAccess;

namespace ModConfig;

/// <summary>
/// Localization system with dual-channel loading (embedded DLL + PCK)
/// and hybrid language detection (LocManager subscription + lazy fallback).
/// </summary>
internal static class I18n
{
    private static Dictionary<string, string> _translations = new();
    private static string? _loadedLanguage;
    private static bool _subscribed;

    /// <summary>Fired when language changes. UI should refresh.</summary>
    internal static event Action? Changed;

    /// <summary>Current resolved language code (e.g., "en", "zhs"). Used by mods for display name resolution.</summary>
    internal static string CurrentLang => _loadedLanguage ?? ResolveLanguage();

    // ─── Public Properties ──────────────────────────────────────────

    internal static string TabMods => Get("tab_mods", "Mods");
    internal static string NoConfigs => Get("no_configs", "No mods have registered configurations.");
    internal static string ResetDefaults => Get("reset_defaults", "Reset");
    internal static string PressAnyKey => Get("press_any_key", "Press any key...");
    internal static string KeyNone => Get("key_none", "None");

    // ─── Lifecycle ──────────────────────────────────────────────────

    internal static void Initialize()
    {
        ForceReload();
        TrySubscribe();
    }

    internal static string Get(string key, string fallback)
    {
        EnsureLoaded();
        return _translations.GetValueOrDefault(key) ?? fallback;
    }

    // ─── Language Detection ─────────────────────────────────────────

    private static void TrySubscribe()
    {
        if (_subscribed) return;
        try
        {
            var instance = LocManager.Instance;
            if (instance != null)
            {
                instance.SubscribeToLocaleChange(OnLocaleChanged);
                _subscribed = true;
            }
            else
            {
                MainFile.Log.Info("LocManager.Instance is null at init, will use lazy detection");
            }
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to subscribe to locale change: {e}");
        }
    }

    private static void OnLocaleChanged()
    {
        _loadedLanguage = null;
        ForceReload();
        Changed?.Invoke();
    }

    private static void EnsureLoaded()
    {
        if (!_subscribed) TrySubscribe();

        string language = ResolveLanguage();
        if (!string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            _translations = LoadTranslations(language);
            _loadedLanguage = language;
        }
    }

    private static void ForceReload()
    {
        string language = ResolveLanguage();
        _translations = LoadTranslations(language);
        _loadedLanguage = language;
    }

    private static string ResolveLanguage()
    {
        string? language = null;
        try { language = LocManager.Instance?.Language; } catch { }

        if (string.IsNullOrWhiteSpace(language))
            try { language = TranslationServer.GetLocale(); } catch { }

        return NormalizeLanguageCode(language);
    }

    // ─── Translation Loading ────────────────────────────────────────

    private static Dictionary<string, string> LoadTranslations(string language)
    {
        foreach (string candidate in GetLanguageCandidates(language))
        {
            var translations = TryLoadEmbedded(candidate);
            if (translations is { Count: > 0 })
                return translations;

            var pck = TryLoadFromPck($"res://ModConfig/localization/{candidate}.json");
            if (pck is { Count: > 0 })
                return pck;
        }
        return new Dictionary<string, string>();
    }

    private static Dictionary<string, string>? TryLoadEmbedded(string lang)
    {
        try
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"ModConfig.localization.{lang}.json");
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch { return null; }
    }

    private static Dictionary<string, string>? TryLoadFromPck(string path)
    {
        try
        {
            if (!FileAccess.FileExists(path)) return null;
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            var json = file.GetAsText();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch { return null; }
    }

    // ─── Language Code Normalization ────────────────────────────────

    private static string NormalizeLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "en";
        code = code.ToLowerInvariant().Trim();

        // Direct mappings
        if (code is "zhs" or "zht" or "en" or "jpn" or "kor" or "deu" or "fra"
            or "esp" or "ita" or "pol" or "ptb" or "rus" or "tha" or "tur")
            return code;

        // Chinese variants → zhs
        if (code.StartsWith("zh")) return "zhs";

        // English variants → en
        if (code.StartsWith("en")) return "en";

        // Japanese
        if (code.StartsWith("ja")) return "jpn";

        // Korean
        if (code.StartsWith("ko")) return "kor";

        return "en";
    }

    private static IEnumerable<string> GetLanguageCandidates(string language)
    {
        yield return language;

        // Strip region suffix
        int dash = language.IndexOf('_');
        if (dash < 0) dash = language.IndexOf('-');
        if (dash > 0)
            yield return language[..dash];

        // Ultimate fallback
        if (language != "en")
            yield return "en";
    }
}
