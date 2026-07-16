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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var sidebar = new DockPanel { Margin = new Thickness(16, 18, 16, 18) };
        var add = ActionButton(T("Add profile"), OnAddProfile);
        add.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(add, Dock.Top);
        sidebar.Children.Add(add);
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        for (var index = 0; index < Profiles.Count; index++)
        {
            var profileNode = Profiles[index]?.AsObject();
            var id = profileNode?["id"]?.GetValue<string>() ?? $"Profile {index + 1}";
            var item = new ListBoxItem
            {
                Content = id,
                Tag = index,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 1, 0, 1),
                IsSelected = index == _selectedProfileIndex
            };
            list.Items.Add(item);
        }
        list.SelectionChanged += (_, _) =>
        {
            if (_building || list.SelectedItem is not ListBoxItem { Tag: int index }) return;
            _selectedProfileIndex = index;
            BuildCurrentPage();
        };
        sidebar.Children.Add(list);
        Grid.SetColumn(sidebar, 0);
        grid.Children.Add(sidebar);
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
        var profileContent = new StackPanel { MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Stretch };
        profileContent.Children.Add(BuildProfileHeader(profile));
        profileContent.Children.Add(BuildProfileTabs());
        profileContent.Children.Add(_profileTab switch
        {
            "processing" => BuildProfileProcessing(profile),
            "output" => BuildProfileOutput(profile),
            _ => BuildProfileInput(profile)
        });
        var scroll = Page(profileContent);
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
        return grid;
    }

    private UIElement BuildProfileHeader(JsonObject profile)
    {
        var root = new StackPanel();
        var id = profile["id"]?.GetValue<string>() ?? string.Empty;
        var name = new TextBox { Text = id, MinWidth = 260 };
        name.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                eventArgs.Handled = true;
            }
        };
        name.LostKeyboardFocus += (_, _) => RenameProfile(name, id);

        var isDefault = string.Equals(GetString("profiles.defaultProfileId"), id, StringComparison.OrdinalIgnoreCase);
        var enabled = new CheckBox
        {
            Content = T("Enabled"),
            IsChecked = profile["enabled"]?.GetValue<bool>() != false,
            IsEnabled = !isDefault,
            ToolTip = isDefault ? "The default profile must remain enabled." : null
        };
        enabled.Checked += (_, _) => { profile["enabled"] = true; MarkDirty(); };
        enabled.Unchecked += (_, _) => { profile["enabled"] = false; MarkDirty(); };

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 16)
        };
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
        }, true);
        var setDefault = ActionButton(T("Set default"), (_, _) =>
        {
            Set("profiles.defaultProfileId", profile["id"]?.GetValue<string>() ?? string.Empty);
            MarkDirty();
        });
        var automatic = ActionButton(T("Automatic routing"), async (_, _) =>
        {
            await SaveNowAsync();
            if (_dirty) return;
            try
            {
                await _controller.SetProfileOverrideAsync(null);
                UpdateTopbar();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        });
        var duplicate = ActionButton(T("Duplicate"), OnDuplicateProfile);
        var delete = ActionButton(T("Delete"), OnDeleteProfile);
        delete.IsEnabled = profile["builtIn"]?.GetValue<bool>() != true;
        foreach (var button in new[] { use, automatic, setDefault, duplicate, delete })
        {
            button.Height = 34;
            button.Margin = new Thickness(0, 0, 7, 7);
            actions.Children.Add(button);
        }

        root.Children.Add(Field(T("Profile name"), name));
        root.Children.Add(enabled);
        root.Children.Add(actions);
        return root;
    }

    private UIElement BuildProfileTabs()
    {
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 20)
        };
        foreach (var (id, label) in new[]
        {
            ("input", T("Input")),
            ("processing", T("Processing")),
            ("output", T("Output"))
        })
        {
            var button = ActionButton(label, (_, _) =>
            {
                _profileTab = id;
                BuildCurrentPage();
            }, id == _profileTab);
            button.MinWidth = 110;
            button.Margin = new Thickness(0, 0, 8, 0);
            tabs.Children.Add(button);
        }
        return tabs;
    }

    private UIElement BuildProfileInput(JsonObject profile)
    {
        var basePath = $"profiles.items.{_selectedProfileIndex}";
        var content = new StackPanel();
        var processNames = profile["match"]?["processNames"]?.AsArray()
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? Array.Empty<string?>();
        var processText = new TextBox
        {
            Text = string.Join(Environment.NewLine, processNames),
            AcceptsReturn = true,
            MinHeight = 62,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true
        };
        var processButton = ActionButton(T("Choose processes"), (_, _) => OpenProcessPicker(profile));
        processButton.Margin = new Thickness(0, 7, 0, 0);
        content.Children.Add(Section(
            T("Application"),
            processNames.Length == 0 ? "PTT uses the foreground application." : string.Empty,
            Field(T("Target processes"), processText),
            processButton));

        var mode = GetString($"{basePath}.input.mode", "keyboard");
        var modeCombo = BoundCombo(
            $"{basePath}.input.mode",
            new[]
            {
                new Option("keyboard", T("Keyboard")),
                new Option("mouse", T("Mouse")),
                new Option("gamepad", T("Gamepad")),
                new Option("steamvr", T("SteamVR"))
            },
            rebuild: true);
        var triggerFields = new StackPanel();
        triggerFields.Children.Add(Field(T("Trigger type"), modeCombo));
        triggerFields.Children.Add(BuildTriggerBinding(profile, basePath, mode));
        content.Children.Add(Section(T("Push to talk"), string.Empty, triggerFields));

        var microphones = new List<Option> { new(string.Empty, T("Default communications device")) };
        microphones.AddRange(GetMicrophones().Select(device => new Option(device.Id, device.Name)));
        var audioDevice = new ComboBox
        {
            ItemsSource = microphones,
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedValue = GetString($"{basePath}.audio.deviceId")
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
                Field(T("Minimum recording (ms)"), BoundNumberBox($"{basePath}.audio.minimumDurationMs", 100, 5000, nullable: true)))));
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
            });
            stack.Children.Add(Field(T("Keyboard"), row));
            stack.Children.Add(BoundCheckBox(T("Suppress trigger"), $"{basePath}.input.keyboard.suppressKey"));
        }
        else if (mode == "mouse")
        {
            var row = BindingRow(GetString($"{basePath}.input.mouse.button", "x1"), async button =>
            {
                var capture = await _controller.CaptureMouseButtonAsync();
                Set($"{basePath}.input.mouse.button", capture.Button);
                MarkDirty(rebuild: true);
            });
            stack.Children.Add(Field(T("Mouse"), row));
            stack.Children.Add(BoundCheckBox(T("Suppress trigger"), $"{basePath}.input.mouse.suppressButton"));
        }
        else if (mode == "gamepad")
        {
            var userIndex = GetInt($"{basePath}.input.gamepad.userIndex");
            var mask = GetInt($"{basePath}.input.gamepad.buttonMask");
            var row = BindingRow($"Controller {userIndex + 1} · {FormatGamepadButton(mask)}", async button =>
            {
                var capture = await _controller.CaptureGamepadButtonAsync();
                Set($"{basePath}.input.gamepad.userIndex", capture.UserIndex);
                Set($"{basePath}.input.gamepad.buttonMask", capture.ButtonMask);
                MarkDirty(rebuild: true);
            });
            stack.Children.Add(Field(T("Gamepad"), row));
            stack.Children.Add(Field(T("Poll interval (ms)"), BoundNumberBox($"{basePath}.input.gamepad.pollIntervalMs", 1, 1000)));
        }
        else
        {
            var status = _controller.GetSteamVrStatus();
            stack.Children.Add(Notice(status.Message, !status.Connected));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            var refresh = ActionButton(T("Refresh SteamVR"), (_, _) => BuildCurrentPage());
            var bindings = ActionButton(T("Controller bindings"), async (_, _) =>
            {
                try { await _controller.OpenSteamVrBindingsAsync(); }
                catch (Exception exception) { ShowError(exception); }
            });
            bindings.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(refresh);
            buttons.Children.Add(bindings);
            stack.Children.Add(buttons);
            stack.Children.Add(Field(T("Poll interval (ms)"), BoundNumberBox($"{basePath}.input.steamVr.pollIntervalMs", 1, 1000)));
        }
        return stack;
    }

    private UIElement BuildProfileProcessing(JsonObject profile)
    {
        var basePath = $"profiles.items.{_selectedProfileIndex}";
        var provider = GetString($"{basePath}.recognition.provider", "sensevoice-gguf");
        var parsedConfiguration = AppConfiguration.Parse(_configuration.ToJsonString());
        var availability = AsrProviderFactory.CheckAvailability(parsedConfiguration.Asr)
            .First(status => status.Id == provider);
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
            availability.Available
                ? new Border()
                : Notice($"Missing files: {string.Join(", ", availability.MissingFiles)}", true),
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
            field.Children.Add(Notice($"Hotwords are unavailable for {GetString($"{basePath}.recognition.provider") }.", true));
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
            }, rebuild: true))));

        if (outputMode == "vrchat-osc")
        {
            content.Children.Add(Section(
                "VRChat OSC",
                string.Empty,
                TwoColumns(
                    Field(T("Host"), BoundTextBox($"{basePath}.output.vrChat.host")),
                    Field(T("Port"), BoundNumberBox($"{basePath}.output.vrChat.port", 1, 65535))),
                TwoColumns(
                    Field(T("Character limit"), BoundNumberBox($"{basePath}.output.vrChat.maxChatboxCharacters", 1, 144)),
                    BoundCheckBox(T("Send immediately"), $"{basePath}.output.vrChat.sendImmediately"))));
            return content;
        }

        var openMode = GetString($"{basePath}.output.windows.openInput.mode", "none");
        var submitMode = GetString($"{basePath}.output.windows.submission.mode", "none");
        content.Children.Add(Section(
            T("Open input"),
            string.Empty,
            Field(T("Open input"), BoundCombo($"{basePath}.output.windows.openInput.mode", new[]
            {
                new Option("none", T("None")), new Option("hotkey", T("Hotkey"))
            }, rebuild: true)),
            openMode == "hotkey"
                ? TwoColumns(
                    Field(T("Capture keys"), BuildOutputChord(profile, $"{basePath}.output.windows.openInput.virtualKeys")),
                    Field(T("Open delay (ms)"), BoundNumberBox($"{basePath}.output.windows.openInputDelayMs", 0, 5000)))
                : new Border()));
        content.Children.Add(Section(
            T("Text input"),
            string.Empty,
            Field(T("Text input"), BoundCombo($"{basePath}.output.windows.textInputMethod", new[]
            {
                new Option("clipboard-paste", T("Clipboard paste")),
                new Option("unicode-send-input", T("Unicode input")),
                new Option("keyboard", T("Keyboard events"))
            })),
            BoundCheckBox(T("Require same foreground window"), $"{basePath}.output.windows.requireSameForeground")));
        content.Children.Add(Section(
            T("Submit"),
            string.Empty,
            Field(T("Submit"), BoundCombo($"{basePath}.output.windows.submission.mode", new[]
            {
                new Option("none", T("None")), new Option("hotkey", T("Hotkey"))
            }, rebuild: true)),
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
        });
    }

    private Grid BindingRow(string value, Func<Button, Task> capture)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBox { Text = value, IsReadOnly = true };
        var button = new Button
        {
            Content = T("Capture binding"),
            Style = (Style)FindResource("ActionButtonStyle")
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
        input["mode"] ??= "keyboard";
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
        var cancel = ActionButton("Cancel", (_, _) => dialog.DialogResult = false);
        var apply = ActionButton("Apply", (_, _) => dialog.DialogResult = true, true);
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
