[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [string]$BuildDirectory,
    [string]$VulkanBuildDirectory,
    [string]$VulkanSdkDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$upstreamUrl = "https://github.com/FunAudioLLM/SenseVoice.git"
$upstreamCommit = "fbf91f3baccebec554dcee68708b0cda61d42805"
$patchPath = Join-Path $repoRoot "third_party\sensevoice-worker\sensevoice-worker.patch"
$cpuTargetPath = Join-Path $repoRoot "runtimes\llama-funasr-sensevoice.exe"
$vulkanTargetPath = Join-Path $repoRoot "runtimes\sensevoice-vulkan\llama-funasr-sensevoice.exe"

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $repoRoot ".tmp\SenseVoiceOfficial"
}

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $driveRoot = [System.IO.Path]::GetPathRoot($repoRoot)
    $BuildDirectory = Join-Path $driveRoot ".vrchatvoiceinput-build\sensevoice"
}

if ([string]::IsNullOrWhiteSpace($VulkanBuildDirectory)) {
    $VulkanBuildDirectory = "${BuildDirectory}-vulkan"
}

if ([string]::IsNullOrWhiteSpace($VulkanSdkDirectory)) {
    $VulkanSdkDirectory = Join-Path $repoRoot "artifacts\vulkan-sdk-1.4.350.0"
}

$SourceDirectory = [System.IO.Path]::GetFullPath($SourceDirectory)
$BuildDirectory = [System.IO.Path]::GetFullPath($BuildDirectory)
$VulkanBuildDirectory = [System.IO.Path]::GetFullPath($VulkanBuildDirectory)
$VulkanSdkDirectory = [System.IO.Path]::GetFullPath($VulkanSdkDirectory)

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$WorkingDirectory = $repoRoot
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

if (!(Test-Path -LiteralPath $patchPath -PathType Leaf)) {
    throw "SenseVoice worker patch was not found: $patchPath"
}

if (!(Test-Path -LiteralPath (Join-Path $SourceDirectory ".git") -PathType Container)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SourceDirectory) | Out-Null
    Invoke-CheckedCommand -Command "git" -Arguments @(
        "clone", "--filter=blob:none", "--no-checkout", $upstreamUrl, $SourceDirectory
    )
    Invoke-CheckedCommand -Command "git" -Arguments @(
        "-C", $SourceDirectory, "fetch", "--depth", "1", "origin", $upstreamCommit
    )
    Invoke-CheckedCommand -Command "git" -Arguments @(
        "-C", $SourceDirectory, "checkout", "--detach", $upstreamCommit
    )
}

$actualCommit = (& git -C $SourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $upstreamCommit) {
    throw "SenseVoice source must be at pinned commit $upstreamCommit; found $actualCommit."
}

& git -C $SourceDirectory apply --reverse --check $patchPath 2>$null
$patchAlreadyApplied = $LASTEXITCODE -eq 0
if (!$patchAlreadyApplied) {
    & git -C $SourceDirectory apply --check $patchPath 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "SenseVoice source has changes that conflict with the resident-worker patch."
    }

    Invoke-CheckedCommand -Command "git" -Arguments @(
        "-C", $SourceDirectory, "apply", $patchPath
    )
}

$changedFiles = @(& git -C $SourceDirectory diff --name-only)
$expectedSource = "runtime/llama.cpp/funasr-sensevoice/funasr-sensevoice.cpp"
if ($changedFiles.Count -ne 1 -or $changedFiles[0] -ne $expectedSource) {
    throw "SenseVoice source contains unexpected changes: $($changedFiles -join ', ')"
}

$nativeSource = Join-Path $SourceDirectory "runtime\llama.cpp"
function Build-SenseVoiceRuntime {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][bool]$EnableVulkan,
        [Parameter(Mandatory)][string]$TargetPath
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $cmakeArguments = @(
        "-S", $nativeSource,
        "-B", $OutputDirectory,
        "-A", "x64",
        "-DGGML_OPENMP=OFF",
        "-DGGML_VULKAN=$($EnableVulkan.ToString().ToUpperInvariant())",
        "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded"
    )
    Invoke-CheckedCommand -Command "cmake" -Arguments $cmakeArguments
    Invoke-CheckedCommand -Command "cmake" -Arguments @(
        "--build", $OutputDirectory,
        "--config", "Release",
        "--target", "llama-funasr-sensevoice",
        "--parallel"
    )

    $builtExecutable = Join-Path $OutputDirectory "bin\Release\llama-funasr-sensevoice.exe"
    if (!(Test-Path -LiteralPath $builtExecutable -PathType Leaf)) {
        throw "SenseVoice build completed without the expected executable: $builtExecutable"
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TargetPath) | Out-Null
    Copy-Item -LiteralPath $builtExecutable -Destination $TargetPath -Force
    $hash = (Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "SenseVoice runtime installed: $TargetPath"
    Write-Host "SHA-256: $hash"
}

Build-SenseVoiceRuntime -OutputDirectory $BuildDirectory -EnableVulkan $false -TargetPath $cpuTargetPath

$vulkanHeader = Join-Path $VulkanSdkDirectory "Include\vulkan\vulkan.h"
$vulkanLibrary = Join-Path $VulkanSdkDirectory "Lib\vulkan-1.lib"
$glslc = Join-Path $VulkanSdkDirectory "Bin\glslc.exe"
foreach ($requiredPath in @($vulkanHeader, $vulkanLibrary, $glslc)) {
    if (!(Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "The Vulkan SDK is incomplete. Missing: $requiredPath"
    }
}

$env:VULKAN_SDK = $VulkanSdkDirectory
$env:Path = "$(Join-Path $VulkanSdkDirectory 'Bin');$env:Path"
Build-SenseVoiceRuntime -OutputDirectory $VulkanBuildDirectory -EnableVulkan $true -TargetPath $vulkanTargetPath
