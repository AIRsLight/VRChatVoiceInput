# Development Plan

## Product scope

Build one Windows companion application that turns push-to-talk audio into text and routes it to the selected target. VRChat remains the first-class target through OSC, but the capture and ASR pipeline must also work with ordinary desktop applications and non-VR games.

The core flow is:

```text
PTT input -> audio capture -> VAD/finalize -> ASR provider -> text policy -> target output
```

Every stage is replaceable. No ASR provider may depend directly on SteamVR, and no input adapter may depend directly on VRChat.

## Input adapters

Planned PTT sources:

- Global keyboard press/release, including a configurable single key or key combination.
- Standard gamepads through XInput for Xbox-compatible devices.
- Extended gamepads through a later HID/SDL adapter for DualSense and other devices not fully exposed by XInput.
- SteamVR controller actions through an OpenVR background application and action manifest.

Hold-to-talk is the default behavior. Toggle and press-to-cancel modes can be added after the press/release state machine is stable.

## Output adapters

### VRChat

Use OSC `/chatbox/input`. This remains the preferred VRChat path because it does not require keyboard focus or synthetic key presses.

### Windows target application

Support two text injection strategies behind a `WindowsTextOutput` adapter:

- Clipboard plus paste shortcut for reliable Unicode input in normal text controls.
- Unicode `SendInput` as a fallback for applications where paste is unavailable.

The output adapter must support per-application profiles because games and desktop controls handle synthetic input differently.

Safety and focus rules:

- Capture the foreground window handle and process identity when PTT is pressed.
- After ASR completes, inject only if the captured window still exists and matches the configured target policy.
- Default to cancelling output when focus changed during recognition. A per-profile option may explicitly follow the current foreground window.
- Never inject into the companion application's own configuration window.
- Preserve and restore clipboard contents when clipboard injection is enabled, subject to Windows clipboard ownership races.
- Report Windows integrity-level restrictions when a normal process cannot inject into an elevated target.
- Do not attempt to bypass anti-cheat, protected-input, or accessibility restrictions. Unsupported applications should fail visibly and remain opt-in.

Target modes:

- `CapturedForeground`: window active when PTT begins; default for general use.
- `PinnedProcess`: any foreground window owned by a configured executable.
- `VrChatOsc`: bypass Windows injection and send through OSC.

## Delivery phases

### Phase 0: Core foundation

Status: implemented foundation.

- ASR and output interfaces.
- Persistent SenseVoice and Paraformer GGUF worker providers with cancellation recovery.
- CPU/Vulkan runtime configuration.
- VRChat OSC output and text chunking.
- CLI smoke tests.

### Phase 1: Audio and PTT state machine

Status: MVP and segmented streaming recognition implemented; pre-roll remains.

- WASAPI microphone capture with explicit input-device selection.
- Ring buffer and short pre-roll so the first syllable is not lost.
- Silero-VAD segmentation while recording, with configurable silence, speech, threshold, and maximum-segment settings. Implemented.
- Shared PTT session coordinator with cancellation and error recovery.
- Global keyboard hold-to-talk adapter.

Acceptance: hold a configured keyboard key, speak, release it, and obtain one finalized transcript without starting SteamVR.

### Phase 2: General Windows text input

Status: clipboard paste, Unicode `SendInput`, pure keyboard events, open/submit hotkeys, delays, and process-matched profiles implemented.

- Foreground-window capture and process inspection.
- Clipboard/paste, Unicode `SendInput`, and pure keyboard output strategies. Implemented.
- Focus-change protection and per-application profiles for PTT, provider, terminology, output, and submission settings.
- Dry-run preview that shows the selected target without injecting text.

Acceptance: use the same PTT workflow to enter multilingual text into Notepad, a browser text field, and at least one non-VR game chat field.

### Phase 3: Gamepad PTT

Status: XInput MVP implemented; binding UI and extended controller support remain.

- XInput button press/release adapter.
- Controller selection and reconnect handling.
- Configurable button/chord binding with conflict warnings.
- Evaluate SDL/HID support for PlayStation and generic controllers.

Acceptance: trigger the same desktop transcription session from a standard controller without SteamVR running.

### Phase 4: SteamVR and VRChat integration

Status: built-in VRChat OSC profile, streaming Chatbox updates, and OpenVR background action input implemented; broader physical-controller validation remains.

- OpenVR background application registration. Implemented.
- SteamVR action manifest and controller binding UI. Implemented.
- VRChat OSC connectivity test and Chatbox rate-limit handling.
- OSC typing state, per-segment rolling text, and final text update. Implemented.
- Optional policy to transcribe only while the VRChat microphone is muted.

Acceptance: controller PTT works in VRChat without an overlay or OpenVR driver and sends either finalized text or enabled per-segment updates through OSC.

### Phase 5: Provider and runtime expansion

Status: five local CPU providers implemented; cloud providers remain. See [MODEL_BENCHMARK.md](MODEL_BENCHMARK.md).

- Automatic CPU versus non-VR Vulkan adapter benchmark.
- Vulkan device enumeration for AMD, Intel, and NVIDIA adapters.
- Local Paraformer GGUF with optional sherpa-onnx punctuation, SenseVoice GGUF, Fun-ASR-Nano GGUF, Qwen3-ASR sherpa-onnx, and whisper.cpp providers.
- Persistent lifecycle for every local provider, with lazy loading during automatic routing and eager loading for explicit preset activation. Implemented.
- OpenAI-compatible cloud ASR provider with explicit privacy settings.
- Provider health checks, fallback, timeout, and cancellation behavior.
- Optional terminology-hint capability is implemented by Qwen3-ASR; other model-specific hotword and prompt implementations remain deferred.

### Phase 6: Desktop UI and packaging

Status: initial desktop UI and tray application implemented; self-contained packaging and runtime/model update management remain. See [UI_ARCHITECTURE.md](UI_ARCHITECTURE.md).

- Windows desktop configuration page and system tray lifecycle. Implemented.
- WPF native host with a WebView2 configuration surface. Implemented.
- Microphone, PTT binding, ASR provider, target, and per-application profile editors. Implemented.
- Self-contained Windows release without a separately installed .NET runtime.
- Runtime/model download verification and update policy.

## Initial non-VR compatibility target

The first compatibility matrix should include:

- Notepad as a plain Win32 text-control baseline.
- A Chromium browser text field as a desktop application baseline.
- A non-elevated game with a normal chat field.
- An elevated test application to verify that integrity-level failures are detected and explained.

Passing the desktop baselines is required before broad claims of support for arbitrary games or protected applications.
