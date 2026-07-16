# Application Profile Configuration

## Structure

Configuration is split into global runtime definitions and application profiles:

- `schemaVersion`: configuration contract version; currently `1`.
- `application`: desktop lifecycle, interface-language, and model-download-source settings.
- `audio`: global microphone and recording-duration defaults inherited by profiles.
- `asr`: installed provider executables and model paths.
- `profiles`: application matching and per-application behavior.

Provider model paths are global so multiple profiles can reuse one loaded model definition. A profile selects a provider by ID rather than duplicating its native paths.

## Interface language

`application.uiLanguage` accepts `auto`, `zh`, `ja`, or `en`. `auto` maps Chinese, Japanese, and English Windows UI cultures to the corresponding interface; every unsupported system language falls back to English. The General page allows this value to be changed manually. The WPF settings UI, message-box titles, and tray menu use the same setting.

`application.modelDownloadSource` accepts `official` or `hf-mirror` and defaults to `official` for existing configurations. The General page exposes both as source buttons. `hf-mirror` rewrites only Hugging Face resolve URLs to `https://hf-mirror.com`; GitHub release URLs remain unchanged, and every downloaded file still has to pass its catalog SHA-256 check.

When `application.closeToTray` is `true`, closing the settings window keeps the native PTT/ASR service and tray icon running but disposes the WPF window. Opening settings from the tray creates a fresh window. Pending debounced configuration changes are saved before disposal. If validation or writing fails, the error is logged and a localized warning explains that the unsaved changes will be discarded; acknowledging the warning always allows the window or application to close.

## Profile selection

The desktop application activates `profiles.defaultProfileId` on cold start. This is a real initial runtime profile, not merely an editor selection. `Use profile` can explicitly activate another profile, and `Resume automatic application routing` enters automatic mode.

Automatic and CLI selection order is:

1. `--profile <id>` command-line override or the desktop UI's explicit `Use profile` runtime override.
2. First enabled profile whose `match.processNames` contains the foreground process.
3. An enabled profile with an empty `match.processNames`, representing the application that is in the foreground when PTT is pressed. If the default profile is empty, it is preferred; otherwise configuration order is used.
4. `profiles.defaultProfileId` when no process match or current-foreground profile exists.

Process matching is case-insensitive and treats `VRChat` and `VRChat.exe` as the same name. A process may be assigned to multiple profiles because only one explicitly selected profile runs at a time. In automatic-routing mode, the first enabled matching profile in configuration order wins. Profile names must be unique, and duplicate process names inside one profile are rejected.

For `captured-window` output, the current foreground window is captured and locked when PTT is pressed. Leaving `match.processNames` empty therefore creates a reusable profile for whichever desktop application is active, without writing that process name back into the configuration.

The profile editor opens a process picker containing applications with visible top-level windows. It supports selecting multiple running processes. Previously configured processes that are not currently running remain visible so opening the picker does not silently discard them.

In daemon mode the foreground application is evaluated when a PTT binding is pressed. The selected profile is then locked until recording, recognition, and output complete.

Selecting a row in the desktop profile editor only opens it for editing. `Use profile` explicitly activates that profile and restarts the runtime. This avoids expensive provider reloads while browsing profiles.

## Profile fields

```json
{
  "id": "Example Game",
  "enabled": true,
  "builtIn": false,
  "match": {
    "processNames": ["ExampleGame.exe"]
  },
  "audio": {
    "deviceId": null,
    "minimumDurationMs": null
  },
  "input": {},
  "recognition": {},
  "output": {}
}
```

`id` is both the profile name shown in the interface and its unique configuration identifier. Names may contain spaces and non-ASCII characters but cannot be empty or duplicated case-insensitively. `builtIn` identifies profiles shipped by the application. It does not make a profile immutable in the JSON configuration.

The only protected built-in profile is `VRChat`. It matches `VRChat.exe`, enables both left Ctrl (`VK_LCONTROL`, `0xA2`) and the SteamVR PTT action, and sends recognized text through the VRChat OSC Chatbox output. The shipped `Desktop default` starter remains available for foreground-window output but is an ordinary profile that can be duplicated, renamed, or deleted.

Existing installations migrate the former built-in `VRCHAT Desktop` profile into `VRChat`: its keyboard binding is preserved, keyboard and SteamVR triggers are enabled together, references to it as the default profile are redirected to `VRChat`, and the redundant built-in profile is removed. Other custom profiles are not merged or deleted.

## Profile microphone

`audio.deviceId` inside a profile selects its capture device. `null` inherits the global `audio.deviceId`; this allows the VRChat profile to use a remote VR microphone while desktop profiles use the normal communications device. Disconnected configured devices remain visible as unavailable in the editor instead of silently changing microphones.

`audio.minimumDurationMs` sets the profile's minimum recording duration. `null` inherits the global value. Recordings shorter than the effective value are discarded before recognition.

## PTT input

`input.modes` enables one or more trigger adapters in the same profile. Pressing any enabled binding starts one recording session; when bindings overlap, recording ends only after every pressed binding has been released. The legacy singular `input.mode` field remains readable and is migrated to a one-element `modes` array when the desktop application opens.

Combined keyboard and SteamVR example:

```json
"input": {
  "modes": ["keyboard", "steamvr"],
  "keyboard": {
    "virtualKeys": [162],
    "suppressKey": false
  },
  "steamVr": {
    "actionPath": "/actions/voiceinput/in/ptt",
    "pollIntervalMs": 8
  }
}
```

Keyboard chord:

```json
"input": {
  "modes": ["keyboard"],
  "keyboard": {
    "virtualKeys": [17, 119],
    "suppressKey": false
  }
}
```

`virtualKeys` contains Windows virtual-key codes. `[119]` is F8, `[20]` is Caps Lock, and `[17, 119]` is Ctrl+F8. The older single `virtualKey` field remains supported.

The desktop editor captures keyboard chords through a native `WH_KEYBOARD_LL` hook. This supports any key exposed by Windows as a virtual key, including left/right modifiers, Windows keys, media keys, OEM punctuation keys, Print Screen, and numpad keys. Hardware-only `Fn` keys and secure attention sequences such as Ctrl+Alt+Delete are not ordinary virtual-key input and cannot be bound.

Mouse button:

```json
"input": {
  "modes": ["mouse"],
  "mouse": {
    "button": "x1",
    "suppressButton": false
  }
}
```

`button` accepts `left`, `right`, `middle`, `x1`, or `x2`. The desktop editor captures the next complete press and release through a native `WH_MOUSE_LL` hook. `suppressButton` prevents the selected button event from reaching the foreground application while the matching profile is active.

XInput gamepad:

```json
"input": {
  "modes": ["xinput"],
  "gamepad": {
    "userIndex": 0,
    "buttonMask": 4096,
    "pollIntervalMs": 8
  }
}
```

`buttonMask` may combine multiple XInput button flags to form a chord. The profile editor listens to all four XInput slots during binding; the first controller button pressed updates both `userIndex` and `buttonMask`, so the controller index does not need to be entered manually.

SteamVR action input:

```json
"input": {
  "modes": ["steamvr"],
  "steamVr": {
    "actionPath": "/actions/voiceinput/in/ptt",
    "pollIntervalMs": 8
  }
}
```

The Windows runtime connects as an OpenVR background application and automatically reconnects when SteamVR starts or restarts. It does not install an OpenVR driver or create an overlay. The bundled action manifest currently owns the fixed `/actions/voiceinput/in/ptt` action path. Oculus/Quest Touch controllers default to the left-hand X button; use **Controller bindings** on the Input tab to change the physical controller button in SteamVR. Controllers without an X input retain a hardware-compatible default binding.

Bundled defaults use the right menu button for Vive/generic controllers, the right A button for Index, and the left X button for Oculus/Quest Touch controllers. User bindings in SteamVR override these defaults.

## Recognition

```json
"recognition": {
  "provider": "paraformer-gguf",
  "language": "auto",
  "hotwords": ["VRChat", "OpenVR"],
  "streamingEnabled": false
}
```

`provider` selects one of the globally defined local providers. `language` and `hotwords` are profile-owned. Qwen3-ASR applies both values per recognition stream; `auto` leaves language detection to the model.

`streamingEnabled` makes the recorder feed 16 kHz mono PCM into Silero VAD while PTT is held. Each completed speech segment is recognized immediately, and the segment transcripts are joined in order. The shared VAD settings are global:

```json
"asr": {
  "streaming": {
    "sileroVadModelPath": "models/silero_vad.onnx",
    "threshold": 0.5,
    "minimumSilenceSeconds": 0.5,
    "minimumSpeechSeconds": 0.25,
    "maximumSegmentSeconds": 15
  }
}
```

Streaming recognition and streaming output are separate behaviors:

- `vrchat-osc` with `sendImmediately: true` updates Chatbox after each recognized segment and sends the joined final text when PTT is released.
- `captured-window`, or OSC with `sendImmediately: false`, performs the same segmented recognition but submits only the joined final text after PTT release.
- Missing `silero_vad.onnx` does not disable ordinary final recognition, but a streaming-enabled profile cannot be activated until the VAD model is installed.
- All five local providers stay loaded across segments. Fun-ASR-Nano uses a resident stdin/stdout worker, while whisper.cpp uses its official loopback HTTP server.

The desktop UI checks the configured executable, model, language-model, and enabled VAD paths. Providers with missing required files are marked `Not installed`, but incomplete model and runtime selections can still be saved so source-only packages support staged downloads without reporting a configuration error after every component. Service startup is the enforcement boundary: the current runtime profile is checked before any provider is created, startup is refused when required files are missing, and the WPF interface shows a persistent warning with a direct link to the Models page. Clicking Start also shows the complete localized missing-component list. Streaming-enabled profiles include the Silero VAD model in this readiness check.

Paraformer can optionally restore Chinese and English punctuation after recognition:

```json
"paraformer": {
  "executablePath": "runtimes/llama-funasr-paraformer.exe",
  "modelPath": "models/paraformer-q5_0.gguf",
  "vadModelPath": "models/fsmn-vad.gguf",
  "usePunctuation": false,
  "punctuationModelPath": "models/paraformer-punctuation-int8.onnx"
}
```

The Models page lists Paraformer Q8_0, Q5_0, and Q4_0 as independently downloadable assets. Downloading a version does not activate it. New configurations use Q5_0, and the active-version selector permits switching among installed standard variants.

SenseVoice similarly exposes independent Q8_0 and Q5_0 downloads while retaining Q8_0 as the quality default. Fun-ASR-Nano exposes Q4_K_M, Q5_K_M, and Q8_0 language models in the primary selector and F16/Q8_0 encoders in a separate component selector; new configurations use the Q4_K_M language model and Q8_0 encoder. Components with no alternative version remain plain text. Existing active paths remain unchanged until an installed version is selected explicitly.

The default model view exposes quantization selection and optional punctuation/VAD capabilities. Missing quantizations show a Download and use action; the active path is changed only after installation succeeds. Punctuation can be enabled after its model is available. Only manual paths and numeric tuning values are hidden under Advanced model configuration. Per-profile streaming recognition remains on the profile Processing tab because enabling segmented recognition is a profile routing decision rather than a global model property.

The punctuation model is a local sherpa-onnx CT-Transformer for Chinese and English. It is loaded with the active Paraformer provider and released when the runtime stops. The option defaults to `false` so existing installations do not become unavailable before the additional model is downloaded.

Qwen3-ASR joins the configured hotwords into its terminology prompt. Hotwords remain stored but inactive for the current GGUF providers, and the CLI prints a warning when a profile contains hotwords that its provider cannot consume.

Qwen3-ASR is configured globally and loaded once per runtime host:

```json
"qwen3Asr": {
  "convFrontendPath": "models/qwen3-asr/conv_frontend.onnx",
  "encoderPath": "models/qwen3-asr/encoder.int8.onnx",
  "decoderPath": "models/qwen3-asr/decoder.int8.onnx",
  "tokenizerPath": "models/qwen3-asr/tokenizer",
  "threadCount": 4,
  "maxNewTokens": 256
}
```

The Windows sherpa-onnx package uses CPU for this provider. Activating a Qwen3-ASR profile loads the model; stopping or switching the runtime releases it. Multiple enabled profiles share the same provider instance instead of loading duplicate model copies.

## Windows output

```json
"output": {
  "mode": "captured-window",
  "windows": {
    "openInput": {
      "mode": "hotkey",
      "virtualKeys": [13]
    },
    "openInputDelayMs": 100,
    "textInputMethod": "clipboard-paste",
    "requireSameForeground": true,
    "submission": {
      "mode": "hotkey",
      "virtualKeys": [17, 13]
    }
  }
}
```

Implemented `textInputMethod` values:

- `clipboard-paste`: default; snapshot the current clipboard, temporarily place text on it, press Ctrl+V, then restore the snapshot after a short consumption wait.
- `unicode-send-input`: inject UTF-16 keyboard events directly.
- `keyboard`: type virtual-key events through the target keyboard layout; unsupported characters fail explicitly. Active IMEs can intercept these events, so use an English/direct-input layout for this mode.

Window output uses synthetic keyboard input for text insertion and optional open/submit hotkeys. Anti-cheat software may classify this as input injection and may suspend or ban an account. Use `captured-window` only in applications where automated input is explicitly permitted. This warning does not apply to the global keyboard/mouse PTT listeners themselves or to `vrchat-osc` output.

`openInput` uses `none` or `hotkey`. When enabled, its keys are pressed first and the runtime waits `openInputDelayMs` before inserting text. The delay accepts `0` through `5000` milliseconds.

`submission` modes:

- `none`: insert text without submitting it.
- `hotkey`: press the configured keys in order and release them in reverse order after text injection.

Common submission chords are `[13]` for Enter and `[17, 13]` for Ctrl+Enter. The legacy `pressEnterAfterInjection` field remains supported.

## VRChat output

The built-in `VRChat` profile matches `VRChat.exe` and uses:

```json
"output": {
  "mode": "vrchat-osc",
  "vrChat": {
    "host": "127.0.0.1",
    "port": 9000,
    "sendImmediately": true,
    "maxChatboxCharacters": 144
  }
}
```

This sends `/chatbox/input` directly and does not use Windows text injection or a submission hotkey.

When profile streaming is enabled, `sendImmediately: true` also sends `/chatbox/typing`, interim `/chatbox/input` packets without notification sound, and one final `/chatbox/input` packet with notification sound. The visible rolling text is limited to the last `maxChatboxCharacters` Unicode characters.

## Legacy configuration

When `profiles.items` is empty, the application synthesizes a `legacy-default` profile from the older top-level `input`, `asr.provider`, `output`, and `vrChat` fields. New configurations should use explicit profiles.

## GPU device selection

The settings page enumerates physical devices through the Windows Vulkan loader. Device indices therefore match ggml's Vulkan device order and include supported Intel, AMD, and NVIDIA integrated or discrete GPUs.

SenseVoice stores `backend` as `cpu` or `vulkan`. Vulkan mode runs `asr.senseVoice.vulkanExecutablePath` and passes `vulkanDeviceIndex` to the resident worker; CPU mode runs `cpuExecutablePath`. An empty device index selects the first Vulkan adapter. The two executables are separate so CPU mode does not map the roughly 72 MiB embedded Vulkan shader runtime.

Fun-ASR-Nano uses the same `cpu` or `vulkan` backend selection. CPU mode retains the compatible `asr.funAsrNano.executablePath`; Vulkan mode runs `vulkanExecutablePath` and passes `vulkanDeviceIndex`. The Vulkan worker keeps the SAN-M encoder weights and all Qwen language-model layers on the selected adapter. CPU remains the default because Vulkan performance is device-specific and can be slower even when it reduces CPU load and process working set.

The Vulkan worker uploads model weights once and performs a short compute warm-up before reporting `READY`. Explicit **Use preset** activation therefore pays model upload and Vulkan pipeline initialization before the first PTT recognition instead of delaying the first submitted phrase.

Whisper.cpp stores `gpuDeviceIndex` and passes it through `--device` when `useGpu` is enabled. An empty index leaves selection to the runtime. GPU mode runs `asr.whisperCpp.vulkanServerExecutablePath`; CPU mode runs `asr.whisperCpp.serverExecutablePath`, so the CPU path does not load the large Vulkan backend. The older CLI executable fields remain readable for configuration compatibility but are not used by the provider.

The portable package includes SenseVoice and Fun-ASR-Nano CPU/static Vulkan workers plus whisper.cpp's CPU/Vulkan server builds and `ggml-vulkan.dll`. The Windows Vulkan loader and hardware driver are supplied by the target computer's graphics driver. Paraformer and the sherpa-onnx Qwen3-ASR provider remain CPU-only.

In automatic foreground routing mode, each profile's provider is created on its first recognition. Explicit **Use preset** activation creates the selected provider immediately so model-loading failures and warm-up cost occur before PTT use. Stopping the runtime releases all resident workers and servers.

## Microphone test

General > Audio includes a native microphone test. Starting it enumerates every active Windows capture endpoint and reports each endpoint's peak level every 100 ms. The WPF interface renders those values without opening or retaining audio files. The monitor stops when the button is pressed again or when the settings window closes.
