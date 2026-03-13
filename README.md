# ModConfig — Universal Mod Configuration Framework for Slay the Spire 2

# ModConfig — 杀戮尖塔2 通用模组配置框架

<p align="center">
  <img src="ModConfig/mod_image.png" alt="ModConfig" width="400"/>
</p>

Adds a **"Mods"** tab to the game's Settings screen. Mod authors register config entries via a simple API — controls are rendered automatically.

在游戏设置界面注入 **「Mods」** 标签页。模组作者通过 API 注册配置项，控件自动渲染。

## Download / 下载

- **Nexus Mods**: [ModConfig on Nexus Mods](https://www.nexusmods.com/slaythespire2/mods/27)
- **Video Tutorial / 视频教程**: [Bilibili](https://www.bilibili.com/video/BV12ZcrzmEo)

## For Players / 玩家须知

Install ModConfig alongside any mod that supports it. A new **"Mods"** tab will appear in Settings where you can adjust mod options without editing config files.

安装 ModConfig 后，支持该框架的模组会在设置页出现配置选项，无需手动改文件。

**Installation / 安装：** Extract `ModConfig.dll` + `ModConfig.pck` into `<Game>/mods/ModConfig/`.

**安装方法：** 将 `ModConfig.dll` 和 `ModConfig.pck` 放入游戏目录 `mods/ModConfig/` 即可。

## Supported Controls / 支持控件

| Control | Description | 说明 |
|---------|-------------|------|
| **Toggle** | On/Off switch | 开关 |
| **Slider** | Numeric range with step | 滑条（支持步长和格式化） |
| **Dropdown** | Select from options | 下拉框 |
| **KeyBind** | Keyboard shortcut capture | 快捷键绑定（支持组合键） |
| **TextInput** | Free-form text entry | 文本输入框 |
| **Header** | Section title (visual only) | 分组标题 |
| **Separator** | Visual divider (visual only) | 分隔线 |

## Compatibility / 兼容性

- Windows / macOS / Linux (AnyCPU)
- Bilingual: English & 简体中文 (auto-detected)
- Does not conflict with other mods

---

# For Mod Authors / 开发者接入指南

## Core Principle / 核心原则

**Zero-dependency integration** — your mod calls ModConfig via **reflection**. If ModConfig is not installed, your mod still works normally. No DLL reference needed.

**零依赖接入** — 你的模组通过**反射**调用 ModConfig。玩家没装 ModConfig 时模组照常运行，无需引用 DLL。

## Quick Start / 快速开始

### 1. Create a ModConfigBridge.cs in your mod

```csharp
using System.Reflection;

namespace YourMod;

/// <summary>
/// Weak-dependency bridge to ModConfig. Works via reflection — no DLL reference needed.
/// </summary>
internal static class ModConfigBridge
{
    private static bool _detected;
    private static bool _available;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configType;

    /// <summary>Check if ModConfig is loaded (cached after first check).</summary>
    internal static bool IsAvailable
    {
        get
        {
            if (!_detected)
            {
                _detected = true;
                _apiType = Type.GetType("ModConfig.ModConfigApi, ModConfig");
                _entryType = Type.GetType("ModConfig.ConfigEntry, ModConfig");
                _configType = Type.GetType("ModConfig.ConfigType, ModConfig");
                _available = _apiType != null && _entryType != null && _configType != null;
            }
            return _available;
        }
    }

    /// <summary>
    /// Register config entries with ModConfig.
    /// Call this AFTER all mods have loaded (use SceneTree.ProcessFrame callback).
    /// </summary>
    internal static void Register()
    {
        if (!IsAvailable) return;

        try
        {
            DeferredRegister();
        }
        catch (Exception e)
        {
            // Log error but don't crash — ModConfig is optional
            Console.WriteLine($"ModConfig registration failed: {e}");
        }
    }

    private static void DeferredRegister()
    {
        // Build config entries via reflection
        var entries = new[]
        {
            MakeEntry("my_toggle", "My Feature", ConfigTypeValue("Toggle"),
                defaultValue: true,
                onChanged: v => MySettings.FeatureEnabled = (bool)v),

            MakeEntry("my_slider", "Speed", ConfigTypeValue("Slider"),
                defaultValue: 1.0f, min: 0.5f, max: 3.0f, step: 0.1f, format: "F1",
                onChanged: v => MySettings.Speed = (float)v),
        };

        // Call ModConfigApi.Register(modId, displayName, entries)
        var register = _apiType!.GetMethod("Register",
            new[] { typeof(string), typeof(string), entries.GetType() });
        register?.Invoke(null, new object[] { "YourMod", "Your Mod Name", entries });
    }

    // ─── Reflection Helpers ──────────────────────────────────────

    private static object ConfigTypeValue(string name) =>
        Enum.Parse(_configType!, name);

    private static object MakeEntry(string key, string label, object type,
        object? defaultValue = null, float min = 0, float max = 100, float step = 1,
        string format = "F0", string[]? options = null,
        Action<object>? onChanged = null,
        Dictionary<string, string>? labels = null,
        Dictionary<string, string>? descriptions = null)
    {
        var entry = Activator.CreateInstance(_entryType!)!;
        SetProp(entry, "Key", key);
        SetProp(entry, "Label", label);
        SetProp(entry, "Type", type);
        if (defaultValue != null) SetProp(entry, "DefaultValue", defaultValue);
        SetProp(entry, "Min", min);
        SetProp(entry, "Max", max);
        SetProp(entry, "Step", step);
        SetProp(entry, "Format", format);
        if (options != null) SetProp(entry, "Options", options);
        if (onChanged != null) SetProp(entry, "OnChanged", onChanged);
        if (labels != null) SetProp(entry, "Labels", labels);
        if (descriptions != null) SetProp(entry, "Descriptions", descriptions);
        return entry;
    }

    private static void SetProp(object obj, string name, object value)
    {
        obj.GetType().GetProperty(name)?.SetValue(obj, value);
    }

    /// <summary>Read a config value with fallback.</summary>
    internal static T GetValue<T>(string modId, string key, T fallback)
    {
        if (!IsAvailable) return fallback;
        try
        {
            var method = _apiType!.GetMethod("GetValue")!.MakeGenericMethod(typeof(T));
            return (T)method.Invoke(null, new object[] { modId, key })!;
        }
        catch { return fallback; }
    }
}
```

### 2. Call Register() in your mod's Initialize()

Use a **deferred callback** so ModConfig has time to load first:

```csharp
public static void Initialize()
{
    // ... your mod init code ...

    // Deferred registration — wait one frame for all mods to load
    var tree = (Godot.SceneTree)Godot.Engine.GetMainLoop();
    tree.ProcessFrame += () =>
    {
        tree.ProcessFrame += () => ModConfigBridge.Register();
    };
}
```

### 3. Read config values at runtime

```csharp
// In your mod logic:
bool enabled = ModConfigBridge.GetValue("YourMod", "my_toggle", true);
float speed = ModConfigBridge.GetValue("YourMod", "my_slider", 1.0f);
```

## Bilingual Labels / 多语言标签

Use the `Labels` and `Descriptions` dictionaries for runtime i18n:

```csharp
MakeEntry("my_toggle", "My Feature", ConfigTypeValue("Toggle"),
    defaultValue: true,
    labels: new() { { "en", "My Feature" }, { "zhs", "我的功能" } },
    descriptions: new() { { "en", "Enable this feature" }, { "zhs", "启用此功能" } },
    onChanged: v => { /* ... */ });
```

## ConfigEntry Properties / 配置项属性

| Property | Type | Used By | Description |
|----------|------|---------|-------------|
| `Key` | string | All (except Header/Separator) | Unique key for persistence |
| `Label` | string | All | Display text (English fallback) |
| `Labels` | Dict | All | Per-language labels `{"en":"...", "zhs":"..."}` |
| `Type` | ConfigType | All | Control type to render |
| `DefaultValue` | object | Toggle/Slider/Dropdown/KeyBind/TextInput | Default value |
| `Min` | float | Slider | Minimum value |
| `Max` | float | Slider | Maximum value (default: 100) |
| `Step` | float | Slider | Step increment (default: 1) |
| `Format` | string | Slider | Display format: `"F0"`, `"F2"`, `"P0"` |
| `Options` | string[] | Dropdown | Selectable options |
| `MaxLength` | int | TextInput | Max characters (default: 64) |
| `Placeholder` | string | TextInput | Placeholder text |
| `OnChanged` | Action\<object\> | All data types | Callback when value changes |
| `Description` | string | All | Tooltip / description text |
| `Descriptions` | Dict | All | Per-language descriptions |

## Real-World Examples / 实际案例

These mods already integrate with ModConfig:

- **[Skada: Damage Meter](https://www.nexusmods.com/slaythespire2/mods/14)** — 5 settings (FontScale, Opacity, MaxBars, AutoReset, AutoSwitch)
- **SpeedX** — 14 settings (8 Toggle + 3 Slider + 3 KeyBind)

## Common Pitfalls / 常见问题

| Problem | Solution |
|---------|----------|
| Config entries don't appear | Use deferred registration (2-frame delay). ModConfig may not be loaded yet at your `Initialize()` time. |
| `OnChanged` not firing | Make sure you set the `OnChanged` property via reflection (`SetProp`). |
| Slider shows wrong format | Set `Format` property (e.g., `"F1"` for 1 decimal, `"P0"` for percentage). |
| KeyBind value type | KeyBind uses `long` (Godot keycode with modifiers). Cast from `object` to `long`. |
| Config not saved | Values auto-save to `user://ModConfig/<modId>.json`. No manual save needed. |
| ModConfig not installed | Your mod works normally — `IsAvailable` returns `false`, all `GetValue` calls return fallback. |

| 问题 | 解决方案 |
|------|---------|
| 配置项不显示 | 使用延迟注册（两帧回调）。`Initialize()` 时 ModConfig 可能还没加载完。 |
| `OnChanged` 不触发 | 确保通过反射正确设置了 `OnChanged` 属性。 |
| 滑条格式不对 | 设置 `Format` 属性（如 `"F1"` 一位小数，`"P0"` 百分比）。 |
| 快捷键值类型 | KeyBind 使用 `long`（Godot 键码含修饰键），从 `object` 转换为 `long`。 |
| 配置没保存 | 值会自动保存到 `user://ModConfig/<modId>.json`，无需手动保存。 |
| 玩家没装 ModConfig | 模组正常运行——`IsAvailable` 返回 `false`，`GetValue` 返回 fallback 值。 |

---

## Building from Source / 从源码构建

### Prerequisites / 前置条件
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download)
- [Godot 4.5.1 Mono](https://godotengine.org/)
- Slay the Spire 2 installed

### Build / 构建

1. Update `Sts2Dir` in `ModConfig.csproj` to your game install path
2. `dotnet build ModConfig.csproj`
3. DLL auto-copies to game's `mods/ModConfig/` folder

### Export PCK / 导出 PCK

```bash
Godot_v4.5.1-stable_mono_win64.exe --headless --path . --export-pack "Windows Desktop" "mods/ModConfig/ModConfig.pck"
```

---

## License / 许可

MIT License — free to use, modify, and redistribute.

## Author / 作者

**皮一下就很凡** — [Bilibili](https://space.bilibili.com/26786884) | [Nexus Mods](https://www.nexusmods.com/slaythespire2/users/56800967)
