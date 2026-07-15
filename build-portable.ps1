[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",

    [ValidateSet("none", "current", "configured", "all")]
    [string]$ModelSet = "current",

    [ValidateSet("none", "current", "all")]
    [string]$RuntimeSet = "current",

    [string]$ConfigurationPath,

    [string]$OutputDirectory,

    [switch]$SkipArchive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$appProject = Join-Path $repoRoot "src\VRChatVoiceInput.App\VRChatVoiceInput.App.csproj"
$webUiDirectory = Join-Path $repoRoot "src\VRChatVoiceInput.App\WebUI"
$configPath = if (![string]::IsNullOrWhiteSpace($ConfigurationPath)) {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ConfigurationPath))
}
elseif (Test-Path -LiteralPath (Join-Path $repoRoot "appsettings.json") -PathType Leaf) {
    Join-Path $repoRoot "appsettings.json"
}
else {
    Join-Path $repoRoot "appsettings.example.json"
}
$modelsDirectory = Join-Path $repoRoot "models"
$runtimesDirectory = Join-Path $repoRoot "runtimes"
$openVrLicensePath = Join-Path $repoRoot "third_party\openvr\LICENSE"
$senseVoiceLicensePath = Join-Path $repoRoot "third_party\sensevoice-worker\LICENSE"
$paraformerLicensePath = Join-Path $repoRoot "third_party\paraformer-worker\LICENSE"
$funAsrNanoLicensePath = Join-Path $repoRoot "third_party\funasr-nano-worker\LICENSE"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\portable"
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$packageName = "VRChatVoiceInput-$RuntimeIdentifier"
$packageDirectory = Join-Path $outputRoot $packageName
$archivePath = "$packageDirectory.zip"

function Assert-PathInsideOutputRoot {
    param([Parameter(Mandatory)][string]$Path)

    $root = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath($Path)
    if (!$candidate.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the package output root: $candidate"
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (!(Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required directory was not found: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force |
        Copy-Item -Destination $Destination -Recurse -Force
}

function Get-ConfiguredModelPaths {
    param([Parameter(Mandatory)]$Configuration)

    $paths = @(
        $Configuration.asr.senseVoice.modelPath
        $Configuration.asr.senseVoice.vadModelPath
        $Configuration.asr.paraformer.modelPath
        $Configuration.asr.paraformer.vadModelPath
        $Configuration.asr.funAsrNano.encoderModelPath
        $Configuration.asr.funAsrNano.languageModelPath
        $Configuration.asr.funAsrNano.vadModelPath
        $Configuration.asr.whisperCpp.modelPath
        $Configuration.asr.whisperCpp.vadModelPath
    )
    $qwenProperty = $Configuration.asr.PSObject.Properties["qwen3Asr"]
    if ($null -ne $qwenProperty) {
        $paths += @(
            $qwenProperty.Value.convFrontendPath
            $qwenProperty.Value.encoderPath
            $qwenProperty.Value.decoderPath
            Join-Path $qwenProperty.Value.tokenizerPath "merges.txt"
            Join-Path $qwenProperty.Value.tokenizerPath "tokenizer_config.json"
            Join-Path $qwenProperty.Value.tokenizerPath "vocab.json"
        )
    }
    $streamingProperty = $Configuration.asr.PSObject.Properties["streaming"]
    if ($null -ne $streamingProperty) {
        $paths += $streamingProperty.Value.sileroVadModelPath
    }
    $paraformerPunctuationProperty = $Configuration.asr.paraformer.PSObject.Properties["usePunctuation"]
    if ($null -ne $paraformerPunctuationProperty -and $paraformerPunctuationProperty.Value) {
        $paths += $Configuration.asr.paraformer.punctuationModelPath
    }

    $paths | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
}

function Get-CurrentModelPaths {
    param([Parameter(Mandatory)]$Configuration)

    $providerIds = @(
        $Configuration.asr.provider
        $Configuration.profiles.items |
            Where-Object { $_.enabled } |
            ForEach-Object { $_.recognition.provider }
    ) | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique

    $paths = foreach ($providerId in $providerIds) {
        switch ($providerId) {
            "paraformer-gguf" {
                $Configuration.asr.paraformer.modelPath
                $Configuration.asr.paraformer.vadModelPath
                $punctuationProperty = $Configuration.asr.paraformer.PSObject.Properties["usePunctuation"]
                if ($null -ne $punctuationProperty -and $punctuationProperty.Value) {
                    $Configuration.asr.paraformer.punctuationModelPath
                }
            }
            "sensevoice-gguf" {
                $Configuration.asr.senseVoice.modelPath
                $Configuration.asr.senseVoice.vadModelPath
            }
            "funasr-nano-gguf" {
                $Configuration.asr.funAsrNano.encoderModelPath
                $Configuration.asr.funAsrNano.languageModelPath
                $Configuration.asr.funAsrNano.vadModelPath
            }
            "qwen3-asr" {
                $qwenProperty = $Configuration.asr.PSObject.Properties["qwen3Asr"]
                if ($null -eq $qwenProperty) {
                    throw "Qwen3-ASR is selected but asr.qwen3Asr is missing from appsettings.json. Open and save the configuration in the application first."
                }
                $qwenProperty.Value.convFrontendPath
                $qwenProperty.Value.encoderPath
                $qwenProperty.Value.decoderPath
                Join-Path $qwenProperty.Value.tokenizerPath "merges.txt"
                Join-Path $qwenProperty.Value.tokenizerPath "tokenizer_config.json"
                Join-Path $qwenProperty.Value.tokenizerPath "vocab.json"
            }
            "whisper-cpp" {
                $Configuration.asr.whisperCpp.modelPath
                $Configuration.asr.whisperCpp.vadModelPath
            }
            default {
                throw "Cannot determine model files for provider '$providerId'."
            }
        }
    }

    $streamingProfiles = @(
        $Configuration.profiles.items |
            Where-Object {
                $_.enabled -and
                $null -ne $_.recognition.PSObject.Properties["streamingEnabled"] -and
                $_.recognition.streamingEnabled
            }
    )
    if ($streamingProfiles.Count -gt 0) {
        $streamingProperty = $Configuration.asr.PSObject.Properties["streaming"]
        if ($null -eq $streamingProperty) {
            throw "Streaming recognition is enabled but asr.streaming is missing from appsettings.json. Open and save the configuration in the application first."
        }
        $paths += $streamingProperty.Value.sileroVadModelPath
    }

    $paths | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
}

function Get-WhisperRuntimePaths {
    param(
        [Parameter(Mandatory)][string]$ServerExecutablePath,
        [Parameter(Mandatory)][bool]$UseGpu
    )

    $directory = Split-Path -Parent $ServerExecutablePath
    $fileNames = if ($UseGpu) {
        @(
            "whisper-server.exe"
            "whisper.dll"
            "ggml.dll"
            "ggml-base.dll"
            "ggml-cpu.dll"
            "ggml-vulkan.dll"
        )
    }
    else {
        @(
            "whisper-server.exe"
            "whisper.dll"
            "ggml.dll"
            "ggml-base.dll"
            "ggml-cpu-alderlake.dll"
            "ggml-cpu-cannonlake.dll"
            "ggml-cpu-cascadelake.dll"
            "ggml-cpu-haswell.dll"
            "ggml-cpu-icelake.dll"
            "ggml-cpu-sandybridge.dll"
            "ggml-cpu-skylakex.dll"
            "ggml-cpu-sse42.dll"
            "ggml-cpu-x64.dll"
        )
    }

    $fileNames | ForEach-Object { Join-Path $directory $_ }
}

function Get-PropertyValueOrDefault {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$DefaultValue
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value -or
        ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value))) {
        return $DefaultValue
    }
    $property.Value
}

function Get-CurrentRuntimePaths {
    param([Parameter(Mandatory)]$Configuration)

    $providerIds = @(
        $Configuration.asr.provider
        $Configuration.profiles.items |
            Where-Object { $_.enabled } |
            ForEach-Object { $_.recognition.provider }
    ) | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique

    $paths = foreach ($providerId in $providerIds) {
        switch ($providerId) {
            "paraformer-gguf" {
                $Configuration.asr.paraformer.executablePath
            }
            "sensevoice-gguf" {
                $backendProperty = $Configuration.asr.senseVoice.PSObject.Properties["backend"]
                $useVulkan = $null -ne $backendProperty -and $backendProperty.Value -eq "vulkan"
                if ($useVulkan) {
                    Get-PropertyValueOrDefault $Configuration.asr.senseVoice "vulkanExecutablePath" "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe"
                }
                else {
                    $Configuration.asr.senseVoice.cpuExecutablePath
                }
            }
            "funasr-nano-gguf" {
                $backendProperty = $Configuration.asr.funAsrNano.PSObject.Properties["backend"]
                $useVulkan = $null -ne $backendProperty -and $backendProperty.Value -eq "vulkan"
                if ($useVulkan) {
                    Get-PropertyValueOrDefault $Configuration.asr.funAsrNano "vulkanExecutablePath" "runtimes/funasr-nano-vulkan/llama-funasr-cli.exe"
                }
                else {
                    $Configuration.asr.funAsrNano.executablePath
                }
            }
            "qwen3-asr" {
                # sherpa-onnx native libraries are published with the application.
            }
            "whisper-cpp" {
                $useGpuProperty = $Configuration.asr.whisperCpp.PSObject.Properties["useGpu"]
                $useGpu = $null -ne $useGpuProperty -and [bool]$useGpuProperty.Value
                $serverPath = if ($useGpu) {
                    $Configuration.asr.whisperCpp.vulkanServerExecutablePath
                }
                else {
                    $Configuration.asr.whisperCpp.serverExecutablePath
                }
                Get-WhisperRuntimePaths -ServerExecutablePath $serverPath -UseGpu $useGpu
            }
            default {
                throw "Cannot determine runtime files for provider '$providerId'."
            }
        }
    }

    $paths | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
}

function Get-AllRuntimePaths {
    param([Parameter(Mandatory)]$Configuration)

    @(
        "runtimes/llama-funasr-paraformer.exe"
        "runtimes/llama-funasr-sensevoice.exe"
        "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe"
        "runtimes/llama-funasr-cli.exe"
        "runtimes/funasr-nano-vulkan/llama-funasr-cli.exe"
        Get-WhisperRuntimePaths -ServerExecutablePath $Configuration.asr.whisperCpp.serverExecutablePath -UseGpu $false
        Get-WhisperRuntimePaths -ServerExecutablePath $Configuration.asr.whisperCpp.vulkanServerExecutablePath -UseGpu $true
    ) | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
}

function Copy-SelectedModels {
    param([Parameter(Mandatory)][string[]]$RelativePaths)

    foreach ($relativePath in $RelativePaths) {
        $source = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Configured model was not found: $relativePath"
        }

        $target = Join-Path $packageDirectory $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}

function Copy-SelectedRuntimes {
    param([Parameter(Mandatory)][string[]]$RelativePaths)

    foreach ($relativePath in $RelativePaths) {
        if ([System.IO.Path]::IsPathRooted($relativePath)) {
            throw "Portable runtime paths must be relative to the repository: $relativePath"
        }

        $source = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Configured runtime file was not found: $relativePath"
        }

        $target = Join-Path $packageDirectory $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}

function Get-Sha256Hash {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
}

function Write-PackageManifest {
    param([Parameter(Mandatory)][string]$Directory)

    $manifestPath = Join-Path $Directory "PACKAGE-MANIFEST.sha256"
    $directoryPrefix = [System.IO.Path]::GetFullPath($Directory).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $lines = Get-ChildItem -LiteralPath $Directory -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $fullPath = [System.IO.Path]::GetFullPath($_.FullName)
            if (!$fullPath.StartsWith($directoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Package manifest input is outside the package directory: $fullPath"
            }
            $relativePath = $fullPath.Substring($directoryPrefix.Length).Replace('\', '/')
            $hash = Get-Sha256Hash -Path $_.FullName
            "$hash  $relativePath"
        }
    [System.IO.File]::WriteAllLines($manifestPath, $lines, [System.Text.UTF8Encoding]::new($false))
}

if (Get-Process -Name "VRChatVoiceInput.App" -ErrorAction SilentlyContinue) {
    throw "VRChat Voice Input is running. Exit it from the tray before building the portable package."
}

$requiredPaths = @($appProject, $webUiDirectory, $configPath, $openVrLicensePath, $senseVoiceLicensePath, $paraformerLicensePath, $funAsrNanoLicensePath)
if ($ModelSet -ne "none") {
    $requiredPaths += $modelsDirectory
}
if ($RuntimeSet -ne "none") {
    $requiredPaths += $runtimesDirectory
}
foreach ($requiredPath in $requiredPaths) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required input was not found: $requiredPath"
    }
}

Assert-PathInsideOutputRoot -Path $packageDirectory
Assert-PathInsideOutputRoot -Path $archivePath
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Write-Host "[1/6] Building packaged WebView UI..."
Invoke-CheckedCommand -Command "npm.cmd" -Arguments @("ci") -WorkingDirectory $webUiDirectory
Invoke-CheckedCommand -Command "npm.cmd" -Arguments @("run", "typecheck") -WorkingDirectory $webUiDirectory
Invoke-CheckedCommand -Command "npm.cmd" -Arguments @("run", "build") -WorkingDirectory $webUiDirectory

Write-Host "[2/6] Publishing self-contained $RuntimeIdentifier application..."
Invoke-CheckedCommand -Command "dotnet" -Arguments @(
    "publish",
    $appProject,
    "--configuration", "Release",
    "--runtime", $RuntimeIdentifier,
    "--self-contained", "true",
    "--output", $packageDirectory,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
) -WorkingDirectory $repoRoot

Write-Host "[3/6] Copying configuration and $RuntimeSet ASR runtime set..."
Copy-Item -LiteralPath $configPath -Destination (Join-Path $packageDirectory "appsettings.json") -Force
$configuration = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($RuntimeSet -ne "none") {
    $runtimePaths = if ($RuntimeSet -eq "all") {
        @(Get-AllRuntimePaths -Configuration $configuration)
    }
    else {
        @(Get-CurrentRuntimePaths -Configuration $configuration)
    }
    Copy-SelectedRuntimes -RelativePaths $runtimePaths
}
$licensesDirectory = Join-Path $packageDirectory "licenses"
New-Item -ItemType Directory -Force -Path $licensesDirectory | Out-Null
Copy-Item -LiteralPath $openVrLicensePath -Destination (Join-Path $licensesDirectory "openvr-LICENSE.txt") -Force
Copy-Item -LiteralPath $senseVoiceLicensePath -Destination (Join-Path $licensesDirectory "sensevoice-LICENSE.txt") -Force
Copy-Item -LiteralPath $paraformerLicensePath -Destination (Join-Path $licensesDirectory "funasr-LICENSE.txt") -Force
Copy-Item -LiteralPath $funAsrNanoLicensePath -Destination (Join-Path $licensesDirectory "funasr-nano-LICENSE.txt") -Force

Write-Host "[4/6] Copying $ModelSet model set..."
if ($ModelSet -eq "none") {
    Write-Host "No model files are included; install them from the Models page."
}
elseif ($ModelSet -eq "all") {
    Copy-DirectoryContents -Source $modelsDirectory -Destination (Join-Path $packageDirectory "models")
}
else {
    $modelPaths = if ($ModelSet -eq "current") {
        @(Get-CurrentModelPaths -Configuration $configuration)
    }
    else {
        @(Get-ConfiguredModelPaths -Configuration $configuration)
    }
    Copy-SelectedModels -RelativePaths $modelPaths
}

$testingNotes = @"
VRChat Voice Input portable test build

1. Run VRChatVoiceInput.App.exe.
2. Connect the microphone or VR headset before opening the application.
3. On General, click Start test and confirm every active microphone shows a moving level meter.
4. On Models > SenseVoice or Whisper.cpp, enable Vulkan/GPU and select a detected adapter. Prefer an idle integrated GPU when the VR GPU is saturated. The target computer still needs a Vulkan-capable graphics driver.
5. VRChat: enable OSC, keep the VRChat profile enabled, hold F8 while speaking, and release F8 to send the transcript.
6. Desktop applications: configure Open input, Open delay, Text input, and Submit on the profile Output tab. Focus the target application, hold F8 while speaking, and release it to run the configured output sequence.
7. SteamVR: select SteamVR on a profile's Input tab, save the configuration, then use Controller bindings to choose the physical VR-controller button. Closing SteamVR must leave this application running and waiting for reconnection.

The application is self-contained and does not require a separately installed .NET runtime.
Microsoft Edge WebView2 Runtime is still required. It is normally included with current Windows 10 and Windows 11 installations.
Only the ASR native runtimes required by the currently enabled providers and selected CPU/Vulkan backends are bundled by default. Other runtimes can be installed from the Models page.
Source release builds may contain no ASR models or native runtimes. Install the required components from the Models page before starting recognition.
"@
[System.IO.File]::WriteAllText(
    (Join-Path $packageDirectory "TESTING.txt"),
    $testingNotes,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "[5/6] Generating SHA-256 package manifest..."
Write-PackageManifest -Directory $packageDirectory

if (!$SkipArchive) {
    Write-Host "[6/6] Creating ZIP archive..."
    $tar = Get-Command "tar.exe" -ErrorAction Stop
    Invoke-CheckedCommand -Command $tar.Source -Arguments @(
        "-a", "-c", "-f", $archivePath, $packageName
    ) -WorkingDirectory $outputRoot
}
else {
    Write-Host "[6/6] ZIP creation skipped."
}

$exePath = Join-Path $packageDirectory "VRChatVoiceInput.App.exe"
if (!(Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Publish completed without the expected application executable: $exePath"
}

$packageBytes = (Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host "Portable package ready: $packageDirectory"
Write-Host ("Package size: {0:N2} GiB" -f ($packageBytes / 1GB))
if (!$SkipArchive) {
    $archiveBytes = (Get-Item -LiteralPath $archivePath).Length
    Write-Host "ZIP archive: $archivePath"
    Write-Host ("Archive size: {0:N2} GiB" -f ($archiveBytes / 1GB))
}
