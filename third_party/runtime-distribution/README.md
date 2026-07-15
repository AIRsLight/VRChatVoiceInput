# VRChat Voice Input native runtimes

This repository contains the minimal Windows x64 native ASR runtimes used by
VRChat Voice Input. Model weights are distributed separately. The application
downloads these files from an immutable Hugging Face revision and verifies the
size and SHA-256 of every file before installation.

The archive intentionally excludes development tools, test executables,
quantizers, Parakeet utilities, SDL, and command-line programs that the
application does not call.

## Sources

- Paraformer resident worker: FunASR commit
  `5990412a196518d511bff12584417195fb9c952b` plus the product worker and English
  post-processing patch.
- SenseVoice resident workers: SenseVoice commit
  `fbf91f3baccebec554dcee68708b0cda61d42805` plus the product worker patch.
- Fun-ASR-Nano resident workers: FunASR commit
  `5990412a196518d511bff12584417195fb9c952b` plus the product worker and Vulkan
  backend patch.
- Whisper CPU and Vulkan servers: whisper.cpp v1.9.1 commit
  `f049fff95a089aa9969deb009cdd4892b3e74916`.

The FunASR, SenseVoice, and whisper.cpp-derived files are provided under their
respective MIT licenses included in each runtime directory. Vulkan-enabled
runtimes require the Vulkan loader installed by the target machine's graphics
driver; `vulkan-1.dll` is not redistributed.
