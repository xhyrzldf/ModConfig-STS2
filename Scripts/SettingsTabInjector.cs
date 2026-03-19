using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace ModConfig;

/// <summary>
/// Injects a "Mods" tab into the game's settings screen using SceneTree signals.
/// Zero Harmony — pure Godot public API + reflection for private field access.
/// </summary>
internal static class SettingsTabInjector
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    // Track all injected instances (main menu + pause menu are separate instances)
    private static readonly List<WeakReference<VBoxContainer>> _allContainers = new();
    private static readonly List<WeakReference<NSettingsTab>> _allTabs = new();
    private static bool _i18nSubscribed;

    // KeyBind capture state
    private static Button? _activeKeyBindButton;
    private static string _activeKeyBindModId = "";
    private static ConfigEntry? _activeKeyBindEntry;

    // Collapsible mod sections — track which mods are collapsed by modId
    private static readonly HashSet<string> _collapsedMods = new();
    private static readonly Dictionary<(string ModId, string Key), List<LiveBinding>> _liveBindings = new();

    private sealed class LiveBinding
    {
        public LiveBinding(Func<object, bool> apply)
        {
            Apply = apply;
        }

        public Func<object, bool> Apply { get; }
    }

    private sealed class UiUpdateGuard
    {
        public bool Suppress { get; set; }
    }

    // ─── Colors matching the game's settings screen palette ─────────
    private static readonly Color CreamGold = new("D4C88E");
    private static readonly Color DimText = new("8A7E5C");
    private static readonly Color TextColor = new(0.9f, 0.85f, 0.75f);
    private static readonly Color ResetColor = new(0.8f, 0.5f, 0.4f);
    private static readonly Color KeyBindListening = new(1.0f, 0.85f, 0.3f);
    private static readonly Color ModHeaderBg = new("2C434F");
    private static readonly Color RowSeparatorColor = new("2C434F", 0.5f);

    internal static void Initialize()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.NodeAdded += OnNodeAdded;
    }

    private static void OnNodeAdded(Node node)
    {
        if (node is not NSettingsTabManager)
            return;

        if (node.GetNodeOrNull("Mods") != null)
            return;

        node.Connect("ready",
            Callable.From(() => InjectModsTab((NSettingsTabManager)node)),
            (uint)GodotObject.ConnectFlags.OneShot);
    }

    private static void InjectModsTab(NSettingsTabManager tabManager)
    {
        try
        {
            var tabsField = typeof(NSettingsTabManager).GetField("_tabs", PrivateInstance);
            if (tabsField == null)
            {
                MainFile.Log.Error("Cannot find _tabs field on NSettingsTabManager");
                return;
            }

            var tabs = tabsField.GetValue(tabManager) as IDictionary;
            if (tabs == null || tabs.Count == 0)
            {
                MainFile.Log.Error("_tabs dictionary is empty or null");
                return;
            }

            NSettingsTab? firstTab = null;
            NSettingsPanel? firstPanel = null;
            foreach (DictionaryEntry entry in tabs)
            {
                firstTab = entry.Key as NSettingsTab;
                firstPanel = entry.Value as NSettingsPanel;
                break;
            }

            if (firstTab == null || firstPanel == null)
            {
                MainFile.Log.Error("Could not find existing tab/panel to clone");
                return;
            }

            // === 1. Create "Mods" tab ===
            var modsTab = (NSettingsTab)firstTab.Duplicate();
            modsTab.Name = "Mods";

            var tabImage = modsTab.GetNodeOrNull<TextureRect>("TabImage");
            if (tabImage?.Material is ShaderMaterial shader)
                tabImage.Material = (ShaderMaterial)shader.Duplicate();

            tabManager.AddChild(modsTab);
            modsTab.SetLabel(I18n.TabMods);
            modsTab.Deselect();
            PositionNewTab(tabs, modsTab);

            // === 2. Create panel ===
            // Duplicate then clean up BEFORE adding to tree.
            // Game-internal nodes (NDropdownPositioner etc.) fire _Ready() on AddChild
            // and reference controls from the original panel → ObjectDisposedException.
            var modsPanel = (NSettingsPanel)firstPanel.Duplicate();
            modsPanel.Name = "ModsSettings";
            modsPanel.Visible = false;

            // Identify Content container name from the original (already in tree)
            var contentName = firstPanel.Content?.Name;
            VBoxContainer? contentContainer = null;

            foreach (var child in modsPanel.GetChildren().ToArray())
            {
                if (contentName != null && child.Name == contentName && child is VBoxContainer vbox)
                {
                    contentContainer = vbox;
                    foreach (var inner in vbox.GetChildren().ToArray())
                    {
                        vbox.RemoveChild(inner);
                        inner.Free();
                    }
                }
                else
                {
                    modsPanel.RemoveChild(child);
                    child.Free();
                }
            }

            firstPanel.GetParent().AddChild(modsPanel);

            // Fallback if Content couldn't be resolved before tree entry
            if (contentContainer == null)
                contentContainer = modsPanel.Content;

            _allContainers.Add(new WeakReference<VBoxContainer>(contentContainer));

            // === 3. Register in _tabs ===
            tabs.Add(modsTab, modsPanel);

            // === 4. Connect tab click ===
            modsTab.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NButton>(delegate
                {
                    try { tabManager.Call("SwitchTabTo", modsTab); }
                    catch (Exception e) { MainFile.Log.Error($"Tab switch failed: {e}"); }
                }));

            // === 5. i18n tracking ===
            _allTabs.Add(new WeakReference<NSettingsTab>(modsTab));
            if (!_i18nSubscribed)
            {
                I18n.Changed += OnLanguageChanged;
                _i18nSubscribed = true;
            }

            // === 6. Populate ===
            PopulateInto(contentContainer);
            RefreshPanelSize(modsPanel, contentContainer);

            MainFile.Log.Info("Mods tab injected into settings screen!");
        }
        catch (Exception e)
        {
            MainFile.Log.Error($"Failed to inject Mods tab: {e}");
        }
    }

    private static void PositionNewTab(IDictionary tabs, NSettingsTab modsTab)
    {
        var existingTabs = new List<NSettingsTab>();
        foreach (DictionaryEntry entry in tabs)
            existingTabs.Add((NSettingsTab)entry.Key);

        if (existingTabs.Count < 2) return;

        float spacing = existingTabs[1].Position.X - existingTabs[0].Position.X;
        var lastTab = existingTabs.Last();

        modsTab.Position = new Vector2(lastTab.Position.X + spacing, lastTab.Position.Y);
        modsTab.Size = existingTabs[0].Size;

        var tabManager = modsTab.GetParent<Control>();
        float rightEdge = modsTab.Position.X + modsTab.Size.X;
        if (rightEdge > tabManager.Size.X && tabManager.Size.X > 0)
        {
            int totalTabs = existingTabs.Count + 1;
            float tabWidth = existingTabs[0].Size.X;
            float newSpacing = tabManager.Size.X / totalTabs;
            float startX = (newSpacing - tabWidth) / 2f;

            for (int i = 0; i < existingTabs.Count; i++)
                existingTabs[i].Position = new Vector2(startX + newSpacing * i, existingTabs[i].Position.Y);

            modsTab.Position = new Vector2(startX + newSpacing * existingTabs.Count, existingTabs[0].Position.Y);
        }
    }

    private static void OnLanguageChanged()
    {
        for (int i = _allTabs.Count - 1; i >= 0; i--)
        {
            if (!_allTabs[i].TryGetTarget(out var tab) || !GodotObject.IsInstanceValid(tab))
            {
                _allTabs.RemoveAt(i);
                continue;
            }
            tab.SetLabel(I18n.TabMods);
        }
        RefreshUI();
    }

    internal static void RefreshUI()
    {
        _liveBindings.Clear();

        for (int i = _allContainers.Count - 1; i >= 0; i--)
        {
            if (!_allContainers[i].TryGetTarget(out var container) ||
                !GodotObject.IsInstanceValid(container))
            {
                _allContainers.RemoveAt(i);
                continue;
            }

            foreach (var child in container.GetChildren().ToArray())
            {
                container.RemoveChild(child);
                child.Free();
            }

            PopulateInto(container);
            RefreshPanelSize(container.GetParent<NSettingsPanel>(), container);
        }
    }

    internal static void NotifyValueChanged(string modId, string key, object value)
    {
        var bindingKey = (modId, key);
        if (!_liveBindings.TryGetValue(bindingKey, out var bindings))
            return;

        for (int i = bindings.Count - 1; i >= 0; i--)
        {
            try
            {
                if (!bindings[i].Apply(value))
                    bindings.RemoveAt(i);
            }
            catch (Exception e)
            {
                MainFile.Log.Error($"Live config UI update failed [{modId}.{key}]: {e}");
                bindings.RemoveAt(i);
            }
        }

        if (bindings.Count == 0)
            _liveBindings.Remove(bindingKey);
    }

    private static void RegisterLiveBinding(string modId, string key, Func<object, bool> apply)
    {
        var bindingKey = (modId, key);
        if (!_liveBindings.TryGetValue(bindingKey, out var bindings))
        {
            bindings = new List<LiveBinding>();
            _liveBindings[bindingKey] = bindings;
        }

        bindings.Add(new LiveBinding(apply));
    }

    // ─── Content Population ──────────────────────────────────────

    private static void PopulateInto(VBoxContainer contentContainer)
    {
        if (contentContainer == null) return;

        var registrations = ModConfigManager.Registrations;
        if (registrations.Count == 0)
        {
            AddCenteredLabel(contentContainer, I18n.NoConfigs);
            return;
        }

        var target = contentContainer;

        bool first = true;
        foreach (var (modId, reg) in registrations)
        {
            // ── Mod separator (thicker, between mods) ──
            if (!first)
                AddModSeparator(target);
            first = false;

            bool isCollapsed = _collapsedMods.Contains(modId);

            // ── Mod header row (▼/▶ Name ─── Reset) with background ──
            string localizedName = reg.GetLocalizedName();
            var (headerPanel, headerLabel) = AddModHeaderWithReset(target, modId, localizedName, isCollapsed);

            // ── Entries container (collapsible) ──
            var entriesBox = new VBoxContainer { Name = $"Entries_{modId}" };
            target.AddChild(entriesBox);

            // Wire up collapse toggle with direct reference (no node lookup)
            var capturedEntriesBox = entriesBox;
            var capturedLabel = headerLabel;
            var capturedModId = modId; // for re-resolve on language change
            var capturedReg = reg;
            headerPanel.GuiInput += (InputEvent @event) =>
            {
                if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    if (_collapsedMods.Contains(modId))
                        _collapsedMods.Remove(modId);
                    else
                        _collapsedMods.Add(modId);

                    bool collapsed = _collapsedMods.Contains(modId);
                    capturedEntriesBox.Visible = !collapsed;
                    capturedLabel.Text = $"{(collapsed ? "\u25b6" : "\u25bc")}  {capturedReg.GetLocalizedName()}";
                    RefreshPanelSize(contentContainer.GetParent<NSettingsPanel>(), contentContainer);
                }
            };

            for (int i = 0; i < reg.Entries.Length; i++)
            {
                var entry = reg.Entries[i];
                switch (entry.Type)
                {
                    case ConfigType.Header:
                        AddSectionHeader(entriesBox, ResolveLabel(entry));
                        break;
                    case ConfigType.Separator:
                        AddSeparator(entriesBox);
                        break;
                    case ConfigType.Toggle:
                        AddToggle(entriesBox, modId, entry);
                        break;
                    case ConfigType.Slider:
                        AddSlider(entriesBox, modId, entry);
                        break;
                    case ConfigType.Dropdown:
                        AddDropdown(entriesBox, modId, entry);
                        break;
                    case ConfigType.KeyBind:
                        AddKeyBind(entriesBox, modId, entry);
                        break;
                    case ConfigType.TextInput:
                        AddTextInput(entriesBox, modId, entry);
                        break;
                }

                // Add thin row separator after each config entry (not after Header/Separator types)
                if (entry.Type is not (ConfigType.Header or ConfigType.Separator))
                    AddRowSeparator(entriesBox);
            }

            // Apply collapsed state
            if (isCollapsed)
                entriesBox.Visible = false;
        }
    }

    private static void RefreshPanelSize(NSettingsPanel panel, VBoxContainer contentContainer)
    {
        var parent = panel.GetParent<Control>();
        var minimumSize = contentContainer.GetMinimumSize();
        float height = minimumSize.Y + 50f >= parent.Size.Y
            ? minimumSize.Y + parent.Size.Y * 0.4f
            : minimumSize.Y;

        panel.Size = new Vector2(panel.Size.X, height);
    }

    // ─── Label Resolution ────────────────────────────────────────

    private static string ResolveLabel(ConfigEntry entry)
    {
        if (entry.Labels is { Count: > 0 })
        {
            var resolved = ResolveLangDict(entry.Labels);
            if (resolved != null) return resolved;
        }
        if (!string.IsNullOrEmpty(entry.LabelKey))
            return I18n.Get(entry.LabelKey, entry.Label);
        return entry.Label;
    }

    private static string ResolveDescription(ConfigEntry entry)
    {
        if (entry.Descriptions is { Count: > 0 })
        {
            var resolved = ResolveLangDict(entry.Descriptions);
            if (resolved != null) return resolved;
        }
        if (!string.IsNullOrEmpty(entry.DescriptionKey))
            return I18n.Get(entry.DescriptionKey, entry.Description);
        return entry.Description;
    }

    private static string? ResolveLangDict(Dictionary<string, string> dict)
    {
        string lang = I18n.CurrentLang;
        if (dict.TryGetValue(lang, out var exact))
            return exact;
        foreach (var (key, value) in dict)
            if (lang.StartsWith(key) || key.StartsWith(lang))
                return value;
        return null;
    }

    private static string ResolveDropdownOption(ConfigEntry entry, int index)
    {
        if (entry.OptionsKeys != null && index < entry.OptionsKeys.Length && entry.Options != null && index < entry.Options.Length)
            return I18n.Get(entry.OptionsKeys[index], entry.Options[index]);
        if (entry.Options != null && index < entry.Options.Length)
            return entry.Options[index];
        return "";
    }

    // ─── Description Helper ──────────────────────────────────────

    private static void AddDescriptionIfPresent(VBoxContainer parent, ConfigEntry entry)
    {
        var desc = ResolveDescription(entry);
        if (string.IsNullOrEmpty(desc)) return;

        var label = new Label
        {
            Text = $"      {desc}",
            CustomMinimumSize = new Vector2(0, 20),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeColorOverride("font_color", DimText);
        label.AddThemeFontSizeOverride("font_size", 15);
        parent.AddChild(label);
    }

    // ─── Tooltip Helper ──────────────────────────────────────────

    private static void ApplyTooltip(Control control, ConfigEntry entry)
    {
        var desc = ResolveDescription(entry);
        if (!string.IsNullOrEmpty(desc))
            control.TooltipText = desc;
    }

    // ─── Separator Helpers ───────────────────────────────────────

    /// <summary>Thicker separator between mods with more spacing.</summary>
    private static void AddModSeparator(VBoxContainer parent)
    {
        parent.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

        var sep = new HSeparator { CustomMinimumSize = new Vector2(0, 4) };
        sep.AddThemeConstantOverride("separation", 4);
        var sepStyle = new StyleBoxFlat
        {
            BgColor = new Color(ModHeaderBg, 0.7f),
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        };
        sep.AddThemeStyleboxOverride("separator", sepStyle);
        parent.AddChild(sep);

        parent.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
    }

    /// <summary>Thin row separator between config items, matching game palette.</summary>
    private static void AddRowSeparator(VBoxContainer parent)
    {
        var sep = new HSeparator { CustomMinimumSize = new Vector2(0, 1) };
        sep.AddThemeConstantOverride("separation", 1);
        var sepStyle = new StyleBoxFlat
        {
            BgColor = RowSeparatorColor,
            ContentMarginTop = 0,
            ContentMarginBottom = 0,
        };
        sep.AddThemeStyleboxOverride("separator", sepStyle);
        parent.AddChild(sep);
    }

    // ─── UI Factory Methods ──────────────────────────────────────

    /// <summary>Returns (bgPanel, label) so the caller can wire up collapse toggle.</summary>
    private static (PanelContainer, Label) AddModHeaderWithReset(VBoxContainer parent, string modId, string text, bool isCollapsed)
    {
        // Background panel for mod header — MouseFilter.Stop so it receives clicks
        var bgPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        bgPanel.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        var bgStyle = new StyleBoxFlat
        {
            BgColor = ModHeaderBg,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        bgPanel.AddThemeStyleboxOverride("panel", bgStyle);

        var hbox = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 40),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        string arrow = isCollapsed ? "\u25b6" : "\u25bc";
        var label = new Label
        {
            Text = $"{arrow}  {text}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", CreamGold);
        label.AddThemeFontSizeOverride("font_size", 20);

        var resetBtn = new Button
        {
            Text = I18n.ResetDefaults,
            CustomMinimumSize = new Vector2(0, 28),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        resetBtn.AddThemeColorOverride("font_color", ResetColor);
        resetBtn.AddThemeFontSizeOverride("font_size", 14);
        resetBtn.Pressed += () =>
        {
            if (ModConfigManager.ResetToDefaults(modId))
                RefreshUI();
        };

        hbox.AddChild(label);
        hbox.AddChild(resetBtn);
        bgPanel.AddChild(hbox);
        parent.AddChild(bgPanel);
        return (bgPanel, label);
    }

    private static void AddSectionHeader(VBoxContainer parent, string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, 32),
        };
        label.AddThemeColorOverride("font_color", DimText);
        label.AddThemeFontSizeOverride("font_size", 20);
        parent.AddChild(label);
    }

    private static void AddCenteredLabel(VBoxContainer parent, string text)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 50),
        };
        label.AddThemeColorOverride("font_color", DimText);
        label.AddThemeFontSizeOverride("font_size", 16);
        parent.AddChild(label);
    }

    private static void AddSeparator(VBoxContainer parent)
    {
        var sep = new HSeparator { CustomMinimumSize = new Vector2(0, 8) };
        sep.AddThemeConstantOverride("separation", 6);
        parent.AddChild(sep);
    }

    // ─── Toggle ──────────────────────────────────────────────────

    private static void AddToggle(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);

        var toggle = new CheckButton
        {
            ButtonPressed = ModConfigManager.GetValue<bool>(modId, entry.Key),
        };
        var guard = new UiUpdateGuard();
        var toggleRef = new WeakReference<CheckButton>(toggle);

        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(toggleRef, out var liveToggle))
                return false;
            if (!TryConvertValue(value, out bool pressed))
                return true;
            if (liveToggle.ButtonPressed == pressed)
                return true;

            guard.Suppress = true;
            liveToggle.ButtonPressed = pressed;
            guard.Suppress = false;
            return true;
        });

        toggle.Toggled += pressed =>
        {
            if (guard.Suppress)
                return;
            ModConfigManager.SetValue(modId, entry.Key, pressed);
        };

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(toggle);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── Slider (with Format + debounced save) ───────────────────

    private static void AddSlider(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 15);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);

        var slider = new HSlider
        {
            MinValue = entry.Min,
            MaxValue = entry.Max,
            Step = entry.Step,
            CustomMinimumSize = new Vector2(200, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        slider.Value = ModConfigManager.GetValue<float>(modId, entry.Key);

        string fmt = entry.Format ?? "F0";
        var valueLabel = new Label
        {
            CustomMinimumSize = new Vector2(70, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = slider.Value.ToString(fmt),
        };
        valueLabel.AddThemeColorOverride("font_color", CreamGold);
        valueLabel.AddThemeFontSizeOverride("font_size", 18);

        var guard = new UiUpdateGuard();
        var sliderRef = new WeakReference<HSlider>(slider);
        var valueLabelRef = new WeakReference<Label>(valueLabel);

        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(sliderRef, out var liveSlider))
                return false;
            if (!TryConvertValue(value, out float numericValue))
                return true;

            if (Math.Abs(liveSlider.Value - numericValue) > 0.0001d)
            {
                guard.Suppress = true;
                liveSlider.Value = numericValue;
                guard.Suppress = false;
            }

            if (TryGetLiveTarget(valueLabelRef, out var liveValueLabel))
                liveValueLabel.Text = numericValue.ToString(fmt);

            return true;
        });

        slider.ValueChanged += value =>
        {
            valueLabel.Text = ((float)value).ToString(fmt);
            if (guard.Suppress)
                return;
            ModConfigManager.SetValue(modId, entry.Key, (float)value);
        };

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(slider);
        hbox.AddChild(valueLabel);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── Dropdown (with i18n OptionsKeys) ────────────────────────

    private static void AddDropdown(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);

        var dropdown = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };

        var currentValue = ModConfigManager.GetValue<string>(modId, entry.Key);
        if (entry.Options != null)
        {
            for (int i = 0; i < entry.Options.Length; i++)
            {
                dropdown.AddItem(ResolveDropdownOption(entry, i), i);
                if (entry.Options[i] == currentValue)
                    dropdown.Selected = i;
            }
        }

        var guard = new UiUpdateGuard();
        var dropdownRef = new WeakReference<OptionButton>(dropdown);

        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(dropdownRef, out var liveDropdown))
                return false;
            if (!TryConvertValue(value, out string selectedValue) || entry.Options == null)
                return true;

            int selectedIndex = Array.IndexOf(entry.Options, selectedValue);
            if (selectedIndex < 0 || liveDropdown.Selected == selectedIndex)
                return true;

            guard.Suppress = true;
            liveDropdown.Selected = selectedIndex;
            guard.Suppress = false;
            return true;
        });

        dropdown.ItemSelected += index =>
        {
            if (guard.Suppress)
                return;
            if (entry.Options != null && index < entry.Options.Length)
                ModConfigManager.SetValue(modId, entry.Key, entry.Options[index]);
        };

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(dropdown);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    // ─── KeyBind ─────────────────────────────────────────────────

    private static void AddKeyBind(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);

        long currentKey = ModConfigManager.GetValue<long>(modId, entry.Key);
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(140, 32),
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = I18n.KeyBindTooltip,
        };
        btn.AddThemeFontSizeOverride("font_size", 17);
        ApplyKeyBindButtonState(btn, currentKey);

        var buttonRef = new WeakReference<Button>(btn);
        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(buttonRef, out var liveButton))
                return false;
            if (!TryConvertValue(value, out long keyCode))
                return true;
            if (_activeKeyBindButton != null &&
                GodotObject.IsInstanceValid(_activeKeyBindButton) &&
                ReferenceEquals(_activeKeyBindButton, liveButton) &&
                _activeKeyBindModId == modId &&
                _activeKeyBindEntry?.Key == entry.Key)
            {
                return true;
            }

            ApplyKeyBindButtonState(liveButton, keyCode);
            return true;
        });

        btn.Pressed += () =>
        {
            if (_activeKeyBindButton != null && GodotObject.IsInstanceValid(_activeKeyBindButton))
            {
                // Cancel previous listening
                long prevKey = ModConfigManager.GetValue<long>(_activeKeyBindModId, _activeKeyBindEntry!.Key);
                ApplyKeyBindButtonState(_activeKeyBindButton, prevKey);
                CancelKeyCapture();
            }

            _activeKeyBindButton = btn;
            _activeKeyBindModId = modId;
            _activeKeyBindEntry = entry;
            btn.Text = I18n.PressAnyKey;
            btn.AddThemeColorOverride("font_color", KeyBindListening);

            StartKeyCapture(modId, entry, btn);
        };

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(btn);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    private static string KeyToDisplayString(long keyCode)
    {
        if (keyCode == 0) return I18n.KeyUnbound;
        var key = (Key)keyCode;
        return OS.GetKeycodeString(key);
    }

    private static void ApplyKeyBindButtonState(Button button, long keyCode)
    {
        button.Text = KeyToDisplayString(keyCode);
        button.TooltipText = I18n.KeyBindTooltip;

        if (keyCode == 0)
            button.AddThemeColorOverride("font_color", DimText);
        else
            button.RemoveThemeColorOverride("font_color");
    }

    // Temporary Node used to capture _UnhandledKeyInput
    private static KeyCaptureNode? _captureNode;

    private static void StartKeyCapture(string modId, ConfigEntry entry, Button btn)
    {
        CancelKeyCapture();
        _captureNode = new KeyCaptureNode();
        _captureNode.OnKeyCaptured = keyCode =>
        {
            ModConfigManager.SetValue(modId, entry.Key, keyCode);
            ApplyKeyBindButtonState(btn, keyCode);
            _activeKeyBindButton = null;
            _activeKeyBindModId = "";
            _activeKeyBindEntry = null;
            CancelKeyCapture();
        };
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_captureNode);
    }

    private static void CancelKeyCapture()
    {
        if (_captureNode != null && GodotObject.IsInstanceValid(_captureNode))
        {
            _captureNode.QueueFree();
            _captureNode = null;
        }
    }

    // ─── TextInput ───────────────────────────────────────────────

    private static void AddTextInput(VBoxContainer parent, string modId, ConfigEntry entry)
    {
        var hbox = new HBoxContainer { CustomMinimumSize = new Vector2(0, 45) };
        hbox.AddThemeConstantOverride("separation", 20);

        var label = new Label
        {
            Text = $"  {ResolveLabel(entry)}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeColorOverride("font_color", TextColor);
        label.AddThemeFontSizeOverride("font_size", 20);

        var lineEdit = new LineEdit
        {
            Text = ModConfigManager.GetValue<string>(modId, entry.Key),
            MaxLength = entry.MaxLength,
            PlaceholderText = entry.Placeholder,
            CustomMinimumSize = new Vector2(200, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        lineEdit.AddThemeFontSizeOverride("font_size", 17);

        var lineEditRef = new WeakReference<LineEdit>(lineEdit);
        RegisterLiveBinding(modId, entry.Key, value =>
        {
            if (!TryGetLiveTarget(lineEditRef, out var liveLineEdit))
                return false;
            if (liveLineEdit.HasFocus())
                return true;
            if (!TryConvertValue(value, out string textValue))
                return true;
            if (liveLineEdit.Text == textValue)
                return true;

            liveLineEdit.Text = textValue;
            return true;
        });

        // Save on focus lost or Enter pressed
        lineEdit.TextSubmitted += text => ModConfigManager.SetValue(modId, entry.Key, text);
        lineEdit.FocusExited += () => ModConfigManager.SetValue(modId, entry.Key, lineEdit.Text);

        ApplyTooltip(hbox, entry);
        hbox.AddChild(label);
        hbox.AddChild(lineEdit);
        parent.AddChild(hbox);
        AddDescriptionIfPresent(parent, entry);
    }

    private static bool TryGetLiveTarget<T>(WeakReference<T> reference, out T target) where T : GodotObject
    {
        if (reference.TryGetTarget(out var liveTarget) && GodotObject.IsInstanceValid(liveTarget))
        {
            target = liveTarget;
            return true;
        }

        target = null!;
        return false;
    }

    private static bool TryConvertValue<T>(object value, out T converted)
    {
        try
        {
            if (value is T typedValue)
            {
                converted = typedValue;
                return true;
            }

            object? changedValue = Convert.ChangeType(value, typeof(T));
            if (changedValue is T finalValue)
            {
                converted = finalValue;
                return true;
            }
        }
        catch
        {
        }

        converted = default!;
        return false;
    }
}
