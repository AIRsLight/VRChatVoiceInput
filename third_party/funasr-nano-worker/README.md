# Fun-ASR-Nano resident-worker patch

This directory contains the local patch used to build the resident
Fun-ASR-Nano runtime. The upstream source is not vendored.

- Upstream: `https://github.com/modelscope/FunASR.git`
- Commit: `5990412a196518d511bff12584417195fb9c952b`
- License: MIT
- Patch target: `runtime/llama.cpp/fun-asr-nano/funasr-cli/funasr-cli.cpp`

The patch adds a `--worker` mode that keeps the encoder, Qwen language model,
context, and sampler loaded. It exchanges line-framed requests over
stdin/stdout while retaining the upstream one-shot CLI mode. `--backend
vulkan` uploads the encoder and all Qwen layers to a Vulkan device selected by
`--device`; CPU remains the default.

```text
READY
TRANSCRIBE<TAB>{base64 UTF-8 absolute WAV path}
RESULT<TAB>{base64 UTF-8 transcript}
ERROR<TAB>{base64 UTF-8 message}
QUIT
```

Use `build-funasr-nano-runtime.ps1` at the repository root to clone the pinned
source, apply the patch, and compile both runtimes. The CPU executable is
installed in `runtimes/`; the static Vulkan executable is installed in
`runtimes/funasr-nano-vulkan/`.
