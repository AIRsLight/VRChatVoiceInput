# Desktop UI Architecture

## Decision

Use a .NET 8 WPF host with two interchangeable configuration surfaces: the established embedded WebView2 client and a native WPF client. WebView2 remains the compatibility default while the native client is exercised and completed. Both clients call the same `RuntimeController` and persist the same configuration schema.

Both settings surfaces are configuration clients only. Push-to-talk input, audio capture, ASR, window targeting, VRChat OSC, and provider lifecycle remain native .NET services and continue running when the configuration window is closed.

## Process shape

```text
VoiceInput.App (WPF process)
  native shell
    tray icon
    startup and single-instance handling
    settings-surface selection
  native WPF configuration UI
    direct RuntimeController calls and events
  optional WebView2 configuration UI
    packaged HTML/CSS/TypeScript assets
    versioned message bridge
    no local HTTP server
  existing .NET services
    PTT inputs
    audio capture
    ASR providers
    text outputs
    configuration and diagnostics
```

The first implementation should stay in one process. A separate worker process is justified only if native ASR crashes cannot be isolated adequately or provider restarts prove unreliable.

## Native host responsibilities

- Start and stop background services independently of the configuration window.
- Own the system tray icon, autostart setting, and application exit command.
- Select native WPF or WebView2 from `application.settingsInterface`; allow `--native-ui` and `--web-ui` to override one process run.
- Save pending configuration and dispose the selected settings surface whenever it closes; recreate the configured surface from the tray while native services continue running.
- Create the WebView2 control lazily only when the WebView2 surface is selected.
- Detect the Evergreen WebView2 Runtime before creating the control.
- Offer the Microsoft Evergreen bootstrapper when the Runtime is missing.
- Show a native recovery panel with retry and local-log actions if WebView2 cannot initialize or its rendering process fails.
- Keep all OS handles, secrets, model paths, and provider processes on the .NET side.

Windows 11 normally includes the Evergreen Runtime and most supported Windows 10 installations already have it. The installer must still detect the edge case where it is absent. Do not bundle Fixed Version by default because of its large package cost and separate update responsibility.

## Shared client responsibilities

- Microphone and input-device selection.
- Keyboard, mouse, XInput, and SteamVR binding configuration. Native host services capture keyboard, mouse, and XInput buttons; the web application only starts capture and displays the result.
- ASR provider, model, language, CPU/Vulkan device, and cloud endpoint settings.
- Output target and per-application profile editing.
- Runtime download progress, provider health, benchmark results, live process memory, average recognition time, per-preset output tests, and diagnostic logs. Native, provider, bridge, WPF, and WebView failures are persisted to daily UTF-8 logs under `%LocalAppData%\VRChatVoiceInput\Logs` for 14 days; the Diagnostics view exposes the active path.
- Debounced automatic configuration saves with validation errors returned by the native host. Changes made while a save is in progress remain dirty and are saved in the next pass.

The WebView2 client uses TypeScript and Vite with locally packaged assets. The native client uses WPF controls and partial page updates. Neither client owns runtime state or bypasses controller-side validation.

## Message bridge

Use `window.chrome.webview.postMessage` and `CoreWebView2.WebMessageReceived`. Messages must use a versioned envelope:

```json
{
  "version": 1,
  "id": "request-id",
  "type": "configuration.get",
  "payload": {}
}
```

Every request receives either a typed result or a structured error. The C# host must deserialize into known message types, validate every field, reject unknown privileged operations, and never execute command strings supplied by JavaScript.

Long-running operations such as model downloads and benchmarks return an operation ID and publish progress events instead of holding a single request open.

## Navigation and security

- Load only packaged local application assets through a virtual-host mapping.
- Block arbitrary top-level navigation and new-window requests.
- Open approved documentation links through the user's external browser.
- Disable DevTools, browser context menus, accelerator keys, and status UI in release builds.
- Do not expose broad host objects with `AddHostObjectToScript`; prefer explicit JSON messages.
- Keep API keys out of the web document and browser storage.
- Set a restrictive Content Security Policy and do not load CDN scripts.

## Packaging

- Publish the WPF application as self-contained Windows x64 initially.
- Reference the release WebView2 SDK package, but use the shared Evergreen Runtime.
- Detect Runtime availability during installation and on startup.
- Use the small online Evergreen bootstrapper for connected systems and document an offline standalone installer path.
- Keep ASR runtimes and models separately versioned so UI updates do not require re-downloading model files.
- Auto-save restarts the profile host only when the active runtime fingerprint changes. Application-only settings and model settings for providers not used by the active runtime are saved without interrupting recognition. Expanded model advanced settings remain open across required runtime-state redraws.

## Implementation status

The initial implementation gate has been met:

- Four local ASR providers share the same configuration and runtime contracts.
- The CLI and GUI reuse the same profile-driven background host.
- Keyboard/file input, captured-window output, and VRChat OSC paths are available.
- The native host provides single-instance handling, tray lifecycle, runtime control, atomic configuration saves, model/profile editors, and diagnostics.
- The packaged web assets are loaded through a local virtual host with arbitrary navigation, DevTools, context menus, and browser accelerators disabled.
- The optional native WPF surface covers general settings, profile input/processing/output, model components and downloads, GPU selection, microphone monitoring, diagnostics, and output tests without creating a browser process.

## References

- [Microsoft: Distribute your app and the WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- [Microsoft: Evergreen vs. Fixed Version](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version)
- [Microsoft: Get started with WebView2 in WPF](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf)
