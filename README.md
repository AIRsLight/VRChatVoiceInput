# Voice Input Companion

[![CI](https://github.com/AIRsLight/VRChatVoiceInput/actions/workflows/ci.yml/badge.svg)](https://github.com/AIRsLight/VRChatVoiceInput/actions/workflows/ci.yml)

A standalone Windows companion application for push-to-talk speech-to-text input. VRChat is the first integration target, while ordinary Windows applications and non-VR games are part of the planned product scope.

The current default is the local Paraformer Q5_0 GGUF provider on CPU. Q8_0 and Q4_0 remain selectable on the Models page. Transcribed text can be injected into a captured Windows foreground window or sent to VRChat through OSC Chatbox, so the Chatbox does not need to be opened first.

## Current scope

- Persistent local Paraformer, SenseVoice, and Fun-ASR-Nano GGUF providers, persistent sherpa-onnx Qwen3-ASR, and an official whisper.cpp server provider.
- Selectable Paraformer Q8/Q5/Q4, SenseVoice Q8/Q5, and Fun-ASR-Nano F16/Q8 encoder variants with pinned, hash-verified downloads.
- Optional Silero-VAD segmented recognition while PTT is held. VRChat OSC can refresh the Chatbox after each completed segment; Windows outputs receive one joined final result after PTT release.
- Per-command provider selection for local model comparison.
- VRChat OSC `/chatbox/input` sender with 144-character chunking.
- Global keyboard, five-button mouse, XInput, and SteamVR action hold-to-talk with WASAPI microphone capture.
- Captured-window chat workflow with open hotkey, configurable delay, clipboard/Unicode/keyboard input, submit hotkey, and focus-change protection.
- Provider capability and recognition-option contracts with deferred terminology-hint support.
- Per-application profiles with a running-process picker, dynamic foreground fallback, native keyboard/XInput binding capture, provider selection, hotwords, output method, and submission hotkeys.
- Native WPF settings interface with automatic configuration saves, tray lifecycle, runtime control, preset/model editors, live process memory and recognition timing diagnostics, per-preset output testing, and runtime logs. Native, model, provider, and WPF failures are written to daily files under `%LocalAppData%\VRChatVoiceInput\Logs` and retained for 14 days.
- Explicit runtime profile activation, per-profile microphone overrides, and installed-model availability checks.
- Live native level meters for every active microphone.
- Enumerated Vulkan GPU selection for the bundled SenseVoice and whisper.cpp Vulkan runtimes, including Intel and AMD integrated adapters exposed by the Windows graphics driver.
- Input, Processing, and Output profile tabs with per-profile minimum recording duration.
- CLI smoke-test, device-enumeration, file-recognition, and daemon commands.

Extended HID controllers, cloud providers, and automatic CPU/Vulkan selection remain deferred. They will consume the same provider, input, and output interfaces.

The staged implementation plan, including global keyboard/gamepad PTT and safe text injection into non-VR applications, is documented in [docs/DEVELOPMENT_PLAN.md](docs/DEVELOPMENT_PLAN.md).

The application profile schema is documented in [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

The native WPF configuration architecture is documented in [docs/UI_ARCHITECTURE.md](docs/UI_ARCHITECTURE.md).

Initial CPU results for SenseVoice, Paraformer, Fun-ASR-Nano, Qwen3-ASR, and whisper.cpp are recorded in [docs/MODEL_BENCHMARK.md](docs/MODEL_BENCHMARK.md).

## Quick start

1. Fetch the pinned OpenVR native dependency. Native binaries and model weights
are intentionally not stored in Git:

```powershell
.\fetch-openvr.ps1
```

2. Copy `appsettings.example.json` to `appsettings.json`.
3. Set the executable and model paths for the providers you intend to use, or install standard components from the Models page. Each item in `profiles.items` selects its provider through `recognition.provider`.
4. List available microphones and copy the required device ID into the configuration, or leave it as `null` to use the default communications microphone:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --list-microphones
```

XInput controllers can be enumerated separately:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --list-gamepads
```

Vulkan GPU devices and their runtime indices can be enumerated with:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --list-gpus
```

Available ASR provider IDs can be listed with:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --list-providers
```

Configured application profiles can be listed with:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --config appsettings.json --list-profiles
```

5. Build and validate the core:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --config appsettings.json --self-test
```

6. Build and open the settings application. Start the configured runtime from the toolbar after the required components are installed. The application remains available from the system tray when the window is closed:

```powershell
dotnet run --project src/VRChatVoiceInput.App -c Release -- --config appsettings.json
```

The development build can also be opened directly at `src/VRChatVoiceInput.App/bin/Release/net8.0-windows/VRChatVoiceInput.App.exe`. It searches its parent directories for the repository `appsettings.json`.

7. Alternatively, start profile-aware hold-to-talk mode without the GUI. The example profiles bind F8 (`virtualKeys: [119]`):

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --config appsettings.json --daemon
```

Keep the target application focused, hold F8 while speaking, and release it to recognize and route the result. The foreground process selects the profile. The target and profile are captured when F8 is pressed; changing focus before recognition finishes cancels Windows injection.

8. The built-in `vrchat` profile matches `VRChat.exe` and routes through OSC. Enable OSC in VRChat and run either the GUI or daemon command. A text-only smoke test is also available:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --config appsettings.json --profile vrchat --send "Hello from VRChat Voice Input"
```

9. Test a recorded audio file:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --config appsettings.json --recognize sample.wav
```

Override the configured provider for one run without editing the configuration:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --config appsettings.json --provider funasr-nano-gguf --recognize sample.wav
```

`--recognize` prints the transcript without injecting it anywhere. To recognize and route the result through the configured output, use:

```powershell
dotnet run --project src/VRChatVoiceInput.Cli -- --config appsettings.json --transcribe sample.wav
```

## Runtime selection

Supported provider IDs are:

- `paraformer-gguf`: persistent, light, fast Mandarin-first default with corrected English BPE spacing and optional local Chinese/English punctuation restoration.
- `sensevoice-gguf`: persistent multilingual speech, emotion, and event model with independently packaged CPU and Vulkan workers; only transcript output is currently consumed.
- `funasr-nano-gguf`: persistent higher-memory Chinese quality option using an encoder GGUF plus Qwen3 language-model GGUF on CPU or Vulkan.
- `qwen3-asr`: persistent CPU-only sherpa-onnx Qwen3-ASR 0.6B INT8 provider with multilingual language hints and hotwords.
- `whisper-cpp`: persistent broad multilingual fallback using the official local whisper.cpp HTTP server on CPU or Vulkan.

SenseVoice, Paraformer, and Fun-ASR-Nano use the official FunASR GGUF model format. Their locally patched runtimes load model weights once per runtime host. SenseVoice and Fun-ASR-Nano can upload their encoder weights to a selected Vulkan device and keep them resident; Nano also offloads every Qwen language-model layer to that device. Paraformer remains CPU-only. The Paraformer patch also mirrors FunASR's Python English BPE post-processing. Optional Paraformer punctuation uses sherpa-onnx CT-Transformer on CPU. Whisper.cpp runs its official local server and keeps the model loaded; `useGpu` selects the bundled Vulkan server and `gpuDeviceIndex` selects an enumerated Vulkan adapter.

Automatic foreground routing creates providers lazily on their first recognition, so unused profiles do not reserve model memory. Explicitly selecting **Use preset** starts and preloads that preset immediately.

The application does not automatically select Vulkan. GPU mode and its adapter must be selected explicitly so an idle integrated GPU can be chosen instead of the discrete GPU used by VR.

The Models page shows each provider's supported languages, packaged model size, measured peak memory, and intended use. It can download or verify official model weights with resume support and SHA-256 validation. Repository revisions and the immutable URL policy are documented in [docs/MODEL_DOWNLOADS.md](docs/MODEL_DOWNLOADS.md); benchmark methodology is documented in [docs/MODEL_BENCHMARK.md](docs/MODEL_BENCHMARK.md).

The settings application supports Chinese, Japanese, and English. It follows the Windows UI language by default, can be changed manually on General, and falls back to English for unsupported system languages.

The ASR contract reserves terminology hints and weighted terminology capabilities. Qwen3-ASR consumes per-profile terminology hints; providers that do not advertise the capability continue to warn instead of silently ignoring configured hotwords.

## Portable package

The resident SenseVoice, Paraformer, and Fun-ASR-Nano executables are built from pinned upstream source commits and tracked MIT-licensed patches. Rebuild them after changing a patch or on a fresh development checkout with:

```powershell
.\build-sensevoice-runtime.ps1
.\build-paraformer-runtime.ps1
.\build-funasr-nano-runtime.ps1
```

The SenseVoice and Fun-ASR-Nano scripts each build a small CPU worker and a static Vulkan worker. They use `artifacts/vulkan-sdk-1.4.350.0` by default; pass `-VulkanSdkDirectory <path>` when using another Vulkan SDK installation.

Exit the tray application, then double-click `build-portable.cmd` or run:

```powershell
.\build-portable.ps1
```

The default `win-x64` build is self-contained and includes only models and native ASR runtimes selected by the active configuration profiles. For GPU-capable providers, only the currently selected CPU or Vulkan runtime is included. It creates a portable directory and ZIP under `artifacts/portable/`. Missing runtime variants can be installed later from the Models page.

To include every provider model referenced by `appsettings.json`, include every file under `models/`, or skip ZIP creation while iterating:

```powershell
.\build-portable.ps1 -ModelSet configured -SkipArchive
.\build-portable.ps1 -ModelSet all
.\build-portable.ps1 -ModelSet all -RuntimeSet all
```

The target computer does not need a separately installed .NET runtime or browser runtime.

For a source-only portable shell that downloads its selected models and runtimes
after first launch, use:

```powershell
.\build-portable.ps1 -ModelSet none -RuntimeSet none -ConfigurationPath appsettings.example.json
```

## Repository and automation

The repository contains source, patches, documentation, configuration examples,
and static application assets. Native DLL/EXE files, model weights, recordings,
archives, local configuration, and build products are
excluded by `.gitignore`. `fetch-openvr.ps1` retrieves the pinned Valve OpenVR
DLL with size and SHA-256 verification.

GitHub Actions provides:

- `CI`: .NET restore, build, and tests on pushes and pull requests.
- `Portable release`: a manually triggered or `v*` tag-triggered source-only
  portable ZIP. Tagged runs also publish the ZIP as a GitHub release asset.

## Current implementation status

- Global Windows keyboard hold-to-talk: implemented for configurable key chords.
- Global Windows mouse hold-to-talk: implemented for left, right, middle, and both side buttons.
- Xbox-compatible gamepad hold-to-talk: implemented through XInput; select it with `input.mode: xinput`.
- SteamVR controller hold-to-talk: implemented as an OpenVR background action client with bundled action manifests and controller bindings; select it with `input.mode: steamvr`.
- WASAPI recording: implemented with default or explicit capture-device selection.
- Captured foreground-window output: implemented with clipboard paste, Unicode `SendInput`, pure keyboard events, open/submit hotkeys, delay, and focus-change protection.
- VRChat OSC output: implemented.
- Segmented recognition and OSC Chatbox streaming updates: implemented.
- Extended HID gamepad input: planned next.
- Application profiles and configurable submission hotkeys: implemented.
- Native WPF settings application and tray lifecycle: implemented.
- Clipboard-based text injection with best-effort clipboard restoration: implemented.
