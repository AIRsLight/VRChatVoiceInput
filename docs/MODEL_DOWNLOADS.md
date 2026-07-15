# Model and runtime downloads

The Models page can install or verify model files and native ASR runtimes without Python or the Hugging Face CLI. Downloads use source or project Hugging Face repositories pinned to immutable commit revisions, or fixed official GitHub release assets. Every installed file is checked against its expected size and SHA-256 before an atomic replacement of the configured target path.

| Provider | Repository | Pinned revision | Files |
| --- | --- | --- | --- |
| Paraformer Q5_0/Q4_0 | `AIRsLight/paraformer-zh-GGUF` | `63bbcf93be5ddb3d6ccf9fa6865a16df82663e0a` | `paraformer-q5_0.gguf`, `paraformer-q4_0.gguf` |
| Paraformer Q8_0 | `FunAudioLLM/Paraformer-GGUF` | `de2cbaaa0f30b34f398d7a066fdfefb8e50d902c` | `paraformer-q8.gguf` |
| Paraformer punctuation | `AIRsLight/paraformer-zh-GGUF` | `5e1cbe68a235f6082a1868d50530e0a308cd1fd9` | `paraformer-punctuation-int8.onnx`, extracted unchanged from the official sherpa-onnx INT8 release archive |
| SenseVoice Q5_0 | `AIRsLight/SenseVoiceSmall-GGUF` | `d6bfce12fb0369874d357e9ebeed012e6349fd25` | `sensevoice-small-q5_0.gguf` |
| SenseVoice Q8_0 | `FunAudioLLM/SenseVoiceSmall-GGUF` | `90c1c61912018b70ada0fcc024ea24aca62f2e63` | `sensevoice-small-q8.gguf` |
| Fun-ASR-Nano Q8_0 encoder | `AIRsLight/Fun-ASR-Nano-2512-GGUF` | `69680848e36e28090e7532842bfa9bb5c1ebde61` | `funasr-encoder-q8_0.gguf` |
| Fun-ASR-Nano F16 encoder and Q4_K_M/Q5_K_M/Q8_0 LLM | `FunAudioLLM/Fun-ASR-Nano-GGUF` | `c1629cbf83548ea0d92077c09d3541ce407ee643` | `funasr-encoder-f16.gguf`, `qwen3-0.6b-q4km.gguf`, `qwen3-0.6b-q5km.gguf`, `qwen3-0.6b-q8_0.gguf` |
| Qwen3-ASR | `csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25` | `68818b2313fe77bd06f6a7c5068ff3ef59d02b8a` | `conv_frontend.onnx`, `encoder.int8.onnx`, `decoder.int8.onnx`, tokenizer files |
| FSMN-VAD | `FunAudioLLM/fsmn-vad-GGUF` | `6840bae4c5c92ee8c04faaf4db23dd0105098d7f` | `fsmn-vad.gguf` |
| Whisper.cpp | `ggerganov/whisper.cpp` | `5359861c739e955e79d9a303bcbc70fb988958b1` | `ggml-small-q5_1.bin` |
| Silero VAD | `k2-fsa/sherpa-onnx` release `asr-models` | release asset | `silero_vad.onnx` |
| Windows x64 native runtimes | `AIRsLight/VRChatVoiceInput-Runtimes` | `2a188dfe4289b3a37d374dbd2352f0fa9ac5f5d0` | Minimal Paraformer CPU, SenseVoice CPU/Vulkan, Fun-ASR-Nano CPU/Vulkan, and Whisper CPU/Vulkan runtime sets |

The immutable URL format is:

```text
https://huggingface.co/{repository}/resolve/{revision}/{file}?download=true
```

Interrupted transfers remain as `*.download` files and resume on the next attempt. A canceled or failed transfer never replaces a working model. Updating a model intentionally requires updating the pinned revision, size, and SHA-256 together in `ModelDownloadCatalog`.

The shared Silero VAD is downloaded from the official sherpa-onnx GitHub release URL. The optional Paraformer punctuation ONNX is mirrored unchanged from the official sherpa-onnx INT8 release archive because Windows `tar.exe` cannot reliably extract `.tar.bz2` without an external `bzip2.exe`. The mirrored ONNX retains the verified official extracted-file size and SHA-256, so clients download it directly without an extraction dependency.

Native runtimes are presented as independent CPU and Vulkan capabilities. A runtime package can contain multiple files, such as the Whisper server and its backend DLLs; the package is marked installed only when every loadable file has the expected size. Runtime licenses are retained in the runtime repository and are included independently in portable application builds. The Qwen3-ASR sherpa-onnx native library remains part of the self-contained application publish and does not need a separate runtime package.

An existing runtime is considered installed when its required executable and DLL files are present. If those files do not match the pinned release sizes, the Models page shows `Update available` instead of incorrectly reporting the runtime as missing. Updating a native runtime automatically stops the active profile host so Windows releases executable locks, performs the verified atomic replacement, and restores the previous running state.

The default portable build includes only the native runtimes required by enabled profiles and their currently selected CPU/Vulkan backends. Other runtimes remain available from the Models page. `build-portable.ps1 -RuntimeSet all` creates a fully offline test package with all seven supported runtime sets, while still excluding unrelated upstream tools, tests, quantizers, SDL, and Parakeet binaries.

The Models page presents the standard quantizations in a version selector. Selecting an installed version activates it immediately. Selecting a missing version keeps the current working configuration active, updates the displayed size and memory guidance, and offers a Download and use action; the configured path changes only after the download and SHA-256 verification succeed. Q5_0 is the default portable-package Paraformer model, Q8_0 prioritizes fidelity, and Q4_0 is the lowest-memory experimental option.

SenseVoice keeps Q8_0 as its quality default and exposes Q5_0 as an optional low-memory model. Fun-ASR-Nano exposes Q4_K_M, Q5_K_M, and Q8_0 in the primary language-model selector, while its encoder component independently exposes Q8_0 and F16. Components with only one catalog version remain plain text instead of showing a redundant selector.

Optional capabilities are listed separately from quantization. They include Paraformer's INT8 punctuation model, the FSMN VAD used by the GGUF providers, and the shared Silero streaming VAD. Each capability has its own availability state and install action. Shared files appear on each relevant provider page but use one canonical target path, so they are downloaded only once.

Only manually entered values such as executable/model paths, thread counts, chunk sizes, output-token limits, and streaming thresholds are kept under Advanced model configuration. Custom paths are never overwritten before a selected standard model finishes downloading and verifying.
