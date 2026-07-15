# SenseVoice resident-worker patch

This directory contains the local patch used to build the resident SenseVoice
runtime. The upstream source is not vendored.

- Upstream: `https://github.com/FunAudioLLM/SenseVoice.git`
- Commit: `fbf91f3baccebec554dcee68708b0cda61d42805`
- License: MIT
- Patch target: `runtime/llama.cpp/funasr-sensevoice/funasr-sensevoice.cpp`

The patch adds a `--worker` mode. The process loads the GGUF model once and
then exchanges line-framed requests over stdin/stdout:

```text
READY
TRANSCRIBE<TAB>{base64 UTF-8 absolute WAV path}
RESULT<TAB>{base64 UTF-8 transcript}
ERROR<TAB>{base64 UTF-8 message}
QUIT
```

Use `build-sensevoice-runtime.ps1` at the repository root to clone the pinned
source, apply the patch, compile the CPU runtime, and install the executable in
`runtimes/`.
