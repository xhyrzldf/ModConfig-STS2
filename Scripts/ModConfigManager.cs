using System.Text.Json;
using Godot;
using FileAccess = Godot.FileAccess;

namespace ModConfig;

/// <summary>
/// Internal state management and persistence for mod configurations.
/// </summary>
internal static class ModConfigManager
{
    private static readonly Dictionary<string, ModRegistration> _registrations = new();
    private static readonly Dictionary<string, Dictionary<string, object>> _values = new();

    // Save debounce: collect dirty modIds and flush after a short delay
    private static readonly HashSet<string> _dirtyMods = new();
    private static bool _saveScheduled;

    private const string ConfigDir = "user://ModConfig/";

    internal static IReadOnlyDictionary<string, ModRegistration> Registrations => _registrations;

    internal static void Initialize()
    {
        DirAccess.MakeDirRecursiveAbsolute(ConfigDir);
    }

    internal static void Register(ModRegistration reg)
    {
        _registrations[reg.ModId] = reg;

        if (!_values.ContainsKey(reg.ModId))
            _values[reg.ModId] = new Dictionary<string, object>();

        LoadValues(reg);
        SettingsTabInjector.RefreshUI();

        MainFile.Log.Info($"Registered config: {reg.DisplayName} ({reg.Entries.Length} entries)");
    }

    internal static T GetValue<T>(string modId, string key)
    {
        if (_values.TryGetValue(modId, out var modValues) &&
            modValues.TryGetValue(key, out var value))
        {
            try { return (T)Convert.ChangeType(value, typeof(T)); }
            catch { /* fall through to default */ }
        }

        if (_registrations.TryGetValue(modId, out var reg))
        {
            var entry = reg.Entries.FirstOrDefault(e => e.Key == key);
            if (entry != null)
            {
                try { return (T)Convert.ChangeType(entry.DefaultValue, typeof(T)); }
                catch { /* fall through */ }
            }
        }

        return default!;
    }

    internal static void SetValue(string modId, string key, object value)
    {
        if (!_values.ContainsKey(modId))
            _values[modId] = new Dictionary<string, object>();

        _values[modId][key] = value;

        // Fire callback
        if (_registrations.TryGetValue(modId, out var reg))
        {
            var entry = reg.Entries.FirstOrDefault(e => e.Key == key);
            try { entry?.OnChanged?.Invoke(value); }
            catch (Exception e) { MainFile.Log.Error($"Config callback error [{modId}.{key}]: {e}"); }
        }

        SettingsTabInjector.NotifyValueChanged(modId, key, value);
        ScheduleSave(modId);
    }

    /// <summary>
    /// Reset all config values for a mod to their defaults.
    /// Returns true if any values were actually changed.
    /// </summary>
    internal static bool ResetToDefaults(string modId)
    {
        if (!_registrations.TryGetValue(modId, out var reg))
            return false;

        if (!_values.ContainsKey(modId))
            _values[modId] = new Dictionary<string, object>();

        bool changed = false;
        foreach (var entry in reg.Entries)
        {
            if (entry.Type is ConfigType.Header or ConfigType.Separator)
                continue;

            var oldValue = _values[modId].GetValueOrDefault(entry.Key);
            _values[modId][entry.Key] = entry.DefaultValue;

            if (!Equals(oldValue, entry.DefaultValue))
            {
                changed = true;
                try { entry.OnChanged?.Invoke(entry.DefaultValue); }
                catch (Exception e) { MainFile.Log.Error($"Config callback error [{modId}.{entry.Key}]: {e}"); }
            }
        }

        if (changed)
            ScheduleSave(modId);

        return changed;
    }

    // ─── Save Debounce ───────────────────────────────────────────

    private static void ScheduleSave(string modId)
    {
        _dirtyMods.Add(modId);
        if (_saveScheduled) return;
        _saveScheduled = true;

        // Flush on next idle frame — batches rapid slider changes into one write
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
            tree.ProcessFrame += FlushSaves;
        else
            FlushSaves(); // fallback: save immediately
    }

    private static void FlushSaves()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
            tree.ProcessFrame -= FlushSaves;

        _saveScheduled = false;
        foreach (var modId in _dirtyMods)
            SaveValues(modId);
        _dirtyMods.Clear();
    }

    private static void LoadValues(ModRegistration reg)
    {
        var path = ConfigDir + reg.ModId + ".json";

        Dictionary<string, JsonElement>? saved = null;
        if (FileAccess.FileExists(path))
        {
            try
            {
                using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                var json = file.GetAsText();
                saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Failed to load config for {reg.ModId}: {e}");
            }
        }

        foreach (var entry in reg.Entries)
        {
            if (entry.Type is ConfigType.Header or ConfigType.Separator)
                continue;

            if (saved != null && saved.TryGetValue(entry.Key, out var element))
            {
                try
                {
                    _values[reg.ModId][entry.Key] = entry.Type switch
                    {
                        ConfigType.Toggle => element.GetBoolean(),
                        ConfigType.Slider => (float)element.GetDouble(),
                        ConfigType.Dropdown => element.GetString() ?? (string)entry.DefaultValue,
                        ConfigType.KeyBind => element.GetInt64(),
                        ConfigType.TextInput => element.GetString() ?? (string)entry.DefaultValue,
                        _ => entry.DefaultValue
                    };
                    continue;
                }
                catch { /* fall through to default */ }
            }

            _values[reg.ModId][entry.Key] = entry.DefaultValue;
        }
    }

    private static void SaveValues(string modId)
    {
        if (!_values.TryGetValue(modId, out var modValues))
            return;

        var path = ConfigDir + modId + ".json";
        try
        {
            var json = JsonSerializer.Serialize(modValues, new JsonSerializerOptions { WriteIndented = true });
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            file.StoreString(json);
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to save config for {modId}: {e}");
        }
    }

    internal static void SaveAll()
    {
        foreach (var modId in _values.Keys)
            SaveValues(modId);
    }
}
