using VRChatVoiceInput.Core.Asr;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Input;
using VRChatVoiceInput.Core.Output;
using VRChatVoiceInput.Core.Sessions;
using VRChatVoiceInput.Windows.Audio;
using VRChatVoiceInput.Windows.Input;
using VRChatVoiceInput.Windows.Output;
using VRChatVoiceInput.Windows.Runtime;
using Valve.VR;

return await new CommandLineApplication().RunAsync(args);

internal sealed class CommandLineApplication
{
    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            if (options.RunSelfTest)
            {
                RunSelfTest();
                Console.WriteLine("Core self-test passed.");
                return 0;
            }

            if (options.ListMicrophones)
            {
                PrintMicrophones();
                return 0;
            }

            if (options.ListGamepads)
            {
                PrintGamepads();
                return 0;
            }

            if (options.ListGpus)
            {
                PrintGpus();
                return 0;
            }

            if (options.ListProviders)
            {
                PrintProviders();
                return 0;
            }

            var configuration = AppConfiguration.Load(options.ConfigurationPath);
            var profiles = configuration.GetEffectiveProfiles();
            var resolver = new ApplicationProfileResolver(profiles, configuration.GetEffectiveDefaultProfileId());
            if (options.ListProfiles)
            {
                PrintProfiles(profiles, configuration.GetEffectiveDefaultProfileId());
                return 0;
            }

            var profile = SelectProfile(configuration, resolver, options.ProfileOverride);
            if (options.RecognizeOnlyAudioPath is not null)
            {
                var provider = CreateProvider(configuration, profile, options.ProviderOverride);
                try
                {
                    var recognitionOptions = CreateRecognitionOptions(profile, provider);
                    var result = await provider.TranscribeAsync(
                        new AudioInput(options.RecognizeOnlyAudioPath),
                        recognitionOptions);
                    Console.WriteLine($"Profile: {profile.Id}; provider: {provider.Id}");
                    Console.WriteLine(result.Text);
                    Console.WriteLine($"Recognized in {result.Duration.TotalMilliseconds:F0} ms.");
                    return 0;
                }
                finally
                {
                    (provider as IDisposable)?.Dispose();
                }
            }

            if (options.RunDaemon)
            {
                var daemon = new ProfileDaemon(configuration, options.ProfileOverride, options.ProviderOverride);
                await daemon.RunAsync();
                return 0;
            }

            var output = CreateOutput(profile.Output);
            try
            {
                if (options.TextToSend is not null)
                {
                    var target = output.CaptureTarget();
                    await output.SendAsync(options.TextToSend, target);
                    Console.WriteLine($"Profile '{profile.Id}' sent text to {target.DisplayName}.");
                    return 0;
                }

                if (options.AudioPath is not null)
                {
                    var target = output.CaptureTarget();
                    var provider = CreateProvider(configuration, profile, options.ProviderOverride);
                    try
                    {
                        var recognitionOptions = CreateRecognitionOptions(profile, provider);
                        var result = await provider.TranscribeAsync(new AudioInput(options.AudioPath), recognitionOptions);
                        Console.WriteLine(result.Text);
                        await output.SendAsync(result.Text, target);
                        Console.WriteLine(
                            $"Profile '{profile.Id}' sent text to {target.DisplayName} " +
                            $"in {result.Duration.TotalMilliseconds:F0} ms.");
                        return 0;
                    }
                    finally
                    {
                        (provider as IDisposable)?.Dispose();
                    }
                }
            }
            finally
            {
                (output as IDisposable)?.Dispose();
            }

            PrintUsage();
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 2;
        }
    }

    private static ApplicationProfileConfiguration SelectProfile(
        AppConfiguration configuration,
        ApplicationProfileResolver resolver,
        string? profileOverride)
    {
        if (!string.IsNullOrWhiteSpace(profileOverride))
        {
            return resolver.GetById(profileOverride);
        }

        try
        {
            return resolver.Resolve(ForegroundApplicationInspector.GetCurrent().ProcessName);
        }
        catch (InvalidOperationException)
        {
            return resolver.GetById(configuration.GetEffectiveDefaultProfileId());
        }
    }

    private static IAsrProvider CreateProvider(
        AppConfiguration configuration,
        ApplicationProfileConfiguration profile,
        string? providerOverride = null) =>
        AsrProviderFactory.Create(configuration.Asr, providerOverride ?? profile.Recognition.Provider);

    private static RecognitionOptions? CreateRecognitionOptions(
        ApplicationProfileConfiguration profile,
        IAsrProvider provider)
    {
        IReadOnlyList<string> terminologyHints = Array.Empty<string>();
        if (profile.Recognition.Hotwords.Count > 0 &&
            !provider.Capabilities.HasFlag(AsrProviderCapabilities.TerminologyHints))
        {
            Console.WriteLine(
                $"Warning: profile '{profile.Id}' defines {profile.Recognition.Hotwords.Count} hotwords, " +
                $"but provider '{provider.Id}' does not implement them yet.");
        }
        else if (profile.Recognition.Hotwords.Count > 0)
        {
            terminologyHints = profile.Recognition.Hotwords;
        }

        return new RecognitionOptions
        {
            TerminologyHints = terminologyHints,
            Language = profile.Recognition.Language
        };
    }

    private static ITextOutput CreateOutput(OutputConfiguration configuration) =>
        configuration.Mode.ToLowerInvariant() switch
        {
            "captured-window" => new CapturedWindowTextOutput(configuration.Windows),
            "vrchat-osc" => new VrChatOscOutput(configuration.VrChat),
            _ => throw new InvalidOperationException($"Unsupported output mode '{configuration.Mode}'.")
        };

    private static void PrintMicrophones()
    {
        var devices = WasapiAudioRecorder.ListCaptureDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No active capture devices were found.");
            return;
        }

        foreach (var device in devices)
        {
            Console.WriteLine(device.Name);
            Console.WriteLine($"  {device.Id}");
        }
    }

    private static void PrintGamepads()
    {
        var devices = XInputGamepadPttInput.ListConnectedControllers();
        if (devices.Count == 0)
        {
            Console.WriteLine("No connected XInput controllers were found.");
            return;
        }

        foreach (var device in devices)
        {
            Console.WriteLine($"{device.UserIndex}: {device.Name}");
        }
    }

    private static void PrintGpus()
    {
        var devices = VulkanDeviceInspector.ListDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No Vulkan GPU devices were found.");
            return;
        }

        foreach (var device in devices)
        {
            Console.WriteLine($"{device.Index}: {device.Name} ({device.DeviceType})");
            Console.WriteLine($"  vendor=0x{device.VendorId:X4}; device=0x{device.DeviceId:X4}; backend={device.Backend}");
        }
    }

    private static void PrintProviders()
    {
        foreach (var providerId in AsrProviderFactory.ProviderIds)
        {
            Console.WriteLine(providerId);
        }
    }

    private static void PrintProfiles(
        IReadOnlyList<ApplicationProfileConfiguration> profiles,
        string defaultProfileId)
    {
        foreach (var profile in profiles)
        {
            var marker = string.Equals(profile.Id, defaultProfileId, StringComparison.OrdinalIgnoreCase)
                ? " (default)"
                : string.Empty;
            var matches = profile.Match.ProcessNames.Count == 0
                ? "current foreground"
                : string.Join(", ", profile.Match.ProcessNames);
            Console.WriteLine($"{profile.Id}{marker}: {profile.DisplayName} [{matches}]");
        }
    }

    private static void RunSelfTest()
    {
        var chunks = TextChunker.Split("abcdef", 2);
        if (!chunks.SequenceEqual(new[] { "ab", "cd", "ef" }))
        {
            throw new InvalidOperationException("Text chunking check failed.");
        }

        var unicodeChunks = TextChunker.Split("A😀中", 2);
        if (!unicodeChunks.SequenceEqual(new[] { "A😀", "中" }))
        {
            throw new InvalidOperationException("Unicode text chunking check failed.");
        }

        if (TextChunker.Tail("A😀中文", 3) != "😀中文")
        {
            throw new InvalidOperationException("Unicode streaming text window check failed.");
        }

        var osc = OscChatboxMessage.Create("test", sendImmediately: true);
        if (osc.Length % 4 != 0 || !System.Text.Encoding.UTF8.GetString(osc).Contains("/chatbox/input", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OSC encoding check failed.");
        }

        var partialOsc = OscChatboxMessage.Create("partial", sendImmediately: true, notificationSfx: false);
        if (!System.Text.Encoding.UTF8.GetString(partialOsc).Contains(",sTF", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OSC streaming type-tag check failed.");
        }

        var typingOsc = OscTypingMessage.Create(isTyping: true);
        if (!System.Text.Encoding.UTF8.GetString(typingOsc).Contains("/chatbox/typing", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OSC typing encoding check failed.");
        }

        if (AsrProviderFactory.ProviderIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            AsrProviderFactory.ProviderIds.Count)
        {
            throw new InvalidOperationException("ASR provider IDs must be unique.");
        }

        if (!SteamVrActionRuntime.IsRuntimeQuitEvent(EVREventType.VREvent_Quit) ||
            SteamVrActionRuntime.IsRuntimeQuitEvent(EVREventType.VREvent_ProcessQuit))
        {
            throw new InvalidOperationException("SteamVR runtime quit event classification failed.");
        }

        var unavailableProviders = AsrProviderFactory.CheckAvailability(new AsrConfiguration());
        if (unavailableProviders.Count != AsrProviderFactory.ProviderIds.Count ||
            unavailableProviders.Any(status => status.Available))
        {
            throw new InvalidOperationException("ASR provider availability check failed.");
        }

        foreach (var inputMethod in new[] { "clipboard-paste", "unicode-send-input", "keyboard" })
        {
            _ = new CapturedWindowTextOutput(new WindowsOutputConfiguration
            {
                TextInputMethod = inputMethod,
                OpenInputDelayMs = 100
            });
        }

        var chordAccumulator = new KeyboardChordAccumulator();
        _ = chordAccumulator.Process(0xA2, isDown: true);
        _ = chordAccumulator.Process(0x4B, isDown: true);
        if (chordAccumulator.Process(0x4B, isDown: false) is not null ||
            chordAccumulator.Process(0xA2, isDown: false) is not { } capturedChord ||
            !capturedChord.SequenceEqual(new[] { 0xA2, 0x4B }))
        {
            throw new InvalidOperationException("Keyboard chord capture check failed.");
        }

        var capsLockAccumulator = new KeyboardChordAccumulator();
        _ = capsLockAccumulator.Process(0x14, isDown: true);
        if (capsLockAccumulator.Process(0x14, isDown: false) is not { } capsLockChord ||
            !capsLockChord.SequenceEqual(new[] { 0x14 }))
        {
            throw new InvalidOperationException("Single keyboard key capture check failed.");
        }

        var mouseAccumulator = new MouseButtonCaptureAccumulator();
        _ = mouseAccumulator.Process(MousePttButtons.X1, isDown: true);
        if (mouseAccumulator.Process(MousePttButtons.X1, isDown: false) != MousePttButtons.X1)
        {
            throw new InvalidOperationException("Mouse button capture check failed.");
        }

        if (!MousePttButtons.TryDecodeWindowsEvent(0x0201, 0, out var leftButton, out var leftIsDown) ||
            leftButton != MousePttButtons.Left ||
            !leftIsDown ||
            !MousePttButtons.TryDecodeWindowsEvent(0x020B, 1u << 16, out var x1Button, out var x1IsDown) ||
            x1Button != MousePttButtons.X1 ||
            !x1IsDown ||
            !MousePttButtons.TryDecodeWindowsEvent(0x020C, 2u << 16, out var x2Button, out var x2IsDown) ||
            x2Button != MousePttButtons.X2 ||
            x2IsDown)
        {
            throw new InvalidOperationException("Windows mouse button message decoding check failed.");
        }

        var invalidMouseButtonWasRejected = false;
        try
        {
            _ = new GlobalMousePttInput(new MouseInputConfiguration { Button = "wheel-up" });
        }
        catch (ArgumentOutOfRangeException)
        {
            invalidMouseButtonWasRejected = true;
        }

        if (!invalidMouseButtonWasRejected)
        {
            throw new InvalidOperationException("Invalid mouse PTT button was accepted.");
        }

        var invalidDelayWasRejected = false;
        try
        {
            _ = new CapturedWindowTextOutput(new WindowsOutputConfiguration { OpenInputDelayMs = 5001 });
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("open-input delay", StringComparison.Ordinal))
        {
            invalidDelayWasRejected = true;
        }

        if (!invalidDelayWasRejected)
        {
            throw new InvalidOperationException("Invalid open-input delay was accepted.");
        }

        using (var mouseInput = new GlobalMousePttInput(new MouseInputConfiguration()))
        {
            mouseInput.StartAsync().GetAwaiter().GetResult();
            Thread.Sleep(50);
            mouseInput.StopAsync().GetAwaiter().GetResult();
        }

        using (var steamVrInput = new SteamVrPttInput(new SteamVrInputConfiguration()))
        {
            steamVrInput.StartAsync().GetAwaiter().GetResult();
            Thread.Sleep(50);
            steamVrInput.StopAsync().GetAwaiter().GetResult();
        }

        var invalidSteamVrPollWasRejected = false;
        try
        {
            _ = new SteamVrPttInput(new SteamVrInputConfiguration { PollIntervalMs = 0 });
        }
        catch (ArgumentOutOfRangeException)
        {
            invalidSteamVrPollWasRejected = true;
        }

        if (!invalidSteamVrPollWasRejected)
        {
            throw new InvalidOperationException("Invalid SteamVR poll interval was accepted.");
        }

        if (XInputGamepadPttInput.ListConnectedControllers().Count == 0)
        {
            var disconnectedGamepadWasRejected = false;
            try
            {
                _ = XInputGamepadPttInput
                    .CaptureButtonAsync(TimeSpan.FromMilliseconds(20))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (InvalidOperationException)
            {
                disconnectedGamepadWasRejected = true;
            }

            if (!disconnectedGamepadWasRejected)
            {
                throw new InvalidOperationException("Gamepad capture accepted an unavailable controller.");
            }
        }

        var runningApplications = ForegroundApplicationInspector.ListApplications();
        if (runningApplications.Any(application => application.ProcessId == Environment.ProcessId) ||
            runningApplications.Select(application => application.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            runningApplications.Count)
        {
            throw new InvalidOperationException("Running application enumeration check failed.");
        }

        if (AsrProviderFactory.GetCapabilities("funasr-nano-gguf")
            .HasFlag(AsrProviderCapabilities.TerminologyHints))
        {
            throw new InvalidOperationException("Fun-ASR-Nano must not advertise unsupported terminology hints.");
        }

        var profiles = new ApplicationProfileConfiguration[]
        {
            new()
            {
                Id = "desktop",
                Recognition = new ProfileRecognitionConfiguration { Provider = "paraformer-gguf" }
            },
            new()
            {
                Id = "vrchat",
                Match = new ApplicationMatchConfiguration { ProcessNames = ["VRChat.exe"] },
                Recognition = new ProfileRecognitionConfiguration { Provider = "paraformer-gguf" }
            }
        };
        var resolver = new ApplicationProfileResolver(profiles, "desktop");
        if (resolver.Resolve("vrchat").Id != "vrchat" ||
            resolver.Resolve("notepad.exe").Id != "desktop")
        {
            throw new InvalidOperationException("Application profile resolution check failed.");
        }

        var explicitDefaultResolver = new ApplicationProfileResolver(profiles, "vrchat");
        if (explicitDefaultResolver.Resolve("notepad.exe").Id != "desktop")
        {
            throw new InvalidOperationException("Empty process match must select the current-foreground profile.");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  --self-test");
        Console.WriteLine("  --list-microphones");
        Console.WriteLine("  --list-gamepads");
        Console.WriteLine("  --list-gpus");
        Console.WriteLine("  --list-providers");
        Console.WriteLine("  --config <path> --list-profiles");
        Console.WriteLine("  --config <path> [--profile <id>] [--provider <id>] --daemon");
        Console.WriteLine("  --config <path> [--profile <id>] [--provider <id>] --recognize <audio-file>");
        Console.WriteLine("  --config <path> [--profile <id>] --send <text>");
        Console.WriteLine("  --config <path> [--profile <id>] [--provider <id>] --transcribe <audio-file>");
    }
}

internal sealed record CommandLineOptions(
    string ConfigurationPath,
    string? ProfileOverride,
    string? ProviderOverride,
    bool RunSelfTest,
    bool ListMicrophones,
    bool ListGamepads,
    bool ListGpus,
    bool ListProviders,
    bool ListProfiles,
    bool RunDaemon,
    string? RecognizeOnlyAudioPath,
    string? TextToSend,
    string? AudioPath,
    bool ShowHelp)
{
    public static CommandLineOptions Parse(string[] args)
    {
        var configurationPath = "appsettings.json";
        string? profileOverride = null;
        string? providerOverride = null;
        var selfTest = false;
        var listMicrophones = false;
        var listGamepads = false;
        var listGpus = false;
        var listProviders = false;
        var listProfiles = false;
        var daemon = false;
        string? recognizeOnlyAudio = null;
        string? text = null;
        string? audio = null;
        var help = args.Length == 0;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config":
                    configurationPath = ReadValue(args, ref index, "--config");
                    break;
                case "--provider":
                    providerOverride = ReadValue(args, ref index, "--provider");
                    break;
                case "--profile":
                    profileOverride = ReadValue(args, ref index, "--profile");
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--list-microphones":
                    listMicrophones = true;
                    break;
                case "--list-gamepads":
                    listGamepads = true;
                    break;
                case "--list-gpus":
                    listGpus = true;
                    break;
                case "--list-providers":
                    listProviders = true;
                    break;
                case "--list-profiles":
                    listProfiles = true;
                    break;
                case "--daemon":
                    daemon = true;
                    break;
                case "--recognize":
                    recognizeOnlyAudio = ReadValue(args, ref index, "--recognize");
                    break;
                case "--send":
                    text = ReadValue(args, ref index, "--send");
                    break;
                case "--transcribe":
                    audio = ReadValue(args, ref index, "--transcribe");
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        var commandCount = new[] { selfTest, listMicrophones, listGamepads, listGpus, listProviders, listProfiles, daemon, recognizeOnlyAudio is not null, text is not null, audio is not null }.Count(value => value);
        if (!help && commandCount != 1)
        {
            throw new ArgumentException("Choose exactly one command.");
        }

        return new CommandLineOptions(
            configurationPath,
            profileOverride,
            providerOverride,
            selfTest,
            listMicrophones,
            listGamepads,
            listGpus,
            listProviders,
            listProfiles,
            daemon,
            recognizeOnlyAudio,
            text,
            audio,
            help);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return args[index];
    }
}
