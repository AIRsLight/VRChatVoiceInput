# SenseVoice CPU and CUDA Benchmark

## Decision

GPU acceleration is not advantageous for the tested SenseVoice.cpp backend on this machine. The RTX 3060 was slower than the i7-14700KF in every tested Q8 and FP16 case, including cold process startup, persistent inference, and simulated streaming.

For lowest standalone latency on this machine, keep SenseVoice on CPU and prefer a persistent provider process. For long speech, 10-15 second segments are a practical starting point. Do not add a CUDA build to the packaged runtime based on these results.

This conclusion applies to this specific SenseVoice.cpp implementation and NVIDIA CUDA system. It does not establish the performance of Vulkan, DirectML, OpenVINO, AMD, or Intel iGPU implementations.

The product now includes a separate Vulkan build of the official FunASR GGUF worker for resource routing rather than as a universal speed optimization. It allows inference to be assigned to an otherwise idle integrated GPU when VR saturates the CPU and discrete GPU. The original CPU recommendation remains the default, and Intel/AMD integrated adapters require separate measurements on their target hardware.

## Official GGUF Vulkan Worker Smoke Test

The Vulkan implementation added to the pinned official FunASR GGUF worker uses the same Q8 model as the CPU runtime. On the 8-second mixed-language clip, three resident CPU calls took 0.26, 0.21, and 0.24 seconds. A Vulkan worker on the RTX 3060 initially spent 2.57 seconds creating driver pipelines, then took 0.06 and 0.06 seconds. The worker now performs that Vulkan warm-up before reporting `READY`; after warm-up, two application-facing calls took 0.05 and 0.05 seconds.

This is a functional smoke test rather than cross-device performance guidance. Integrated Intel and AMD GPUs may behave differently, especially when sharing memory bandwidth with the CPU. The UI therefore reports Vulkan memory and throughput as unmeasured until that adapter has a dedicated benchmark.

## Environment

- Date: 2026-07-15
- OS: Windows 11 Pro, build 26200
- CPU: Intel Core i7-14700KF, 20 cores / 28 logical processors
- GPU: NVIDIA GeForce RTX 3060, driver 596.49
- CUDA toolkit: 13.2
- Runtime: `lovemefan/SenseVoice.cpp`, commit `6503f51c2357034e1443c86dabeb24ad026c4b45`
- CPU build: standard GGML CPU backend
- GPU build: `GGML_CUDA=ON`, CUDA architecture 86, CUDA flash attention disabled
- Threads: 4 for both backends
- Input: the first 1-77 seconds of `omnivoice-6a44ec8ff83f.wav`, normalized to 16 kHz mono PCM16
- Repetitions: three per case; tables report arithmetic mean wall time

Models:

| Model | Size | SHA-256 |
|---|---:|---|
| SenseVoiceSmall Q8_0 | 291,985,952 bytes | `F92BEB119D07E42A96E3FBE6FBBB172910026F26B724C2B10FD75654C23D6912` |
| SenseVoiceSmall FP16 | 470,292,448 bytes | `C49CF06C38AF3D679D4D0CE6FECE0C74CABFC911C521A5B9C25469595F9D0BE5` |

The GPU build reported CUDA0 allocation and did not fall back to CPU. CPU and CUDA returned the same text in an FP16 spot check. This benchmark evaluates latency, not recognition accuracy.

## Method

Two runtime lifecycles were measured:

- **Cold:** start a new process and load the model for every file or stream segment. This matched the provider behavior before the resident worker was implemented.
- **Persistent:** load one model process and process multiple files. Per-file non-streaming time is the delta between one file and four additional copies; streaming time is the wall time for all segments in one process.

Streaming is simulated by splitting 60 seconds of audio into fixed chunks. It measures incremental chunk processing and does not imply that the model has a transducer-style streaming decoder.

Wall-clock time is used for conclusions. The runtime's decoder timer resets around internally segmented long audio and is not reliable as an end-to-end measurement.

## Q8 Non-streaming

| Audio | Cold CPU | Cold CUDA | CUDA / CPU | Persistent CPU | Persistent CUDA | CUDA / CPU |
|---:|---:|---:|---:|---:|---:|---:|
| 1 s | 0.249 s | 0.467 s | 1.88x | n/a | n/a | n/a |
| 2 s | 0.279 s | 0.808 s | 2.89x | 0.050 s | 0.285 s | 5.75x |
| 4 s | 0.311 s | 0.788 s | 2.53x | 0.053 s | 0.326 s | 6.15x |
| 8 s | 0.321 s | 0.885 s | 2.76x | 0.066 s | 0.374 s | 5.66x |
| 15 s | 0.302 s | 1.089 s | 3.61x | 0.094 s | 0.466 s | 4.97x |
| 30 s | 0.412 s | 1.434 s | 3.48x | 0.169 s | 0.957 s | 5.65x |
| 60 s | 0.480 s | 1.866 s | 3.89x | 0.244 s | 1.332 s | 5.45x |
| 77 s | 0.515 s | 2.111 s | 4.10x | 0.290 s | 1.599 s | 5.51x |

The one-second persistent delta is omitted because it is below the measurement resolution and produced a negative CPU delta after subtraction.

## FP16 Non-streaming

| Audio | Cold CPU | Cold CUDA | CUDA / CPU | Persistent CPU | Persistent CUDA | CUDA / CPU |
|---:|---:|---:|---:|---:|---:|---:|
| 2 s | 0.403 s | 0.893 s | 2.22x | 0.064 s | 0.283 s | 4.41x |
| 8 s | 0.413 s | 0.961 s | 2.33x | 0.083 s | 0.371 s | 4.46x |
| 15 s | 0.432 s | 1.055 s | 2.44x | 0.097 s | 0.482 s | 4.95x |
| 30 s | 0.534 s | 1.514 s | 2.84x | 0.208 s | 0.943 s | 4.53x |
| 60 s | 0.615 s | 1.973 s | 3.21x | 0.273 s | 1.382 s | 5.05x |
| 77 s | 0.667 s | 2.208 s | 3.31x | 0.313 s | 1.590 s | 5.07x |

FP16 does not reverse the Q8 result. It narrows some cold-start ratios because CPU model loading is more expensive, but CUDA remains slower after the model is resident.

## Streaming, 60 Seconds Total

Q8:

| Chunk | Segments | Cold CPU | Cold CUDA | CUDA / CPU | Persistent CPU | Persistent CUDA | CUDA / CPU |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 2 s | 30 | 8.228 s | 22.051 s | 2.68x | 1.770 s | 8.181 s | 4.62x |
| 5 s | 12 | 3.864 s | 10.119 s | 2.62x | 1.509 s | 5.108 s | 3.38x |
| 10 s | 6 | 2.455 s | 8.331 s | 3.39x | 1.321 s | 5.005 s | 3.79x |
| 15 s | 4 | 1.670 s | 4.227 s | 2.53x | 1.045 s | 2.784 s | 2.66x |

FP16:

| Chunk | Segments | Cold CPU | Cold CUDA | CUDA / CPU | Persistent CPU | Persistent CUDA | CUDA / CPU |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 5 s | 12 | 5.336 s | 10.994 s | 2.06x | 1.943 s | 4.877 s | 2.51x |
| 15 s | 4 | 2.363 s | 4.686 s | 1.98x | 1.400 s | 3.077 s | 2.20x |

The 15-second chunk is fastest among the tested streaming choices. Smaller chunks reduce the delay before a partial result but increase total work, especially when each chunk starts a process and reloads the model.

## Memory

Persistent Q8 host working set ranged from about 304-334 MiB on CPU and 371-493 MiB with CUDA. Persistent FP16 host working set ranged from about 489-503 MiB on CPU and 473-484 MiB with CUDA.

GPU memory was sampled with `nvidia-smi` while one CUDA process handled five 77-second inputs:

| Model | Baseline GPU memory | Peak GPU memory | Observed increase |
|---|---:|---:|---:|
| Q8_0 | 7,519 MiB | 7,942 MiB | 423 MiB |
| FP16 | 7,494 MiB | 8,079 MiB | 585 MiB |

The GPU figures are whole-device deltas under Windows WDDM, because per-process memory was unavailable. They are approximate and include any unrelated activity during the sample window.

## Product Implications

- Keep CPU as the default. The CUDA-capable SenseVoice.cpp fork uses an incompatible model format, while the separately implemented Vulkan worker retains the application's existing official GGUF files.
- The resident worker is now implemented; keep it as the default SenseVoice lifecycle before pursuing hardware acceleration.
- Use 10-15 second chunks for long-form input, with a shorter partial-result cadence only when UI responsiveness is more important than total compute cost.
- A VR game already competing for the discrete GPU makes the slower CUDA path even less attractive.
- Benchmark Vulkan separately on actual AMD and Intel integrated GPUs before publishing performance guidance. These CUDA results cannot be used as measurements for those architectures.
