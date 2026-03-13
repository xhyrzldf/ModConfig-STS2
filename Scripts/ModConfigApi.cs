namespace ModConfig;

/// <summary>
/// Config entry types supported by ModConfig.
/// </summary>
public enum ConfigType
{
    Toggle,
    Slider,
    Dropdown,
    KeyBind,
    TextInput,
    Header,
    Separator
}

/// <summary>
/// Defines a single configuration entry for a mod.
/// </summary>
public class ConfigEntry
{
    /// <summary>Unique key for persistence. Not needed for Header/Separator.</summary>
    public string Key { get; set; } = "";

    /// <summary>Display label shown in the settings UI (English fallback).</summary>
    public string Label { get; set; } = "";

    /// <summary>Optional i18n key. If set, ModConfig resolves via I18n.Get(LabelKey, Label).</summary>
    public string? LabelKey { get; set; }

    /// <summary>Optional per-language labels. Key = language code ("en","zhs",...). Resolved at render time; falls back to Label.</summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>Optional description shown below the control (English fallback).</summary>
    public string Description { get; set; } = "";

    /// <summary>Optional i18n key for description.</summary>
    public string? DescriptionKey { get; set; }

    /// <summary>Optional per-language descriptions. Key = language code. Falls back to Description.</summary>
    public Dictionary<string, string>? Descriptions { get; set; }

    /// <summary>The type of control to render.</summary>
    public ConfigType Type { get; set; }

    /// <summary>Default value. bool for Toggle, float for Slider, string for Dropdown, long for KeyBind, string for TextInput.</summary>
    public object DefaultValue { get; set; } = false;

    /// <summary>Slider minimum value.</summary>
    public float Min { get; set; }

    /// <summary>Slider maximum value.</summary>
    public float Max { get; set; } = 100f;

    /// <summary>Slider step increment.</summary>
    public float Step { get; set; } = 1f;

    /// <summary>Slider value display format (e.g., "F0", "F2", "P0"). Default: "F0".</summary>
    public string Format { get; set; } = "F0";

    /// <summary>Dropdown options — display values (string array).</summary>
    public string[]? Options { get; set; }

    /// <summary>Optional i18n keys for dropdown options. Same length as Options.</summary>
    public string[]? OptionsKeys { get; set; }

    /// <summary>TextInput max character length. Default: 64.</summary>
    public int MaxLength { get; set; } = 64;

    /// <summary>TextInput placeholder text.</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>Callback invoked when the value changes.</summary>
    public Action<object>? OnChanged { get; set; }
}

/// <summary>
/// Internal registration record.
/// </summary>
internal class ModRegistration
{
    public string ModId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Localized display names: {"en": "English Name", "zhs": "中文名"}.</summary>
    public Dictionary<string, string>? DisplayNames { get; set; }
    public ConfigEntry[] Entries { get; set; } = Array.Empty<ConfigEntry>();

    /// <summary>Resolve display name for current game language.</summary>
    internal string GetLocalizedName()
    {
        if (DisplayNames == null || DisplayNames.Count == 0)
            return DisplayName;

        string lang = I18n.CurrentLang;

        // Exact match
        if (DisplayNames.TryGetValue(lang, out var exact))
            return exact;

        // Prefix match (e.g., "eng" → "en")
        foreach (var (key, value) in DisplayNames)
        {
            if (lang.StartsWith(key) || key.StartsWith(lang))
                return value;
        }

        // Fallback
        return DisplayName;
    }
}

/// <summary>
/// Public API for mods to register their configuration with ModConfig.
/// Other mods call these static methods — no hard DLL reference needed if using reflection.
/// </summary>
public static class ModConfigApi
{
    /// <summary>
    /// Register a mod's configuration entries.
    /// Call this in your mod's Initialize() method.
    /// </summary>
    /// <param name="modId">Unique identifier for the mod (e.g., "DamageMeter")</param>
    /// <param name="displayName">Human-readable mod name for the UI header</param>
    /// <param name="entries">Array of ConfigEntry defining the mod's settings</param>
    public static void Register(string modId, string displayName, ConfigEntry[] entries)
    {
        ModConfigManager.Register(new ModRegistration
        {
            ModId = modId,
            DisplayName = displayName,
            Entries = entries
        });
    }

    /// <summary>
    /// Register with localized display names.
    /// displayNames: {"en": "English Name", "zhs": "中文名"}.
    /// </summary>
    public static void Register(string modId, string displayName, Dictionary<string, string> displayNames, ConfigEntry[] entries)
    {
        ModConfigManager.Register(new ModRegistration
        {
            ModId = modId,
            DisplayName = displayName,
            DisplayNames = displayNames,
            Entries = entries
        });
    }

    /// <summary>
    /// Get the current value of a config entry.
    /// </summary>
    public static T GetValue<T>(string modId, string key)
    {
        return ModConfigManager.GetValue<T>(modId, key);
    }

    /// <summary>
    /// Set a config value programmatically.
    /// </summary>
    public static void SetValue(string modId, string key, object value)
    {
        ModConfigManager.SetValue(modId, key, value);
    }
}
