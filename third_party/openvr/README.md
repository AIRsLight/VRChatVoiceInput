# OpenVR dependency

This directory vendors the official Valve OpenVR C# bindings. The Windows x64
client library is intentionally excluded from Git and fetched at build time.

- Source: https://github.com/ValveSoftware/openvr
- Source commit: `0924064316de3effbcd1acf1e309182a2deb1c05`
- Source file: `headers/openvr_api.cs`
- Native build dependency: `bin/win64/openvr_api.dll`
- License: see `LICENSE` (BSD 3-Clause)

The generated C# binding is compiled by `VRChatVoiceInput.OpenVR`. Run
`fetch-openvr.ps1` to download the native library from the pinned commit and
verify its size and SHA-256. The library is copied next to the application
executable and is only loaded when SteamVR status or action input is used.
