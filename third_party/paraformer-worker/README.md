# Paraformer resident-worker patch

This directory contains the local patch used to build the resident Paraformer
runtime. The upstream source is not vendored.

- Upstream: `https://github.com/modelscope/FunASR.git`
- Commit: `5990412a196518d511bff12584417195fb9c952b`
- License: MIT
- Patch target: `runtime/llama.cpp/paraformer/funasr-paraformer/funasr-paraformer.cpp`

The patch adds two product-specific changes:

1. A `--worker` mode that keeps the GGUF model loaded and exchanges line-framed
   requests over stdin/stdout.
2. Paraformer English BPE post-processing compatible with FunASR's Python
   `sentence_postprocess`, including word spaces and abbreviation folding.

```text
READY
TRANSCRIBE<TAB>{base64 UTF-8 absolute WAV path}
RESULT<TAB>{base64 UTF-8 transcript}
ERROR<TAB>{base64 UTF-8 message}
QUIT
```

Use `build-paraformer-runtime.ps1` at the repository root to clone the pinned
source, apply the patch, compile the CPU runtime, and install the executable in
`runtimes/`.
