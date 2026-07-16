# Desktop UI Architecture

## Decision

Use a native .NET 8 WPF settings application. The settings window calls the shared `RuntimeController` directly and persists the same configuration used by the CLI and background runtime. No browser control, web assets, JavaScript bridge, or browser runtime is required.

The settings window is a configuration client only. Push-to-talk input, audio capture, ASR, window targeting, VRChat OSC, and provider lifecycle remain native .NET services and continue running when the window is closed to the tray.

## Process shape

```text
VoiceInput.App (WPF process)
  native application shell
    tray icon
    startup and single-instance handling
    settings-window lifecycle
  native WPF configuration UI
    direct RuntimeController calls and events
    partial page rebuilds
  existing .NET services
    PTT inputs
    audio capture
    ASR providers
    text outputs
    configuration and diagnostics
```

The application stays in one process. A separate worker process is justified only for native ASR components that require crash isolation or independent lifecycle management.

## Native host responsibilities

- Start and stop background services independently of the settings window.
- Own the system tray icon, autostart setting, and application exit command.
- Save pending configuration when the settings window closes and recreate the window from the tray while native services continue running.
- Keep all OS handles, model paths, provider processes, and future provider secrets in native code.
- Persist native, provider, model, and WPF failures to daily UTF-8 logs under `%LocalAppData%\VRChatVoiceInput\Logs` for 14 days.

## Settings responsibilities

- Microphone and input-device selection.
- Keyboard, mouse, XInput, and SteamVR binding configuration through native capture services.
- ASR provider, model, language, CPU/Vulkan device, and future cloud endpoint settings.
- Output target and per-application profile editing.
- Runtime download progress, provider health, benchmark results, live process memory, average recognition time, per-preset output tests, and diagnostic logs.
- Debounced automatic configuration saves with validation errors shown in the WPF interface. Changes made while a save is in progress remain dirty and are saved in the next pass.

The WPF layer does not own runtime state or bypass controller-side validation. Long-running work such as downloads, benchmarks, microphone monitoring, and runtime transitions is asynchronous and reports progress through controller events.

## Window lifecycle

- `application.closeToTray` controls whether closing settings leaves the background service running.
- Pending changes are saved before disposal. Closing waits at most five seconds for saving; a validation failure, write failure, or timeout is logged and shown once, then unsaved changes are discarded so the window and tray Exit command cannot become permanently blocked.
- Reopening settings creates a fresh `NativeMainWindow` connected to the existing controller.
- Application exit closes the settings window only after pending saves complete, then disposes the tray and runtime services.

## Packaging

- Publish the WPF application as self-contained Windows x64 initially.
- Do not package a browser SDK, browser loader, JavaScript runtime, or generated web assets.
- Keep ASR runtimes and models separately versioned so UI updates do not require re-downloading model files.
- Auto-save restarts the profile host only when the active runtime fingerprint changes. Application-only settings and model settings for providers not used by the active runtime are saved without interrupting recognition.

## Implementation status

- Five local ASR providers share the same configuration and runtime contracts.
- The CLI and WPF application reuse the same profile-driven background host.
- Keyboard, mouse, XInput, SteamVR, file input, captured-window output, and VRChat OSC paths are available.
- The native host provides single-instance handling, tray lifecycle, runtime control, atomic configuration saves, model/profile editors, microphone monitoring, downloads, and diagnostics.
- The WPF interface covers general settings, profile input/processing/output, model components, GPU selection, and per-preset output tests without creating a browser process.
