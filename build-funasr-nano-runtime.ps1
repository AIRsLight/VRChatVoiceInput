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
$upstreamUrl = "https://github.com/modelscope/FunASR.git"
$upstreamCommit = "5990412a196518d511bff12584417195fb9c952b"
$patchPath = Join-Path $repoRoot "third_party\funasr-nano-worker\funasr-nano-worker.patch"
$cpuTargetPath = Join-Path $repoRoot "runtimes\llama-funasr-cli.exe"
$vulkanTargetPath = Join-Path $repoRoot "runtimes\funasr-nano-vulkan\llama-funasr-cli.exe"

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $repoRoot ".tmp\FunASRNanoUpstream"
}

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $driveRoot = [System.IO.Path]::GetPathRoot($repoRoot)
    $BuildDirectory = Join-Path $driveRoot ".vrchatvoiceinput-build\nano-worker"
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
    throw "Fun-ASR-Nano worker patch was not found: $patchPath"
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
        "-C", $SourceDirectory, "sparse-checkout", "init", "--cone"
    )
    Invoke-CheckedCommand -Command "git" -Arguments @(
        "-C", $SourceDirectory, "sparse-checkout", "set", "runtime/llama.cpp"
    )
    Invoke-CheckedCommand -Command "git" -Arguments @(
        "-C", $SourceDirectory, "checkout", "--detach", $upstreamCommit
    )
}

$actualCommit = (& git -C $SourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $upstreamCommit) {
    throw "FunASR source must be at pinned commit $upstreamCommit; found $actualCommit."
}

& git -C $SourceDirectory apply --reverse --check $patchPath 2>$null
$patchAlreadyApplied = $LASTEXITCODE -eq 0
if (!$patchAlreadyApplied) {
    & git -C $SourceDirectory apply --check $patchPath 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "FunASR source has changes that conflict with the Fun-ASR-Nano worker patch."
    }

    Invoke-CheckedCommand -Command "git" -Arguments @(
        "-C", $SourceDirectory, "apply", $patchPath
    )
}

$changedFiles = @(& git -C $SourceDirectory diff --name-only)
$expectedSource = "runtime/llama.cpp/fun-asr-nano/funasr-cli/funasr-cli.cpp"
if ($changedFiles.Count -ne 1 -or $changedFiles[0] -ne $expectedSource) {
    throw "FunASR source contains unexpected changes: $($changedFiles -join ', ')"
}

$nativeSource = Join-Path $SourceDirectory "runtime\llama.cpp"
function Build-FunAsrNanoRuntime {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][bool]$EnableVulkan,
        [Parameter(Mandatory)][string]$TargetPath
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    Invoke-CheckedCommand -Command "cmake" -Arguments @(
        "-S", $nativeSource,
        "-B", $OutputDirectory,
        "-A", "x64",
        "-DGGML_OPENMP=OFF",
        "-DGGML_VULKAN=$($EnableVulkan.ToString().ToUpperInvariant())",
        "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded"
    )
    Invoke-CheckedCommand -Command "cmake" -Arguments @(
        "--build", $OutputDirectory,
        "--config", "Release",
        "--target", "llama-funasr-cli",
        "--parallel"
    )

    $builtExecutable = Join-Path $OutputDirectory "bin\Release\llama-funasr-cli.exe"
    if (!(Test-Path -LiteralPath $builtExecutable -PathType Leaf)) {
        throw "Fun-ASR-Nano build completed without the expected executable: $builtExecutable"
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TargetPath) | Out-Null
    Copy-Item -LiteralPath $builtExecutable -Destination $TargetPath -Force
    $hash = (Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Fun-ASR-Nano runtime installed: $TargetPath"
    Write-Host "SHA-256: $hash"
}

Build-FunAsrNanoRuntime -OutputDirectory $BuildDirectory -EnableVulkan $false -TargetPath $cpuTargetPath

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
Build-FunAsrNanoRuntime -OutputDirectory $VulkanBuildDirectory -EnableVulkan $true -TargetPath $vulkanTargetPath
