# Paraformer resident runtime

The packaged `llama-funasr-paraformer.exe` is built from FunASR commit
`5990412a196518d511bff12584417195fb9c952b` plus the tracked patch in
`third_party/paraformer-worker/`.

New portable packages include the Q5_0 model by default. The Models page can
switch between pinned Q8_0, Q5_0, and Q4_0 assets; all three use the same
runtime and embedded vocabulary format.

## Why the patch exists

The upstream C++ GGUF runtime concatenates all vocabulary pieces and removes
every `@@` marker. That preserves Chinese but removes English word boundaries.
FunASR's Python `sentence_postprocess` instead treats `@@` as a BPE
continuation, inserts spaces between completed ASCII words, and folds runs of
single-letter tokens into uppercase abbreviations.

The local patch implements the Python behavior without changing inference. A
30-second mixed-language regression sample produced identical token-ID hashes
before and after the patch:

```text
F162DEA9AFF6013506CF80363F0966BD65AA4AE9E096E1766E3865A5F11AA193
```

Representative output changed from:

```text
omivoiceplus ... asr ... pythoncoda
```

to:

```text
omi voice plus ... ASR ... python CODA
```

Misrecognitions such as `omi` remain model output and are not corrected by the
detokenizer.

## Resident protocol

`--worker` loads the Paraformer GGUF once, emits `READY`, and accepts Base64
UTF-8 file paths over stdin. Cancellation kills the worker; the next request
starts a fresh process. Normal disposal sends `QUIT` and waits for clean exit.

The C# `ResidentExternalAsrWorker` implements this lifecycle for both
Paraformer and SenseVoice, including stderr diagnostics and external-process
working-set metrics.

## Punctuation

The GGUF Paraformer runtime does not contain FunASR's separate `ct-punc`
model. When `usePunctuation` is enabled, the provider runs sherpa-onnx's
Chinese/English INT8 CT-Transformer after ASR. This model is optional and CPU
only. The Models page downloads the fixed official release archive, verifies
its SHA-256, extracts `model.int8.onnx`, and verifies the extracted model again.

## Rebuild

```powershell
.\build-paraformer-runtime.ps1
```

The script checks out the pinned FunASR commit, applies the tracked patch,
builds a static AVX2 Windows executable, and installs it under `runtimes/`.
