using System.Text.Json.Nodes;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VRChatVoiceInput.Core.Asr;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.App;

public partial class NativeMainWindow
{
    private UIElement BuildProfilesPage()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var sidebar = new DockPanel { Margin = new Thickness(12, 18, 12, 18) };
        var sidebarHeader = new DockPanel { LastChildFill = true, Margin = new Thickness(5, 0, 5, 12) };
        var sidebarActions = new StackPanel { Orientation = Orientation.Horizontal };
        var automatic = IconButton(MaterialIconPaths.Refresh, T("Resume automatic application routing"), async (_, _) =>
        {
            await SaveNowAsync();
            if (_dirty) return;
            try
            {
                await _controller.SetProfileOverrideAsync(null);
                UpdateTopbar();
                BuildCurrentPage();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        });
        automatic.IsEnabled = !string.IsNullOrWhiteSpace(_controller.ProfileOverride);
        automatic.Margin = new Thickness(0, 0, 6, 0);
        sidebarActions.Children.Add(automatic);
        sidebarActions.Children.Add(IconButton(MaterialIconPaths.Plus, T("Add profile"), OnAddProfile));
        DockPanel.SetDock(sidebarActions, Dock.Right);
        sidebarHeader.Children.Add(sidebarActions);
        sidebarHeader.Children.Add(new TextBlock
        {
            Text = T("Profiles"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(sidebarHeader, Dock.Top);
        sidebar.Children.Add(sidebarHeader);

        var list = new StackPanel();
        for (var index = 0; index < Profiles.Count; index++)
        {
            var profileNode = Profiles[index]?.AsObject();
            var id = profileNode?["id"]?.GetValue<string>() ?? $"Profile {index + 1}";
            var itemIndex = index;
            var item = new Button
            {
                Style = (Style)FindResource("ProfileItemButtonStyle"),
                Tag = index == _selectedProfileIndex ? "active" : null
            };
            item.Click += (_, _) =>
            {
                _selectedProfileIndex = itemIndex;
                BuildCurrentPage();
            };

            var itemGrid = new Grid();
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition());
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var copy = new StackPanel();
            copy.Children.Add(new TextBlock
            {
                Text = id,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            copy.Children.Add(new TextBlock
            {
                Text = ProfileSummary(profileNode),
                FontSize = 10,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var badges = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            if (string.Equals(id, _controller.ProfileOverride, StringComparison.OrdinalIgnoreCase))
            {
                badges.Children.Add(new TextBlock { Text = T("Active"), FontSize = 10, Foreground = AccentBrush, FontWeight = FontWeights.SemiBold });
            }
            if (string.Equals(id, GetString("profiles.defaultProfileId"), StringComparison.OrdinalIgnoreCase))
            {
                badges.Children.Add(new TextBlock { Text = T("Default"), FontSize = 10, Foreground = AccentBrush });
            }
            Grid.SetColumn(copy, 0);
            Grid.SetColumn(badges, 1);
            itemGrid.Children.Add(copy);
            itemGrid.Children.Add(badges);
            item.Content = itemGrid;
            list.Children.Add(item);
        }
        sidebar.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        });
        var sidebarSurface = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xF6)),
            Child = sidebar
        };
        Grid.SetColumn(sidebarSurface, 0);
        grid.Children.Add(sidebarSurface);
        var separator = new Border { Background = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)) };
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);

        var profile = CurrentProfile;
        if (profile is null)
        {
            var empty = new TextBlock
            {
                Text = T("At least one profile is required."),
                Foreground = DangerBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(empty, 2);
            grid.Children.Add(empty);
            return grid;
        }

        EnsureProfileDefaults(profile);
        var profileContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        profileContent.Children.Add(BuildProfileHeader(profile));
        profileContent.Children.Add(BuildProfileTabs());
        profileContent.Children.Add(_profileTab switch
        {
            "processing" => BuildProfileProcessing(profile),
            "output" => BuildProfileOutput(profile),
            _ => BuildProfileInput(profile)
        });
        var scroll = Page(profileContent, 820, 0);
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
        return grid;
    }

    private UIElement BuildProfileHeader(JsonObject profile)
    {
        var id = profile["id"]?.GetValue<string>() ?? string.Empty;
        var isDefault = string.Equals(GetString("profiles.defaultProfileId"), id, StringComparison.OrdinalIgnoreCase);
        var enabled = new CheckBox
        {
            IsChecked = profile["enabled"]?.GetValue<bool>() != false,
            IsEnabled = !isDefault,
            Width = 34,
            ToolTip = isDefault ? T("The default profile must remain enabled.") : T("Enable profile")
        };
        enabled.Checked += (_, _) => { profile["enabled"] = true; MarkDirty(); };
        enabled.Unchecked += (_, _) => { profile["enabled"] = false; MarkDirty(); };

        var actions = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var use = ActionButton(T("Use profile"), async (_, _) =>
        {
            await SaveNowAsync();
            if (_dirty) return;
            try
            {
                await _controller.SetProfileOverrideAsync(profile["id"]?.GetValue<string>());
                UpdateTopbar();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        }, true, MaterialIconPaths.Play);
        var setDefault = ActionButton(T("Set default"), (_, _) =>
        {
            Set("profiles.defaultProfileId", profile["id"]?.GetValue<string>() ?? string.Empty);
            MarkDirty();
        }, icon: MaterialIconPaths.Check);
        var runtime = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_controller.ProfileOverride)
                ? T("Runtime: automatic")
                : $"{T("Runtime")}: {_controller.ProfileOverride}",
            FontSize = 10,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var duplicate = IconButton(MaterialIconPaths.ContentCopy, T("Duplicate profile"), OnDuplicateProfile);
        var delete = IconButton(MaterialIconPaths.DeleteOutline, T("Delete profile"), OnDeleteProfile, true);
        delete.IsEnabled = profile["builtIn"]?.GetValue<bool>() != true;
        use.Margin = new Thickness(8, 0, 7, 0);
        setDefault.Margin = new Thickness(0, 0, 0, 0);
        duplicate.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(enabled, 0);
        Grid.SetColumn(use, 1);
        Grid.SetColumn(setDefault, 2);
        Grid.SetColumn(runtime, 3);
        Grid.SetColumn(duplicate, 4);
        Grid.SetColumn(delete, 5);
        actions.Children.Add(enabled);
        actions.Children.Add(use);
        actions.Children.Add(setDefault);
        actions.Children.Add(runtime);
        actions.Children.Add(duplicate);
        actions.Children.Add(delete);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 22, 0, 18),
            Child = actions
        };
    }

    private UIElement BuildProfileTabs()
    {
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 46
        };
        foreach (var (id, label) in new[]
        {
            ("input", T("Input")),
            ("processing", T("Processing")),
            ("output", T("Output"))
        })
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource("TabButtonStyle"),
                Tag = id == _profileTab ? "active" : null
            };
            button.Click += (_, _) =>
            {
                _profileTab = id;
                BuildCurrentPage();
            };
            tabs.Children.Add(button);
        }
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tabs
        };
    }

    private UIElement BuildProfileInput(JsonObject profile)
    {
        var basePath = $"profiles.items.{_selectedProfileIndex}";
        var content = new StackPanel();
        var id = profile["id"]?.GetValue<string>() ?? string.Empty;
        var name = new TextBox { Text = id };
        name.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                eventArgs.Handled = true;
            }
        };
        name.LostKeyboardFocus += (_, _) => RenameProfile(name, id);
        var processText = new TextBox
        {
            Text = ProfileSummary(profile),
            IsReadOnly = true
        };
        var processButton = IconButton(MaterialIconPaths.FileSearchOutline, T("Choose processes"), (_, _) => OpenProcessPicker(profile));
        var processRow = new Grid();
        processRow.ColumnDefinitions.Add(new ColumnDefinition());
        processRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        processRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        Grid.SetColumn(processText, 0);
        Grid.SetColumn(processButton, 2);
        processRow.Children.Add(processText);
        processRow.Children.Add(processButton);
        content.Children.Add(Section(
            T("Application"),
            T(profile["builtIn"]?.GetValue<bool>() == true ? "Built-in profile" : "Custom profile"),
            Field(T("Profile name"), name),
            Field(T("Process names"), processRow, T("Leave empty to use the application that is in the foreground when PTT is pressed."))));

        var modes = profile["input"]!["modes"]!.AsArray()
            .Select(node => node?.GetValue<string>())
            .Where(mode => !string.IsNullOrWhiteSpace(mode))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var triggerOptions = new[]
        {
            new Option("keyboard", T("Keyboard"), MaterialIconPaths.Keyboard,
                T("Listen for a global Windows keyboard chord.")),
            new Option("mouse", T("Mouse"), MaterialIconPaths.Mouse,
                T("Listen for a global Windows mouse button.")),
            new Option("xinput", "XInput", MaterialIconPaths.GamepadVariant,
                T("Poll an XInput controller button.")),
            new Option("steamvr", T("SteamVR"), MaterialIconPaths.Steam,
                T("Listen for the configured SteamVR controller action."))
        };
        var triggerFields = new StackPanel();
        for (var index = 0; index < triggerOptions.Length; index++)
        {
            var option = triggerOptions[index];
            var trigger = new StackPanel();
            trigger.Children.Add(BuildTriggerModeToggle($"{basePath}.input.modes", option));
            if (modes.Contains(option.Value))
            {
                var binding = BuildTriggerBinding(profile, basePath, option.Value);
                binding.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 8));
                trigger.Children.Add(binding);
            }

            triggerFields.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE5, 0xE2)),
                BorderThickness = index == triggerOptions.Length - 1
                    ? new Thickness(0)
                    : new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 2, 0, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = trigger
            });
        }
        content.Children.Add(Section(T("Push to talk"), T("Enable one or more trigger types."), triggerFields));

        var microphones = new List<Option> { new(string.Empty, T("Default communications device")) };
        microphones.AddRange(GetMicrophones().Select(device => new Option(device.Id, device.Name)));
        var audioDevice = new ComboBox
        {
            ItemsSource = microphones,
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedValue = GetString($"{basePath}.audio.deviceId"),
            ToolTip = T("Choose the microphone used by this profile. The default option inherits the global device.")
        };
        if (audioDevice.SelectedIndex < 0) audioDevice.SelectedIndex = 0;
        audioDevice.SelectionChanged += (_, _) =>
        {
            if (_building || audioDevice.SelectedValue is not string value) return;
            Set($"{basePath}.audio.deviceId", string.IsNullOrWhiteSpace(value) ? null : value);
            MarkDirty();
        };
        content.Children.Add(Section(
            T("Audio"),
            string.Empty,
            TwoColumns(
                Field(T("Microphone"), audioDevice),
                Field(T("Minimum recording (ms)"), BoundNumberBox(
                    $"{basePath}.audio.minimumDurationMs",
                    100,
                    5000,
                    nullable: true,
                    toolTip: T("Recordings shorter than this value are discarded. Empty inherits the global setting."))))));
        return content;
    }

    private UIElement BuildTriggerBinding(JsonObject profile, string basePath, string mode)
    {
        var stack = new StackPanel();
        if (mode == "keyboard")
        {
            var keys = profile["input"]?["keyboard"]?["virtualKeys"]?.AsArray()
                .Select(node => node?.GetValue<int>() ?? 0).Where(value => value > 0).ToArray() ?? [];
            var row = BindingRow(FormatKeys(keys), async button =>
            {
                var capture = await _controller.CaptureKeyboardChordAsync();
                Set($"{basePath}.input.keyboard.virtualKeys", new JsonArray(capture.VirtualKeys.Select(value => JsonValue.Create(value)).ToArray()));
                MarkDirty(rebuild: true);
            }, T("Press and release the keyboard shortcut to bind it."));
            stack.Children.Add(Field(T("Keyboard"), row));
            stack.Children.Add(BoundCheckBox(
                T("Suppress trigger"),
                $"{basePath}.input.keyboard.suppressKey",
                toolTip: T("Prevent the selected keyboard shortcut from reaching the foreground application while this profile is active.")));
        }
        else if (mode == "mouse")
        {
            var row = BindingRow(GetString($"{basePath}.input.mouse.button", "x1"), async button =>
            {
                var capture = await _controller.CaptureMouseButtonAsync();
                Set($"{basePath}.input.mouse.button", capture.Button);
                MarkDirty(rebuild: true);
            }, T("Press and release the mouse button to bind it."));
            stack.Children.Add(Field(T("Mouse"), row));
            stack.Children.Add(BoundCheckBox(
                T("Suppress trigger"),
                $"{basePath}.input.mouse.suppressButton",
                toolTip: T("Prevent the selected mouse button from reaching the foreground application while this profile is active.")));
        }
        else if (mode == "xinput")
        {
            var userIndex = GetInt($"{basePath}.input.gamepad.userIndex");
            var mask = GetInt($"{basePath}.input.gamepad.buttonMask");
            var row = BindingRow($"Controller {userIndex + 1} · {FormatGamepadButton(mask)}", async button =>
            {
                var capture = await _controller.CaptureGamepadButtonAsync();
                Set($"{basePath}.input.gamepad.userIndex", capture.UserIndex);
                Set($"{basePath}.input.gamepad.buttonMask", capture.ButtonMask);
                MarkDirty(rebuild: true);
            }, T("Press a button on any connected XInput controller to bind it."));
            stack.Children.Add(Field(T("Gamepad"), row));
            stack.Children.Add(Field(T("Poll interval (ms)"), BoundNumberBox(
                $"{basePath}.input.gamepad.pollIntervalMs",
                1,
                1000,
                toolTip: T("Lower polling intervals respond faster but wake the CPU more often."))));
        }
        else
        {
            var status = _controller.GetSteamVrStatus();
            stack.Children.Add(Notice(LocalizeSteamVrStatus(status.Message), !status.Connected));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            var refresh = ActionButton(
                T("Refresh SteamVR"),
                (_, _) => BuildCurrentPage(),
                icon: MaterialIconPaths.Refresh);
            var bindings = ActionButton(T("Controller bindings"), async (_, _) =>
            {
                try { await _controller.OpenSteamVrBindingsAsync(); }
                catch (Exception exception) { ShowError(exception); }
            }, icon: MaterialIconPaths.Steam);
            bindings.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(refresh);
            buttons.Children.Add(bindings);
            stack.Children.Add(buttons);
            stack.Children.Add(Field(T("Poll interval (ms)"), BoundNumberBox(
                $"{basePath}.input.steamVr.pollIntervalMs",
                1,
                1000,
                toolTip: T("Lower polling intervals respond faster but wake the CPU more often."))));
        }
        return stack;
    }

    private CheckBox BuildTriggerModeToggle(string path, Option option)
    {
        var enabledModes = GetNode(path)?.AsArray()
            .Select(node => node?.GetValue<string>())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        if (option.Icon is not null)
        {
            label.Children.Add(MaterialIcon(
                option.Icon,
                16,
                fill: MutedBrush,
                margin: new Thickness(0, 0, 8, 0)));
        }
        label.Children.Add(new TextBlock
        {
            Text = option.Label,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var updating = false;
        var toggle = new CheckBox
        {
            Content = label,
            IsChecked = enabledModes.Contains(option.Value),
            ToolTip = $"{option.ToolTip} {T("Enable or disable this trigger without changing its binding.")}"
        };
        void Apply(bool enabled)
        {
            if (_building || updating)
            {
                return;
            }

            var current = GetNode(path)?.AsArray()
                .Select(node => node?.GetValue<string>())
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!enabled && current.Count <= 1 && current.Contains(option.Value))
            {
                updating = true;
                toggle.IsChecked = true;
                updating = false;
                ShowToast(T("At least one trigger type must remain enabled."), true);
                return;
            }

            if (enabled)
            {
                current.Add(option.Value);
            }
            else
            {
                current.Remove(option.Value);
            }

            Set(path, new JsonArray(
                new[] { "keyboard", "mouse", "xinput", "steamvr" }
                    .Where(current.Contains)
                    .Select(mode => JsonValue.Create(mode))
                    .ToArray()));
            MarkDirty(rebuild: true);
        }

        toggle.Checked += (_, _) => Apply(true);
        toggle.Unchecked += (_, _) => Apply(false);
        return toggle;
    }

    private UIElement BuildProfileProcessing(JsonObject profile)
    {
        var basePath = $"profiles.items.{_selectedProfileIndex}";
        var provider = GetString($"{basePath}.recognition.provider", "sensevoice-gguf");
        var parsedConfiguration = AppConfiguration.Parse(_configuration.ToJsonString());
        var availability = AsrProviderFactory.CheckAvailability(parsedConfiguration.Asr)
            .First(status => status.Id == provider);
        var streamingEnabled = profile["recognition"]?["streamingEnabled"]?.GetValue<bool>() == true;
        var missingRequirements = availability.MissingFiles
            .Concat(streamingEnabled ? availability.StreamingMissingFiles : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var providerOptions = AsrProviderFactory.ProviderIds.Select(id =>
            new Option(id, id + (AsrProviderFactory.CheckAvailability(parsedConfiguration.Asr).First(status => status.Id == id).Available ? "" : $" ({T("Not installed")})")));
        var content = new StackPanel();
        content.Children.Add(Section(
            T("Recognition"),
            string.Empty,
            TwoColumns(
                Field(T("Provider"), BoundCombo($"{basePath}.recognition.provider", providerOptions, rebuild: true)),
                Field(T("Language"), BoundCombo($"{basePath}.recognition.language", new[]
                {
                    new Option("auto", T("Auto detect")), new Option("zh", T("Chinese")),
                    new Option("en", T("English")), new Option("ja", T("Japanese")), new Option("ko", "한국어")
                }))),
            missingRequirements.Length == 0
                ? new Border()
                : Notice($"{T("Missing files")}: {string.Join("; ", missingRequirements.Select(FormatRequirement))}", true),
            BoundCheckBox(T("Streaming recognition"), $"{basePath}.recognition.streamingEnabled"),
            BuildHotwordField(profile, basePath, availability.SupportsTerminologyHints)));
        return content;
    }

    private UIElement BuildHotwordField(JsonObject profile, string basePath, bool supported)
    {
        var text = string.Join(Environment.NewLine,
            profile["recognition"]?["hotwords"]?.AsArray().Select(node => node?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            MinHeight = 78,
            IsEnabled = supported,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        box.TextChanged += (_, _) =>
        {
            if (_building) return;
            var values = box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            Set($"{basePath}.recognition.hotwords", new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray()));
            MarkDirty();
        };
        var field = new StackPanel();
        field.Children.Add(Field(T("Hotwords"), box));
        if (!supported)
        {
            field.Children.Add(Notice(TF(
                "Hotwords are unavailable for {0}.",
                GetString($"{basePath}.recognition.provider")), true));
        }
        return field;
    }

    private UIElement BuildProfileOutput(JsonObject profile)
    {
        var basePath = $"profiles.items.{_selectedProfileIndex}";
        var outputMode = GetString($"{basePath}.output.mode", "captured-window");
        var content = new StackPanel();
        content.Children.Add(Section(
            T("Output"),
            T("Text route and submission"),
            Field(T("Output"), BoundCombo($"{basePath}.output.mode", new[]
            {
                new Option("captured-window", T("Window")),
                new Option("vrchat-osc", "VRChat OSC")
            }, rebuild: true, toolTip: T("Choose whether text is injected into the captured window or sent through VRChat OSC.")))));

        if (outputMode == "vrchat-osc")
        {
            content.Children.Add(Section(
                "VRChat OSC",
                string.Empty,
                TwoColumns(
                    Field(T("Host"), BoundTextBox($"{basePath}.output.vrChat.host")),
                    Field(T("Port"), BoundNumberBox($"{basePath}.output.vrChat.port", 1, 65535))),
                TwoColumns(
                    Field(T("Character limit"), BoundNumberBox(
                        $"{basePath}.output.vrChat.maxChatboxCharacters",
                        1,
                        144,
                        toolTip: T("Limit each VRChat Chatbox message to this many characters."))),
                    BoundCheckBox(
                        T("Send immediately"),
                        $"{basePath}.output.vrChat.sendImmediately",
                        toolTip: T("Submit Chatbox text immediately instead of leaving it in the input field.")))));
            return content;
        }

        content.Children.Add(Notice(
            T("Window output uses synthetic keyboard input for text entry and optional open or submit hotkeys. Anti-cheat software may detect input injection and could suspend or ban the account. Use this output mode only where permitted."),
            true));

        var openMode = GetString($"{basePath}.output.windows.openInput.mode", "none");
        var submitMode = GetString($"{basePath}.output.windows.submission.mode", "none");
        content.Children.Add(Section(
            T("Open input"),
            string.Empty,
            Field(T("Open input"), BoundCombo($"{basePath}.output.windows.openInput.mode", new[]
            {
                new Option("none", T("None")), new Option("hotkey", T("Hotkey"))
            }, rebuild: true, toolTip: T("Optionally send a keyboard shortcut before inserting recognized text."))),
            openMode == "hotkey"
                ? TwoColumns(
                    Field(T("Capture keys"), BuildOutputChord(profile, $"{basePath}.output.windows.openInput.virtualKeys")),
                    Field(T("Open delay (ms)"), BoundNumberBox(
                        $"{basePath}.output.windows.openInputDelayMs",
                        0,
                        5000,
                        toolTip: T("Wait this long after opening the input field before inserting text."))))
                : new Border()));
        content.Children.Add(Section(
            T("Text input"),
            string.Empty,
            Field(T("Text input"), BoundCombo($"{basePath}.output.windows.textInputMethod", new[]
            {
                new Option("clipboard-paste", T("Clipboard paste")),
                new Option("unicode-send-input", T("Unicode input")),
                new Option("keyboard", T("Keyboard events"))
            }, toolTip: T("Choose how recognized text is delivered to the captured window."))),
            BoundCheckBox(
                T("Require same foreground window"),
                $"{basePath}.output.windows.requireSameForeground",
                toolTip: T("Cancel output if focus moves to a different window after recording starts."))));
        content.Children.Add(Section(
            T("Submit"),
            string.Empty,
            Field(T("Submit"), BoundCombo($"{basePath}.output.windows.submission.mode", new[]
            {
                new Option("none", T("None")), new Option("hotkey", T("Hotkey"))
            }, rebuild: true, toolTip: T("Optionally send a keyboard shortcut after inserting recognized text."))),
            submitMode == "hotkey"
                ? Field(T("Capture keys"), BuildOutputChord(profile, $"{basePath}.output.windows.submission.virtualKeys"))
                : new Border()));
        return content;
    }

    private UIElement BuildOutputChord(JsonObject profile, string path)
    {
        var keys = GetNode(path)?.AsArray().Select(node => node?.GetValue<int>() ?? 0).Where(value => value > 0).ToArray() ?? [];
        return BindingRow(FormatKeys(keys), async button =>
        {
            var capture = await _controller.CaptureKeyboardChordAsync();
            Set(path, new JsonArray(capture.VirtualKeys.Select(value => JsonValue.Create(value)).ToArray()));
            MarkDirty(rebuild: true);
        }, T("Press and release the keyboard shortcut to bind it."));
    }

    private Grid BindingRow(string value, Func<Button, Task> capture, string? toolTip = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBox { Text = value, IsReadOnly = true, ToolTip = toolTip };
        var button = new Button
        {
            Content = T("Capture binding"),
            Style = (Style)FindResource("ActionButtonStyle"),
            ToolTip = toolTip
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await capture(button); }
            catch (Exception exception) { ShowError(exception); button.IsEnabled = true; }
        };
        button.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(button, 1);
        grid.Children.Add(text);
        grid.Children.Add(button);
        return grid;
    }

    private void EnsureProfileDefaults(JsonObject profile)
    {
        profile["enabled"] ??= true;
        profile["match"] ??= new JsonObject { ["processNames"] = new JsonArray() };
        profile["audio"] ??= new JsonObject { ["deviceId"] = null, ["minimumDurationMs"] = null };
        profile["recognition"] ??= new JsonObject
        {
            ["provider"] = GetString("asr.provider", "sensevoice-gguf"), ["language"] = "auto",
            ["hotwords"] = new JsonArray(), ["streamingEnabled"] = false
        };
        profile["input"] ??= new JsonObject();
        var input = profile["input"]!.AsObject();
        if (input["modes"] is not JsonArray { Count: > 0 })
        {
            input["modes"] = new JsonArray(input["mode"]?.GetValue<string>() ?? "keyboard");
        }
        input.Remove("mode");
        input["keyboard"] ??= new JsonObject { ["virtualKeys"] = new JsonArray(119), ["suppressKey"] = false };
        input["mouse"] ??= new JsonObject { ["button"] = "x1", ["suppressButton"] = false };
        input["gamepad"] ??= new JsonObject { ["userIndex"] = 0, ["buttonMask"] = 4096, ["pollIntervalMs"] = 8 };
        input["steamVr"] ??= new JsonObject { ["actionPath"] = "/actions/voiceinput/in/ptt", ["pollIntervalMs"] = 8 };
        profile["output"] ??= new JsonObject();
        var output = profile["output"]!.AsObject();
        output["mode"] ??= "captured-window";
        output["windows"] ??= new JsonObject
        {
            ["textInputMethod"] = "clipboard-paste",
            ["openInput"] = new JsonObject { ["mode"] = "none", ["virtualKeys"] = new JsonArray() },
            ["openInputDelayMs"] = 100,
            ["requireSameForeground"] = true,
            ["submission"] = new JsonObject { ["mode"] = "none", ["virtualKeys"] = new JsonArray() }
        };
        output["vrChat"] ??= new JsonObject
        {
            ["host"] = "127.0.0.1", ["port"] = 9000, ["sendImmediately"] = true, ["maxChatboxCharacters"] = 144
        };
    }

    private void RenameProfile(TextBox textBox, string oldId)
    {
        var next = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(next) || Profiles.OfType<JsonObject>().Any(profile =>
                !ReferenceEquals(profile, CurrentProfile) &&
                string.Equals(profile["id"]?.GetValue<string>(), next, StringComparison.OrdinalIgnoreCase)))
        {
            textBox.Text = oldId;
            ShowToast(T("Profile names must be unique and cannot be empty."), true);
            return;
        }
        if (next == oldId) return;
        CurrentProfile!["id"] = next;
        if (string.Equals(GetString("profiles.defaultProfileId"), oldId, StringComparison.OrdinalIgnoreCase))
        {
            Set("profiles.defaultProfileId", next);
        }
        MarkDirty(rebuild: true);
    }

    private void OnAddProfile(object sender, RoutedEventArgs eventArgs)
    {
        var template = CurrentProfile?.DeepClone().AsObject() ?? new JsonObject();
        template.Remove("builtInTemplate");
        template["builtIn"] = false;
        template["id"] = UniqueProfileName("New profile");
        Profiles.Add(template);
        _selectedProfileIndex = Profiles.Count - 1;
        MarkDirty(rebuild: true);
    }

    private void OnDuplicateProfile(object sender, RoutedEventArgs eventArgs)
    {
        if (CurrentProfile is null) return;
        var duplicate = CurrentProfile.DeepClone().AsObject();
        duplicate.Remove("builtInTemplate");
        duplicate["builtIn"] = false;
        duplicate["id"] = UniqueProfileName($"{CurrentProfile["id"]?.GetValue<string>()} copy");
        Profiles.Add(duplicate);
        _selectedProfileIndex = Profiles.Count - 1;
        MarkDirty(rebuild: true);
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs eventArgs)
    {
        if (Profiles.Count <= 1)
        {
            ShowToast(T("At least one profile is required."), true);
            return;
        }
        if (CurrentProfile?["builtIn"]?.GetValue<bool>() == true) return;
        var deleting = CurrentProfile?["id"]?.GetValue<string>();
        Profiles.RemoveAt(_selectedProfileIndex);
        _selectedProfileIndex = Math.Clamp(_selectedProfileIndex, 0, Profiles.Count - 1);
        if (string.Equals(GetString("profiles.defaultProfileId"), deleting, StringComparison.OrdinalIgnoreCase))
        {
            Set("profiles.defaultProfileId", Profiles[_selectedProfileIndex]?["id"]?.GetValue<string>() ?? string.Empty);
        }
        MarkDirty(rebuild: true);
    }

    private string UniqueProfileName(string prefix)
    {
        var names = Profiles.OfType<JsonObject>()
            .Select(profile => profile["id"]?.GetValue<string>() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(prefix)) return prefix;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{prefix} {suffix}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private string ProfileSummary(JsonObject? profile)
    {
        var processes = profile?["match"]?["processNames"]?.AsArray()
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? Array.Empty<string?>();
        return processes.Length == 0
            ? T("Current foreground application")
            : string.Join(", ", processes);
    }

    private void OpenProcessPicker(JsonObject profile)
    {
        var selected = profile["match"]?["processNames"]?.AsArray()
            .Select(node => NormalizeProcessName(node?.GetValue<string>() ?? string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applications = _controller.ListRunningApplications();
        var dialog = new Window
        {
            Owner = this,
            Title = T("Choose processes"),
            Width = 620,
            Height = 520,
            MinWidth = 480,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = ActionButton(T("Cancel"), (_, _) => dialog.DialogResult = false, icon: MaterialIconPaths.Close);
        var apply = ActionButton(T("Apply"), (_, _) => dialog.DialogResult = true, true, MaterialIconPaths.Check);
        apply.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var list = new ListBox { BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)) };
        foreach (var application in applications)
        {
            var process = NormalizeProcessName(application.ProcessName);
            var check = new CheckBox
            {
                Content = application.DisplayName,
                Tag = process,
                IsChecked = selected.Contains(process),
                Margin = new Thickness(8, 5, 8, 5)
            };
            list.Items.Add(check);
        }
        root.Children.Add(list);
        dialog.Content = root;
        if (dialog.ShowDialog() != true) return;
        var values = list.Items.OfType<CheckBox>().Where(check => check.IsChecked == true)
            .Select(check => JsonValue.Create($"{check.Tag}.exe")).ToArray();
        profile["match"]!["processNames"] = new JsonArray(values);
        MarkDirty(rebuild: true);
    }

    private static string NormalizeProcessName(string value) =>
        Path.GetFileNameWithoutExtension(value.Trim());

    private static string FormatKeys(IReadOnlyList<int> keys)
    {
        if (keys.Count == 0) return "None";
        return string.Join(" + ", keys.Select(key => key switch
        {
            0x14 => "Caps Lock", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
            0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
            0xA4 => "Left Alt", 0xA5 => "Right Alt", 0x20 => "Space", 0x0D => "Enter",
            0x1B => "Escape", 0x09 => "Tab", 0x08 => "Backspace", 0x2E => "Delete",
            >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
            >= 0x30 and <= 0x39 => ((char)key).ToString(),
            >= 0x41 and <= 0x5A => ((char)key).ToString(),
            _ => $"VK 0x{key:X2}"
        }));
    }

    private static string FormatGamepadButton(int mask) => mask switch
    {
        0x1000 => "A", 0x2000 => "B", 0x4000 => "X", 0x8000 => "Y",
        0x0100 => "LB", 0x0200 => "RB", 0x0020 => "Back", 0x0010 => "Start",
        0x0001 => "D-pad Up", 0x0002 => "D-pad Down", 0x0004 => "D-pad Left", 0x0008 => "D-pad Right",
        _ => $"0x{mask:X4}"
    };
}
