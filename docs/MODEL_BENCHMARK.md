# Local ASR Model Benchmark

## Purpose

This is an early provider-screening benchmark for the local CPU path. It is not a formal accuracy evaluation and does not report WER/CER because the source clip has no manually verified reference transcript.

The benchmark answers three product questions:

- Which provider returns a short PTT utterance quickly enough?
- Which provider preserves Chinese technical terms and mixed English best?
- What model size and memory cost must the background application carry?

## Environment

- Date: 2026-07-14
- CPU: Intel Core i7-14700KF, 20 cores / 28 logical processors
- Acceleration: CPU only; no CUDA, Vulkan, or OpenVINO backend
- Input: `omnivoice-6a44ec8ff83f.wav`, 77.14 seconds, 24 kHz mono PCM16
- Normalized input: FFmpeg conversion to 16 kHz mono PCM16
- Short input: first 10 seconds of the same normalized clip
- Runtime behavior: cold process start and model load for every request

The clip is Mandarin speech with many English technical terms, including names such as Python, CUDA Toolkit, PyTorch, NVIDIA, Transformers, vLLM, TensorRT, ONNX, FP16, INT8, Studio, and EXE.

## Resident CPU character throughput

The model UI performance hint uses the first eight seconds of the normalized mixed Mandarin/English clip. Each provider is warmed once, then run three times while resident. The displayed value is the arithmetic mean of recognized Unicode scalar values excluding whitespace divided by recognition time. This is a practical text-output throughput hint, not an accuracy score: mistranscriptions and different output lengths can change it.

| Provider and version | Mean characters/s |
|---|---:|
| Paraformer Q8_0 | 148 |
| Paraformer Q5_0 | 124 |
| Paraformer Q4_0 | 172 |
| SenseVoice Q8_0 | 135 |
| SenseVoice Q5_0 | 120 |
| Fun-ASR-Nano Q8_0 encoder + Q4_K_M language model | 64 |
| Fun-ASR-Nano F16 encoder + Q4_K_M language model | 51 |
| Qwen3-ASR INT8 | 28 |
| Whisper Small Q5_1 | 12 |

These measurements were taken on the i7-14700KF CPU in the environment above. They are rounded UI guidance, and should not be compared across machines as fixed model specifications.

## Results

| Provider | Weight files | 77 s wall time | 10 s wall time | Peak working set | Observed result |
|---|---:|---:|---:|---:|---|
| Paraformer Q8 | 226 MiB | 2.58 s | 0.37 s | 270 MiB | Best speed/resource balance; good Mandarin, frequent proper-name errors |
| SenseVoiceSmall Q8 | 242 MiB | 2.67 s | 0.41 s | 349 MiB | Fast, but technical code-switching was weaker and one long run hallucinated Japanese |
| Fun-ASR-Nano encoder F16 + Q4_K_M LLM | 909 MiB | 6.19 s | 1.39 s | 1,336 MiB | Best overall text and punctuation; retained the most technical terminology |
| Whisper Base Q5_1 | 57 MiB | 5.02 s | 1.52 s | 322 MiB | Smallest download, but weakest Mandarin result on this clip |
| Whisper Small Q5_1 with terminology prompt | 181 MiB | 14.14 s | 3.20 s | 589 MiB | Prompt preserved more English terms, but Mandarin homophone errors remained high |

A Q8_0 Nano language-model variant was also tested. Its total weights were about 1,215 MiB, the 77-second run took 8.04 seconds, and peak working set was 1,366 MiB. It did not improve this sample over Q4_K_M. The Q8 language model with FSMN-VAD took 9.34 seconds and introduced additional segmentation errors, so Q4_K_M with 15-second chunking is the better Nano language-model configuration for the current sample.

## Qualitative comparison

Fun-ASR-Nano produced the most coherent sentences and retained the largest number of technical names. It still misrecognized several names, so it is not accurate enough to establish ground truth by itself.

Paraformer was substantially lighter and faster. It recognized ordinary Mandarin well and retained some terms such as Python, CUDA Toolkit, Torch, Transformers, ONNX, FP16, and TensorRT, but distorted product names and some surrounding Chinese words.

SenseVoiceSmall matched Paraformer's speed but was less reliable on this technical, code-switched sample. Its emotion and sound-event features may still justify it for other product modes, but those features do not help basic text entry.

Whisper Small benefited from an initial terminology prompt for English names. It remained much slower and produced more Mandarin homophone errors than Paraformer or Nano on this sample. Its value is broad multilingual coverage, which must be evaluated on non-Chinese clips before making a final decision.

## Qwen3-ASR sherpa-onnx screening

Qwen3-ASR 0.6B INT8 was tested separately after the original comparison. Its model files total about 941 MiB. Creating the CPU recognizer took about 2.4 seconds. Once loaded, an 8-second clip took 2.00 seconds with one thread, 1.57 seconds with two threads, and 1.29 seconds with four threads; eight threads did not improve on four. The in-application C# provider decoded the same 8-second clip in 1.46 seconds with four threads.

The unsegmented 77-second source exceeded the exported model's 512-token context and returned an invalid result. Running the same file through Silero VAD took 16.60 seconds of decoding time at RTF 0.215 and peaked near 2.2 GiB working set. Short PTT recordings do not hit this limit, but long-file support requires explicit segmentation.

The integrated streaming probe fed the same real clip twice with a one-second silent gap. Silero VAD produced four completed speech segments, Qwen3-ASR returned four accumulated updates, and the full session completed in 6.6 seconds. A separate fake-provider probe confirmed that OSC receives each update while a captured-window target receives exactly one joined final submission.

Without terminology hints, technical names such as Qwen3-ASR and OmniVoice were distorted. Passing the profile hotwords corrected both names in the test clips, although unrelated Mandarin errors remained. This makes Qwen3-ASR the first current provider where the saved per-profile hotword list produces a material result.

## Qwen3-ASR community ONNX INT4 screening

The community `andrewleech/qwen3-asr-0.6b-onnx` INT4 export was tested to determine whether it could replace the sherpa-onnx INT8 package as a lower-memory option. It cannot: this is a decoder-only MatMulNBits quantization, not a full-model Q4 export. The audio encoder remains FP32, token embeddings remain FP16, and the autoregressive decoder is split into separate initialization and step ONNX Runtime sessions.

| Configuration | Model files | Loaded working set | 8 s second call | 15 s second call | 30 s second call | Final peak working set |
|---|---:|---:|---:|---:|---:|---:|
| sherpa-onnx INT8, 16 threads | 941 MiB | 1.07 GiB | 1.64 s | 2.89 s | 6.01 s | 2.82 GiB |
| Community INT4, 16 threads | 1.90 GiB | 1.80 GiB | 1.78 s | 2.67 s | 6.27 s | 3.83 GiB |
| Community INT4, 16 threads, CPU arena disabled | 1.90 GiB | 1.80 GiB | 2.14 s | 3.85 s | 12.68 s | 2.32 GiB |

The community package is larger on disk because its 711 MiB FP32 encoder and 297 MiB FP16 embedding table offset the decoder weight reduction. Stage-by-stage process measurements showed that loading the FP32 encoder added about 725 MiB of resident memory. Loading `decoder_init` and `decoder_step` then added about 410 MiB each because ONNX Runtime creates session-local optimized and prepacked representations. Mapping the FP16 embedding table added almost no immediate working set because it is memory-mapped and loaded on demand.

The Python test harness imports Torch for feature extraction and contributes about 160 MiB of resident overhead, so its absolute working set is not directly comparable with the production C# provider. That overhead does not explain the result: the model-session deltas independently identify the FP32 encoder and duplicated decoder sessions as the dominant fixed costs.

The remaining peak growth comes largely from ONNX Runtime's CPU memory arena retaining temporary attention and key/value-cache buffers for reuse. Disabling the arena removed the cumulative growth, but approximately doubled 30-second latency. Quantization does not reduce these activation buffers, and MatMulNBits also requires scales, zero points, and kernel-specific prepacked data.

The INT4 output matched the INT8 output on the 8-second clip after removing its control-token prefix, but differed by 19.8% and 23.2% character edit distance on the 15- and 30-second clips. These are output-drift measurements without a labeled reference and therefore do not establish which result is more accurate.

Do not expose this package as a lower-memory Qwen3-ASR version. A useful Q4 candidate would need at least an INT8/INT4 audio encoder, shared or fused decoder state, reusable I/O buffers, and a benchmark against the same runtime graph. A native sherpa-onnx INT4 export would be preferable because it would permit a controlled comparison with the current INT8 provider.

## Current recommendation

The dedicated [SenseVoice CPU and CUDA benchmark](SENSEVOICE_GPU_BENCHMARK.md) found no CUDA latency advantage on an i7-14700KF and RTX 3060. CPU therefore remains the default. A separate Vulkan worker is available for assigning inference to an idle integrated GPU under VR load; this is a resource-routing option and does not imply that Vulkan is faster on every adapter.

The product Paraformer runtime is now persistent as well. On a 30-second mixed Mandarin/English clip, the patched and upstream runtimes produced identical token IDs. Correct Python-compatible BPE post-processing changed concatenated text such as `omivoiceplus`, `asr`, and `pythoncoda` into spaced or folded forms such as `omi voice plus`, `ASR`, and `python CODA`; remaining name errors are model errors. The optional 72 MiB INT8 CT-Transformer punctuation model loaded in about 292 ms and processed this transcript in about 16 ms on the benchmark machine.

Direct FP32 exports produced experimental Q5_0 and Q4_0 Paraformer models rather than requantizing the existing Q8 file. On a 30-second clip, Q8_0, Q5_0, and Q4_0 peaked at 252.8 MiB, 176.5 MiB, and 151.1 MiB working set; their model files are 226.0 MiB, 149.7 MiB, and 124.3 MiB. Across 59 clips, Q5_0 and Q4_0 each matched Q8 token output exactly on 18 clips, with aggregate token edit distances of 8.8% and 10.1%. This measures output drift from Q8, not labeled WER. Q5_0 is now the package default for its 76 MiB resident-memory saving, while the Models page retains Q8_0 and Q4_0 choices.

Fun-ASR-Nano now uses a resident worker as well. On an 8-second mixed Mandarin/English clip, the old one-shot runtime took about 2.1 seconds including model load; two resident calls took about 0.77 and 0.73 seconds and returned the same text. Its resident working set was about 1.28 GiB. Cancellation terminates the worker, and the next request reloads it before recognition.

The later Vulkan worker test used the Q8_0 encoder, Q4_K_M language model, and the full 77.14-second source clip on an RTX 3060. CPU recognition took 7.08 seconds; Vulkan recognition took 19.09 seconds. UIA cold activation measured about 1.98 seconds and 1.05 GiB for CPU versus 2.18 seconds and 775 MiB for Vulkan. The Vulkan runtime reserved about 1.3 GiB of compute buffers in addition to its model and KV buffers, putting GPU or shared-memory demand near 2 GiB. Vulkan therefore remains an explicit resource-routing option for reducing CPU pressure, not the default or a guaranteed latency improvement. The selected adapter receives both the SAN-M encoder and every Qwen layer.

Additional encoder quantization tests used the same Q4_K_M Nano language model for both variants. Replacing the 447.6 MiB F16 encoder with the 241.8 MiB upstream Q8_0 export reduced peak working set from 1337.5 MiB to 1131.5 MiB. Across 59 clips, 47 final transcripts were identical and aggregate character edit distance was 2.2%; mean recognition time changed from 571 ms to 590 ms. The Q8_0 encoder is now the Nano default, while F16 remains selectable.

SenseVoice Q5_0 and Q4_0 were exported directly from the official FP32 checkpoint. Q5_0 reduced the model from 242.4 MiB to 159.4 MiB and peak working set from 358.8 MiB to 275.8 MiB, but its token edit distance from Q8 was 9.2% across 59 clips. Q4_0 reached 22.3% token drift. SenseVoice therefore keeps Q8_0 as its default, exposes Q5_0 as an optional low-memory version, and does not expose Q4_0 in the application.

Whisper.cpp now uses the official resident `whisper-server`. On the same 8-second clip, two CPU Small Q5_1 calls took about 2.94 and 2.87 seconds with a roughly 364 MiB resident working set. A canceled request stopped the server, the next request recovered successfully, and no server process remained after provider disposal. Keeping timestamp output enabled preserves boundaries used to restore spaces between English segments.

- Default local provider for Mandarin-first users: Paraformer Q5_0; select Q8_0 when maximum fidelity matters more than roughly 76 MiB of resident memory.
- Higher-quality local option when about 1.1 GiB working memory is acceptable: Fun-ASR-Nano Q8_0 encoder with Q4_K_M language model; switch the encoder to F16 when maximum fidelity matters more than roughly 206 MiB of memory.
- Higher-quality multilingual and code-switching option when 2.2 GiB or more is acceptable: Qwen3-ASR 0.6B INT8, loaded persistently with four CPU threads.
- Multilingual fallback candidate: whisper.cpp Small Q5_1 with per-profile terminology prompts.
- Keep SenseVoice as an optional provider for emotion/event-aware modes, not the current default text provider.

Do not implement automatic provider selection from this single clip. The next benchmark corpus must contain manually verified short PTT samples for ordinary Mandarin, Mandarin/English code-switching, English, Japanese, Korean, noisy VR microphone audio, and silence/non-speech rejection.

## Architecture implications

- Provider processes should stay warm where possible. Cold-start measurements include repeated model loading and overstate steady-state PTT latency.
- The provider contract needs optional language, terminology prompt/hotword, segmentation, thread-count, and warm-process settings.
- Benchmark output must store model/runtime versions, hashes, CPU/GPU backend, wall time, peak memory, transcript, and reference-text score when a reference exists.
- Nano should not be loaded alongside a VR game by default without a memory-headroom check.
- Model files and native runtimes remain separately downloadable and are not committed to Git.
