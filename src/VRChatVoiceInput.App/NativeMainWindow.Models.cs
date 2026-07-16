using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.App;

public partial class NativeMainWindow
{
    private static readonly IReadOnlyDictionary<string, string> ProviderLabels = new Dictionary<string, string>
    {
        ["paraformer-gguf"] = "Paraformer",
        ["sensevoice-gguf"] = "SenseVoice",
        ["funasr-nano-gguf"] = "Fun-ASR-Nano",
        ["qwen3-asr"] = "Qwen3-ASR",
        ["whisper-cpp"] = "Whisper.cpp"
    };

    private UIElement BuildModelsPage()
    {
        var root = new StackPanel { MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Stretch };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        foreach (var provider in ProviderLabels)
        {
            var localProvider = provider.Key;
            var installed = IsAnyModelCombinationAvailable(localProvider);
            var button = new Button
            {
                Content = $"{(installed ? "●" : "○")} {provider.Value}",
                Style = (Style)FindResource("ModelTabButtonStyle"),
                Tag = localProvider == _selectedProvider ? "active" : null
            };
            button.Click += (_, _) =>
            {
                _selectedProvider = localProvider;
                BuildCurrentPage();
            };
            button.Foreground = installed && localProvider != _selectedProvider ? SuccessBrush : button.Foreground;
            tabs.Children.Add(button);
        }
        root.Children.Add(tabs);

        var available = IsAnyModelCombinationAvailable(_selectedProvider);
        root.Children.Add(Notice(
            available
                ? $"{_selectedProvider} · {T("Installed")}"
                : $"{_selectedProvider} · {T("Not installed")}",
            !available));
        root.Children.Add(new Border { Height = 18 });

        root.Children.Add(BuildModelAssets(primaryOnly: true));
        root.Children.Add(BuildModelFacts());
        root.Children.Add(BuildModelAssets(primaryOnly: false));
        root.Children.Add(BuildModelBackendOptions());
        root.Children.Add(BuildAdvancedModelSettings());
        return Page(root, 920, 24);
    }

    private UIElement BuildModelAssets(bool primaryOnly)
    {
        var primaryComponent = PrimaryModelComponent(_selectedProvider);
        var assets = ModelDownloadCatalog.GetAssetStatuses()
            .Where(asset => asset.ProviderId == _selectedProvider)
            .Where(asset => primaryOnly == (asset.ComponentId == primaryComponent))
            .GroupBy(asset => asset.ComponentId)
            .OrderBy(group => ModelComponentOrder(group.Key))
            .ToArray();
        var rows = new StackPanel();
        foreach (var group in assets)
        {
            var componentAssets = group.ToArray();
            var selected = SelectModelAsset(componentAssets)!;
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 1),
                Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xF7)),
                MinHeight = 58
            };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(primaryOnly ? 0 : 230) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(primaryOnly ? 104 : 44) });

            if (!primaryOnly)
            {
                var title = new StackPanel { Margin = new Thickness(12, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center };
                title.Children.Add(new TextBlock { Text = ComponentLabel(group.Key), FontSize = 12, FontWeight = FontWeights.SemiBold });
                title.Children.Add(new TextBlock
                {
                    Text = ComponentDescription(group.Key), FontSize = 10, Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(title, 0);
                row.Children.Add(title);
            }

            FrameworkElement selector;
            if (componentAssets.Length > 1)
            {
                var combo = new ComboBox
                {
                    ItemsSource = componentAssets.Select(asset => new ModelAssetOption(asset, $"{asset.Variant} · {FormatBytes(asset.Size)} · {T(asset.Installed ? "Installed" : "Not installed")}")),
                    DisplayMemberPath = nameof(ModelAssetOption.Label),
                    SelectedValuePath = nameof(ModelAssetOption.Asset),
                    SelectedValue = selected,
                    Margin = primaryOnly ? new Thickness(0, 8, 8, 8) : new Thickness(8),
                    VerticalAlignment = VerticalAlignment.Center
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (_building || combo.SelectedValue is not ModelAssetStatus next) return;
                    _selectedModelAssetIds[ModelAssetSelectionKey(next.ProviderId, next.ComponentId)] = next.Id;
                    BuildCurrentPage();
                };
                selector = combo;
            }
            else
            {
                selector = new TextBlock
                {
                    Text = selected.Variant,
                    Foreground = MutedBrush,
                    FontSize = 11,
                    Margin = primaryOnly ? new Thickness(0, 0, 8, 0) : new Thickness(12, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            Grid.SetColumn(selector, primaryOnly ? 0 : 1);
            row.Children.Add(selector);
            BuildAssetAction(row, selected, compact: !primaryOnly);
            rows.Children.Add(row);
        }

        return primaryOnly
            ? Section(
                T(_selectedProvider == "funasr-nano-gguf" ? "Language model quantization" : "Quantization"),
                T("Select an installed version to use it, or download a missing version first."),
                rows)
            : Section(
                T("Model components and optional capabilities"),
                T("Install only the components and capabilities needed by this model."),
                rows);
    }

    private void BuildAssetAction(Grid row, ModelAssetStatus asset, bool compact)
    {
        foreach (var child in row.Children.Cast<UIElement>().Where(child => Grid.GetColumn(child) >= 2).ToArray())
        {
            row.Children.Remove(child);
        }
        var size = new TextBlock
        {
            Text = FormatBytes(asset.Size), FontSize = 10, Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 0, 12, 0)
        };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);

        UIElement action;
        if (asset.Installed)
        {
            var active = IsAssetActive(asset);
            var button = compact
                ? IconButton(MaterialIconPaths.Check, T(active ? "Active" : "Use"), (_, _) =>
                {
                    ApplyAsset(asset);
                    MarkDirty(rebuild: true);
                })
                : ActionButton(active ? T("Active") : T("Use"), (_, _) =>
                {
                    ApplyAsset(asset);
                    MarkDirty(rebuild: true);
                }, !active, MaterialIconPaths.Check);
            button.IsEnabled = !active;
            if (!compact)
            {
                button.Width = 88;
                button.Height = 34;
            }
            button.VerticalAlignment = VerticalAlignment.Center;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            action = button;
        }
        else
        {
            var button = compact
                ? IconButton(MaterialIconPaths.Download, T("Download"), async (_, _) => await DownloadAssetAsync(asset))
                : ActionButton(T("Download"), async (_, _) => await DownloadAssetAsync(asset), true, MaterialIconPaths.Download);
            if (!compact)
            {
                button.Width = 88;
                button.Height = 34;
            }
            button.VerticalAlignment = VerticalAlignment.Center;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.IsEnabled = _downloadProgress?.State is not ("checking" or "downloading" or "verifying" or "extracting");
            action = button;
        }
        Grid.SetColumn(action, 3);
        row.Children.Add(action);
    }

    private async Task DownloadAssetAsync(ModelAssetStatus asset)
    {
        try
        {
            await _controller.DownloadModelAssetAsync(
                asset.Id,
                GetNode("asr")!.ToJsonString(),
                GetString("application.modelDownloadSource", "official"));
            var installed = ModelDownloadCatalog.GetAssetStatuses().First(item => item.Id == asset.Id);
            if (installed.Installed)
            {
                ApplyAsset(installed);
                MarkDirty();
                await SaveNowAsync();
            }
            BuildCurrentPage();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private UIElement BuildModelFacts()
    {
        var primaryAssets = ModelDownloadCatalog.GetAssetStatuses()
            .Where(asset => asset.ProviderId == _selectedProvider && asset.ComponentId == PrimaryModelComponent(_selectedProvider))
            .ToArray();
        var selectedAsset = SelectModelAsset(primaryAssets);
        var fact = _selectedProvider switch
        {
            "paraformer-gguf" => new ModelFact(
                "Mandarin Chinese; limited English code-switching",
                ParaformerRamAndThroughput(selectedAsset),
                "Fast Mandarin input with low CPU and memory cost."),
            "sensevoice-gguf" => new ModelFact(
                "Mandarin, Cantonese, English, Japanese, Korean",
                SenseVoiceRamAndThroughput(selectedAsset),
                "Low-latency multilingual input for East Asian languages."),
            "funasr-nano-gguf" => new ModelFact(
                "Chinese, English, Japanese",
                NanoRamAndThroughput(selectedAsset),
                "Terminology and punctuation when more memory is available."),
            "qwen3-asr" => new ModelFact(
                "30 languages and 22 Chinese dialects",
                ("~2.2-3.0 GiB", FormatThroughput(28)),
                "High-quality multilingual and mixed-language recognition."),
            _ => new ModelFact(
                "Multilingual Whisper model",
                ("~590 MiB", FormatThroughput(12)),
                "Broad language coverage when higher latency is acceptable.")
        };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var languages = FactCell(T("Languages"), T(fact.Languages));
        var files = FactCell(T("Model files"), selectedAsset is null ? T("Not measured") : FormatBytes(selectedAsset.Size));
        var memory = FactCell(T("Estimated peak RAM"), fact.Performance.Ram);
        var throughput = FactCell(
            T("Tested throughput"),
            fact.Performance.Throughput,
            rightBorder: false,
            toolTip: T("Measured on an Intel Core i7-14700KF system with 64 GB RAM."));
        Grid.SetColumn(languages, 0);
        Grid.SetColumn(files, 1);
        Grid.SetColumn(memory, 2);
        Grid.SetColumn(throughput, 3);
        grid.Children.Add(languages);
        grid.Children.Add(files);
        grid.Children.Add(memory);
        grid.Children.Add(throughput);

        var best = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        best.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        best.ColumnDefinitions.Add(new ColumnDefinition());
        best.Children.Add(new TextBlock { Text = T("Best suited for"), FontSize = 10, Foreground = MutedBrush });
        var bestText = new TextBlock
        {
            Text = T(fact.BestFor),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(bestText, 1);
        best.Children.Add(bestText);
        var bestBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = best
        };
        Grid.SetRow(bestBorder, 1);
        Grid.SetColumnSpan(bestBorder, 4);
        grid.Children.Add(bestBorder);

        var note = new TextBlock
        {
            Text = T("Results vary by hardware, audio, backend, and selected quantization."),
            FontSize = 10,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 7, 12, 7)
        };
        var noteBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = note
        };
        Grid.SetRow(noteBorder, 2);
        Grid.SetColumnSpan(noteBorder, 4);
        grid.Children.Add(noteBorder);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Child = grid
        };
    }

    private (string Ram, string Throughput) ParaformerRamAndThroughput(ModelAssetStatus? asset)
    {
        return asset?.Id switch
        {
            "paraformer-q8_0" => ("252.8 MiB", FormatThroughput(148)),
            "paraformer-q4_0" => ("151.1 MiB", FormatThroughput(172)),
            "paraformer-q5_0" => ("176.5 MiB", FormatThroughput(124)),
            _ => NotMeasuredPerformance()
        };
    }

    private (string Ram, string Throughput) SenseVoiceRamAndThroughput(ModelAssetStatus? asset)
    {
        if (GetString("asr.senseVoice.backend", "cpu") == "vulkan") return NotMeasuredPerformance();
        return asset?.Id switch
        {
            "sensevoice-q8_0" => ("358.8 MiB", FormatThroughput(135)),
            "sensevoice-q5_0" => ("275.8 MiB", FormatThroughput(120)),
            _ => NotMeasuredPerformance()
        };
    }

    private (string Ram, string Throughput) NanoRamAndThroughput(ModelAssetStatus? languageModel)
    {
        if (GetString("asr.funAsrNano.backend", "cpu") == "vulkan" || languageModel is null)
        {
            return NotMeasuredPerformance();
        }
        var encoder = SelectModelAsset(ModelDownloadCatalog.GetAssetStatuses()
            .Where(asset => asset.ProviderId == "funasr-nano-gguf" && asset.ComponentId == "encoder"));
        return $"{encoder?.Id}:{languageModel.Id}" switch
        {
            "nano-encoder-q8_0:nano-language-q4_k_m" => ("1131.5 MiB", FormatThroughput(64)),
            "nano-encoder-f16:nano-language-q4_k_m" => ("1337.5 MiB", FormatThroughput(51)),
            _ => NotMeasuredPerformance()
        };
    }

    private (string Ram, string Throughput) NotMeasuredPerformance() => (T("Not measured"), T("Not measured"));

    private string FormatThroughput(int charactersPerSecond) =>
        string.Format(CultureInfo.InvariantCulture, T("~{0} characters/s"), charactersPerSecond);

    private Border FactCell(string label, string value, bool rightBorder = true, string? toolTip = null)
    {
        var stack = new StackPanel();
        if (toolTip is null)
        {
            stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = MutedBrush });
        }
        else
        {
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = toolTip };
            labelRow.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = MutedBrush });
            labelRow.Children.Add(MaterialIcon(
                MaterialIconPaths.InformationOutline,
                12,
                MutedBrush,
                new Thickness(4, 0, 0, 0)));
            stack.Children.Add(labelRow);
        }
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = rightBorder ? new Thickness(0, 0, 1, 0) : new Thickness(0),
            ToolTip = toolTip,
            Child = stack
        };
    }

    private UIElement BuildModelBackendOptions()
    {
        var children = new List<UIElement>();
        if (_selectedProvider == "paraformer-gguf")
        {
            var punctuationInstalled = ModelDownloadCatalog.GetAssetStatuses().Any(asset =>
                asset.ProviderId == "paraformer-gguf" && asset.ComponentId == "punctuation" && asset.Installed);
            var punctuation = BoundCheckBox(T("Enable punctuation"), "asr.paraformer.usePunctuation");
            punctuation.IsEnabled = punctuationInstalled;
            children.Add(punctuation);
            if (!punctuationInstalled)
            {
                children.Add(Notice(T("The punctuation model must be installed before enabling this option."), true));
            }
            return Section(T("Punctuation"), T("The punctuation model must be installed before enabling this option."), children.ToArray());
        }
        string? backendPath = null;
        string? devicePath = null;
        var useGpu = false;
        if (_selectedProvider == "sensevoice-gguf")
        {
            backendPath = "asr.senseVoice.backend";
            devicePath = "asr.senseVoice.vulkanDeviceIndex";
            useGpu = GetString(backendPath, "cpu") == "vulkan";
        }
        else if (_selectedProvider == "funasr-nano-gguf")
        {
            backendPath = "asr.funAsrNano.backend";
            devicePath = "asr.funAsrNano.vulkanDeviceIndex";
            useGpu = GetString(backendPath, "cpu") == "vulkan";
        }
        else if (_selectedProvider == "whisper-cpp")
        {
            backendPath = "asr.whisperCpp.useGpu";
            devicePath = "asr.whisperCpp.gpuDeviceIndex";
            useGpu = GetBool(backendPath);
        }
        if (backendPath is null || devicePath is null)
        {
            return new Border();
        }

        var localBackendPath = backendPath;
        var useGpuCheck = new CheckBox { Content = T("Use GPU"), IsChecked = useGpu };
        useGpuCheck.Checked += (_, _) =>
        {
            Set(localBackendPath, _selectedProvider == "whisper-cpp" ? true : "vulkan");
            MarkDirty(rebuild: true);
        };
        useGpuCheck.Unchecked += (_, _) =>
        {
            Set(localBackendPath, _selectedProvider == "whisper-cpp" ? false : "cpu");
            MarkDirty(rebuild: true);
        };
        children.Add(useGpuCheck);

        var devices = _controller.ListGpuDevices();
        var deviceCombo = new ComboBox
        {
            ItemsSource = devices.Select(device => new GpuOption(device.Index, $"{device.Name} · {device.DeviceType}")),
            DisplayMemberPath = nameof(GpuOption.Label),
            SelectedValuePath = nameof(GpuOption.Index),
            SelectedValue = GetNode(devicePath)?.GetValue<int?>(),
            IsEnabled = useGpu,
            Margin = new Thickness(0, 10, 0, 0)
        };
        if (deviceCombo.SelectedIndex < 0 && devices.Count > 0) deviceCombo.SelectedIndex = 0;
        var localDevicePath = devicePath;
        deviceCombo.SelectionChanged += (_, _) =>
        {
            if (_building || deviceCombo.SelectedValue is not int index) return;
            Set(localDevicePath, index);
            MarkDirty();
        };
        children.Add(Field(T("GPU device"), deviceCombo));
        children.Add(Notice(T("Vulkan performance may be faster or slower depending on the device, but an integrated GPU can reduce CPU load.")));
        return Section(T("CPU / Vulkan"), string.Empty, children.ToArray());
    }

    private UIElement BuildAdvancedModelSettings()
    {
        var panel = new StackPanel();
        foreach (var field in AdvancedFields(_selectedProvider))
        {
            FrameworkElement control = field.Numeric
                ? BoundNumberBox(field.Path, field.Minimum, field.Maximum)
                : field.Kind == "text" ? BoundTextBox(field.Path) : PathField(field.Path, field.Kind);
            panel.Children.Add(Field(T(field.Label), control));
        }
        panel.Children.Add(new TextBlock { Text = T("Streaming recognition"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 10) });
        panel.Children.Add(TwoColumns(
            Field(T("Speech threshold"), BoundDoubleBox("asr.streaming.threshold", 0.01, 1)),
            Field(T("Minimum silence (seconds)"), BoundDoubleBox("asr.streaming.minimumSilenceSeconds", 0.1, 5))));
        panel.Children.Add(TwoColumns(
            Field(T("Minimum speech (seconds)"), BoundDoubleBox("asr.streaming.minimumSpeechSeconds", 0.05, 5)),
            Field(T("Maximum segment (seconds)"), BoundDoubleBox("asr.streaming.maximumSegmentSeconds", 1, 60))));
        panel.Children.Add(Field(T("Silero VAD"), PathField("asr.streaming.sileroVadModelPath", "model")));
        var expander = new Expander
        {
            Header = T("Advanced model configuration"),
            IsExpanded = _advancedModelSettingsOpen,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 20),
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 8, 0, 0),
                Child = panel
            }
        };
        expander.Expanded += (_, _) => _advancedModelSettingsOpen = true;
        expander.Collapsed += (_, _) => _advancedModelSettingsOpen = false;
        return expander;
    }

    private FrameworkElement PathField(string path, string kind)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = BoundTextBox(path);
        var browse = ActionButton(T("Browse"), (_, _) =>
        {
            if (kind == "folder")
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = GetString(path) };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    text.Text = dialog.SelectedPath;
                }
                return;
            }
            var dialogFile = new OpenFileDialog
            {
                FileName = Path.GetFileName(GetString(path)),
                InitialDirectory = Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(GetString(path))))
                    ? Path.GetDirectoryName(Path.GetFullPath(GetString(path))) : null,
                Filter = kind == "executable" ? "Executable (*.exe)|*.exe|All files (*.*)|*.*" : "Model files|*.gguf;*.onnx;*.bin|All files (*.*)|*.*"
            };
            if (dialogFile.ShowDialog(this) == true) text.Text = dialogFile.FileName;
        }, icon: MaterialIconPaths.FolderOpenOutline);
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(text);
        grid.Children.Add(browse);
        return grid;
    }

    private TextBox BoundDoubleBox(string path, double minimum, double maximum)
    {
        var value = GetNode(path)?.GetValue<double>() ?? minimum;
        var box = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture) };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var next))
            {
                box.Text = value.ToString("0.##", CultureInfo.InvariantCulture);
                return;
            }
            next = Math.Clamp(next, minimum, maximum);
            box.Text = next.ToString("0.##", CultureInfo.InvariantCulture);
            Set(path, next);
            MarkDirty();
        };
        return box;
    }

    private bool IsAnyModelCombinationAvailable(string providerId)
    {
        var assets = ModelDownloadCatalog.GetAssetStatuses().Where(asset => asset.ProviderId == providerId).ToArray();
        var hasRuntime = providerId == "qwen3-asr" || assets.Any(asset => asset.ComponentId.StartsWith("runtime-") && asset.Installed);
        var primary = PrimaryModelComponent(providerId);
        var hasPrimary = assets.Any(asset => asset.ComponentId == primary && asset.Installed);
        var hasEncoder = providerId != "funasr-nano-gguf" || assets.Any(asset => asset.ComponentId == "encoder" && asset.Installed);
        var punctuation = providerId != "paraformer-gguf" || !GetBool("asr.paraformer.usePunctuation") ||
            assets.Any(asset => asset.ComponentId == "punctuation" && asset.Installed);
        return hasRuntime && hasPrimary && hasEncoder && punctuation;
    }

    private static string PrimaryModelComponent(string providerId) => providerId switch
    {
        "funasr-nano-gguf" => "language-model",
        "qwen3-asr" => "model-bundle",
        _ => "model"
    };

    private ModelAssetStatus? SelectModelAsset(IEnumerable<ModelAssetStatus> assets)
    {
        var candidates = assets.ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        var key = ModelAssetSelectionKey(candidates[0].ProviderId, candidates[0].ComponentId);
        if (_selectedModelAssetIds.TryGetValue(key, out var selectedId))
        {
            var selected = candidates.FirstOrDefault(asset => string.Equals(asset.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
            _selectedModelAssetIds.Remove(key);
        }
        return candidates.FirstOrDefault(IsAssetActive)
            ?? candidates.FirstOrDefault(asset => asset.Installed)
            ?? candidates[0];
    }

    private static string ModelAssetSelectionKey(string providerId, string componentId) => $"{providerId}:{componentId}";

    private bool IsAssetActive(ModelAssetStatus asset)
    {
        var configuredPath = ConfiguredComponentPath(asset.ProviderId, asset.ComponentId);
        if (asset.ComponentId.StartsWith("runtime-"))
        {
            return asset.ProviderId switch
            {
                "sensevoice-gguf" => (GetString("asr.senseVoice.backend", "cpu") == "vulkan") == (asset.ComponentId == "runtime-vulkan"),
                "funasr-nano-gguf" => (GetString("asr.funAsrNano.backend", "cpu") == "vulkan") == (asset.ComponentId == "runtime-vulkan"),
                "whisper-cpp" => GetBool("asr.whisperCpp.useGpu") == (asset.ComponentId == "runtime-vulkan"),
                _ => asset.ComponentId == "runtime-cpu"
            };
        }
        return !string.IsNullOrWhiteSpace(configuredPath) && !string.IsNullOrWhiteSpace(asset.TargetPath) &&
            string.Equals(Path.GetFileName(configuredPath), Path.GetFileName(asset.TargetPath), StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyAsset(ModelAssetStatus asset)
    {
        var target = asset.TargetPath;
        switch (asset.ProviderId, asset.ComponentId)
        {
            case ("paraformer-gguf", "model"): Set("asr.paraformer.modelPath", target); break;
            case ("paraformer-gguf", "runtime-cpu"): Set("asr.paraformer.executablePath", "runtimes/llama-funasr-paraformer.exe"); break;
            case ("paraformer-gguf", "punctuation"): Set("asr.paraformer.punctuationModelPath", target); break;
            case ("paraformer-gguf", "vad"): Set("asr.paraformer.vadModelPath", target); break;
            case ("sensevoice-gguf", "model"): Set("asr.senseVoice.modelPath", target); break;
            case ("sensevoice-gguf", "runtime-cpu"):
                Set("asr.senseVoice.backend", "cpu");
                Set("asr.senseVoice.cpuExecutablePath", "runtimes/llama-funasr-sensevoice.exe");
                break;
            case ("sensevoice-gguf", "runtime-vulkan"):
                Set("asr.senseVoice.backend", "vulkan");
                Set("asr.senseVoice.vulkanExecutablePath", "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe");
                break;
            case ("sensevoice-gguf", "vad"): Set("asr.senseVoice.vadModelPath", target); break;
            case ("funasr-nano-gguf", "encoder"): Set("asr.funAsrNano.encoderModelPath", target); break;
            case ("funasr-nano-gguf", "language-model"): Set("asr.funAsrNano.languageModelPath", target); break;
            case ("funasr-nano-gguf", "runtime-cpu"):
                Set("asr.funAsrNano.backend", "cpu");
                Set("asr.funAsrNano.executablePath", "runtimes/llama-funasr-cli.exe");
                break;
            case ("funasr-nano-gguf", "runtime-vulkan"):
                Set("asr.funAsrNano.backend", "vulkan");
                Set("asr.funAsrNano.vulkanExecutablePath", "runtimes/funasr-nano-vulkan/llama-funasr-cli.exe");
                break;
            case ("funasr-nano-gguf", "vad"): Set("asr.funAsrNano.vadModelPath", target); break;
            case ("whisper-cpp", "model"): Set("asr.whisperCpp.modelPath", target); break;
            case ("qwen3-asr", "model-bundle"):
                Set("asr.qwen3Asr.convFrontendPath", "models/qwen3-asr/conv_frontend.onnx");
                Set("asr.qwen3Asr.encoderPath", "models/qwen3-asr/encoder.int8.onnx");
                Set("asr.qwen3Asr.decoderPath", "models/qwen3-asr/decoder.int8.onnx");
                Set("asr.qwen3Asr.tokenizerPath", "models/qwen3-asr/tokenizer");
                break;
            case ("whisper-cpp", "runtime-cpu"):
                Set("asr.whisperCpp.useGpu", false);
                Set("asr.whisperCpp.serverExecutablePath", "runtimes/whispercpp/Release/whisper-server.exe");
                break;
            case ("whisper-cpp", "runtime-vulkan"):
                Set("asr.whisperCpp.useGpu", true);
                Set("asr.whisperCpp.vulkanServerExecutablePath", "runtimes/whispercpp-vulkan/Release/whisper-server.exe");
                break;
            case ("whisper-cpp", "vad"): Set("asr.whisperCpp.vadModelPath", target); break;
            case (_, "streaming-vad"): Set("asr.streaming.sileroVadModelPath", target); break;
        }
        Set("asr.provider", asset.ProviderId);
    }

    private string? ConfiguredComponentPath(string providerId, string componentId) => (providerId, componentId) switch
    {
        ("paraformer-gguf", "model") => GetString("asr.paraformer.modelPath"),
        ("paraformer-gguf", "punctuation") => GetString("asr.paraformer.punctuationModelPath"),
        ("paraformer-gguf", "vad") => GetString("asr.paraformer.vadModelPath"),
        ("sensevoice-gguf", "model") => GetString("asr.senseVoice.modelPath"),
        ("sensevoice-gguf", "vad") => GetString("asr.senseVoice.vadModelPath"),
        ("funasr-nano-gguf", "encoder") => GetString("asr.funAsrNano.encoderModelPath"),
        ("funasr-nano-gguf", "language-model") => GetString("asr.funAsrNano.languageModelPath"),
        ("funasr-nano-gguf", "vad") => GetString("asr.funAsrNano.vadModelPath"),
        ("whisper-cpp", "model") => GetString("asr.whisperCpp.modelPath"),
        ("whisper-cpp", "vad") => GetString("asr.whisperCpp.vadModelPath"),
        (_, "streaming-vad") => GetString("asr.streaming.sileroVadModelPath"),
        _ => null
    };

    private static int ModelComponentOrder(string component) => component switch
    {
        "model" or "model-bundle" or "language-model" => 0,
        "encoder" => 1,
        "runtime-cpu" => 2,
        "runtime-vulkan" => 3,
        "punctuation" => 4,
        "vad" => 5,
        "streaming-vad" => 6,
        _ => 10
    };

    private string ComponentLabel(string component) => T(component switch
    {
        "model" or "model-bundle" => "Model",
        "language-model" => "Language model",
        "encoder" => "Encoder",
        "runtime-cpu" => "CPU runtime",
        "runtime-vulkan" => "Vulkan runtime",
        "punctuation" => "Punctuation",
        "vad" => "VAD",
        "streaming-vad" => "Streaming VAD",
        _ => component
    });

    private string ComponentDescription(string component) => T(component switch
    {
        "runtime-cpu" => "Native executable and libraries required for CPU inference.",
        "runtime-vulkan" => "Native executable and libraries required for Vulkan inference.",
        "punctuation" => "Restores punctuation for Paraformer output.",
        "vad" => "Optional endpoint and speech detection.",
        "streaming-vad" => "Required by segmented streaming recognition.",
        _ => "Select an installed version or download the required files."
    });

    private static IReadOnlyList<AdvancedField> AdvancedFields(string provider) => provider switch
    {
        "paraformer-gguf" =>
        [
            new("Executable", "asr.paraformer.executablePath", "executable"),
            new("Model", "asr.paraformer.modelPath", "model"),
            new("VAD model", "asr.paraformer.vadModelPath", "model"),
            new("Punctuation model", "asr.paraformer.punctuationModelPath", "model")
        ],
        "sensevoice-gguf" =>
        [
            new("CPU executable", "asr.senseVoice.cpuExecutablePath", "executable"),
            new("Vulkan executable", "asr.senseVoice.vulkanExecutablePath", "executable"),
            new("Model", "asr.senseVoice.modelPath", "model"),
            new("VAD model", "asr.senseVoice.vadModelPath", "model")
        ],
        "funasr-nano-gguf" =>
        [
            new("Chunk seconds", "asr.funAsrNano.chunkSeconds", "", true, 1, 60),
            new("CPU executable", "asr.funAsrNano.executablePath", "executable"),
            new("Vulkan executable", "asr.funAsrNano.vulkanExecutablePath", "executable"),
            new("Encoder model", "asr.funAsrNano.encoderModelPath", "model"),
            new("Language model", "asr.funAsrNano.languageModelPath", "model"),
            new("VAD model", "asr.funAsrNano.vadModelPath", "model")
        ],
        "qwen3-asr" =>
        [
            new("Threads", "asr.qwen3Asr.threadCount", "", true, 1, 64),
            new("Maximum output tokens", "asr.qwen3Asr.maxNewTokens", "", true, 16, 512),
            new("Convolution frontend", "asr.qwen3Asr.convFrontendPath", "model"),
            new("Encoder", "asr.qwen3Asr.encoderPath", "model"),
            new("Decoder", "asr.qwen3Asr.decoderPath", "model"),
            new("Tokenizer", "asr.qwen3Asr.tokenizerPath", "folder")
        ],
        _ =>
        [
            new("Language", "asr.whisperCpp.language", "text"),
            new("Threads", "asr.whisperCpp.threadCount", "", true, 0, 64),
            new("CPU server", "asr.whisperCpp.serverExecutablePath", "executable"),
            new("Vulkan server", "asr.whisperCpp.vulkanServerExecutablePath", "executable"),
            new("Model", "asr.whisperCpp.modelPath", "model"),
            new("VAD model", "asr.whisperCpp.vadModelPath", "model")
        ]
    };

    private sealed record ModelAssetOption(ModelAssetStatus Asset, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record GpuOption(int Index, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record AdvancedField(string Label, string Path, string Kind, bool Numeric = false, int Minimum = 0, int Maximum = 0);
    private sealed record ModelFact(string Languages, (string Ram, string Throughput) Performance, string BestFor);
}
