using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.App;

internal sealed class ModelDownloadService : IDisposable
{
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private ModelDownloadProgress? _current;
    private long _lastProgressTimestamp;

    public event EventHandler<ModelDownloadProgress>? ProgressChanged;

    public ModelDownloadProgress? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public async Task<bool> DownloadAsync(
        string providerId,
        AsrConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        await DownloadItemsAsync(
            providerId,
            ModelDownloadCatalog.Resolve(providerId, configuration),
            cancellationToken);

    public async Task<bool> DownloadAssetAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        var package = ModelDownloadCatalog.ResolvePackage(assetId);
        return await DownloadItemsAsync(package.ProviderId, package.Items, cancellationToken);
    }

    private async Task<bool> DownloadItemsAsync(
        string providerId,
        IReadOnlyList<ModelDownloadItem> assets,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource operationCancellation;
        lock (_sync)
        {
            if (_activeCancellation is not null)
            {
                throw new InvalidOperationException("Another download is already running.");
            }

            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = operationCancellation;
        }

        var totalBytes = assets.Sum(item => item.Asset.TransferSize);
        long completedBytes = 0;

        try
        {
            Report(providerId, "checking", null, 0, totalBytes, "Checking installed model files.", force: true);
            foreach (var item in assets)
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                var targetPath = Path.GetFullPath(item.TargetPath);
                if (await IsValidFileAsync(targetPath, item.Asset, operationCancellation.Token))
                {
                    completedBytes += item.Asset.TransferSize;
                    Report(
                        providerId,
                        "checking",
                        item.Asset.FileName,
                        completedBytes,
                        totalBytes,
                        $"Verified {item.Asset.FileName}.",
                        force: true);
                    continue;
                }

                await DownloadAssetWithRetryAsync(
                    providerId,
                    item.Asset,
                    targetPath,
                    completedBytes,
                    totalBytes,
                    operationCancellation.Token);
                completedBytes += item.Asset.TransferSize;
            }

            Report(providerId, "completed", null, totalBytes, totalBytes, "Model files are installed and verified.", force: true);
            return true;
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            Report(providerId, "canceled", null, completedBytes, totalBytes, "Download canceled. Partial files will be resumed next time.", force: true);
            return false;
        }
        catch (Exception exception)
        {
            Report(providerId, "error", null, completedBytes, totalBytes, exception.Message, force: true);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeCancellation, operationCancellation))
                {
                    _activeCancellation = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    public bool Cancel()
    {
        lock (_sync)
        {
            if (_activeCancellation is null)
            {
                return false;
            }

            _activeCancellation.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _activeCancellation?.Cancel();
        }
    }

    private async Task DownloadAssetWithRetryAsync(
        string providerId,
        ModelAsset asset,
        string targetPath,
        long completedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                await DownloadAssetAsync(providerId, asset, targetPath, completedBytes, totalBytes, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or CryptographicException)
            {
                lastError = exception;
                if (attempt == MaximumAttempts)
                {
                    break;
                }

                Report(
                    providerId,
                    "downloading",
                    asset.FileName,
                    completedBytes,
                    totalBytes,
                    $"Download interrupted. Retrying {asset.FileName} ({attempt + 1}/{MaximumAttempts}).",
                    force: true);
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        throw new IOException($"Unable to download {asset.FileName} after {MaximumAttempts} attempts: {lastError?.Message}", lastError);
    }

    private async Task DownloadAssetAsync(
        string providerId,
        ModelAsset asset,
        string targetPath,
        long completedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (asset.Archive is not null)
        {
            await DownloadArchivedAssetAsync(
                providerId,
                asset,
                targetPath,
                completedBytes,
                totalBytes,
                cancellationToken);
            return;
        }

        DeleteLegacyArchiveArtifacts(targetPath);

        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Model target directory is unavailable: {targetPath}");
        Directory.CreateDirectory(directory);
        var temporaryPath = targetPath + ".download";
        var existingLength = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        if (existingLength > asset.Size)
        {
            File.Delete(temporaryPath);
            existingLength = 0;
        }

        if (existingLength == asset.Size)
        {
            Report(providerId, "verifying", asset.FileName, completedBytes + asset.Size, totalBytes, $"Verifying {asset.FileName}.", force: true);
            if (await HasExpectedHashAsync(temporaryPath, asset.Sha256, cancellationToken))
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
                return;
            }

            File.Delete(temporaryPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            existingLength = 0;
        }

        var mode = append ? FileMode.Append : FileMode.Create;
        await using var output = new FileStream(
            temporaryPath,
            mode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[1024 * 128];
        long downloaded = existingLength;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            Report(
                providerId,
                "downloading",
                asset.FileName,
                completedBytes + downloaded,
                totalBytes,
                $"Downloading {asset.FileName}.");
        }

        await output.FlushAsync(cancellationToken);
        if (downloaded != asset.Size)
        {
            throw new IOException(
                $"Downloaded size for {asset.FileName} was {downloaded.ToString("N0", CultureInfo.InvariantCulture)} bytes; expected {asset.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes.");
        }

        Report(providerId, "verifying", asset.FileName, completedBytes + asset.Size, totalBytes, $"Verifying {asset.FileName}.", force: true);
        if (!await HasExpectedHashAsync(temporaryPath, asset.Sha256, cancellationToken))
        {
            File.Delete(temporaryPath);
            throw new CryptographicException($"SHA-256 verification failed for {asset.FileName}.");
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    private static void DeleteLegacyArchiveArtifacts(string targetPath)
    {
        foreach (var path in new[] { targetPath + ".archive", targetPath + ".archive.download" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task DownloadArchivedAssetAsync(
        string providerId,
        ModelAsset asset,
        string targetPath,
        long completedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var archive = asset.Archive!;
        var archivePath = targetPath + ".archive";
        var downloadAsset = new ModelAsset(
            asset.Repository,
            asset.Revision,
            archive.FileName,
            archive.Size,
            archive.Sha256,
            asset.DirectDownloadUrl);
        if (!await IsValidFileAsync(archivePath, downloadAsset, cancellationToken))
        {
            await DownloadAssetAsync(
                providerId,
                downloadAsset,
                archivePath,
                completedBytes,
                totalBytes,
                cancellationToken);
        }

        var extractionDirectory = targetPath + ".extract";
        if (Directory.Exists(extractionDirectory))
        {
            Directory.Delete(extractionDirectory, recursive: true);
        }

        Directory.CreateDirectory(extractionDirectory);
        try
        {
            Report(
                providerId,
                "extracting",
                asset.FileName,
                completedBytes + archive.Size,
                totalBytes,
                $"Extracting {asset.FileName}.",
                force: true);
            var startInfo = new ProcessStartInfo("tar.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-xjf");
            startInfo.ArgumentList.Add(archivePath);
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(extractionDirectory);
            startInfo.ArgumentList.Add(archive.EntryPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows tar.exe for model extraction.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new IOException($"Unable to extract {asset.FileName}: {detail.Trim()}");
            }

            var extractedPath = Path.Combine(
                extractionDirectory,
                archive.EntryPath.Replace('/', Path.DirectorySeparatorChar));
            if (!await IsValidFileAsync(extractedPath, asset, cancellationToken))
            {
                throw new CryptographicException(
                    $"Extracted file verification failed for {asset.FileName}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(extractedPath, targetPath, overwrite: true);
            File.Delete(archivePath);
        }
        finally
        {
            if (Directory.Exists(extractionDirectory))
            {
                Directory.Delete(extractionDirectory, recursive: true);
            }
        }
    }

    private static async Task<bool> IsValidFileAsync(string path, ModelAsset asset, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size)
        {
            return false;
        }

        return await HasExpectedHashAsync(path, asset.Sha256, cancellationToken);
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(hash), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private void Report(
        string providerId,
        string state,
        string? fileName,
        long bytesDownloaded,
        long totalBytes,
        string message,
        bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastProgressTimestamp, now) < ProgressInterval)
        {
            return;
        }

        _lastProgressTimestamp = now;
        var progress = new ModelDownloadProgress(providerId, state, fileName, bytesDownloaded, totalBytes, message);
        lock (_sync)
        {
            _current = progress;
        }

        ProgressChanged?.Invoke(this, progress);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VRChatVoiceInput/1.0");
        return client;
    }
}

internal sealed class ModelDownloadProgress : EventArgs
{
    public ModelDownloadProgress(
        string providerId,
        string state,
        string? fileName,
        long bytesDownloaded,
        long totalBytes,
        string message)
    {
        ProviderId = providerId;
        State = state;
        FileName = fileName;
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        Message = message;
    }

    public string ProviderId { get; }

    public string State { get; }

    public string? FileName { get; }

    public long BytesDownloaded { get; }

    public long TotalBytes { get; }

    public string Message { get; }
}

internal sealed record ModelAsset(
    string Repository,
    string Revision,
    string FileName,
    long Size,
    string Sha256,
    string? DirectDownloadUrl = null,
    ModelArchive? Archive = null)
{
    public long TransferSize => Archive?.Size ?? Size;

    public string DownloadUrl =>
        DirectDownloadUrl ??
        $"https://huggingface.co/{Repository}/resolve/{Revision}/{EscapeRepositoryPath(FileName)}?download=true";

    private static string EscapeRepositoryPath(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
}

internal sealed record ModelArchive(
    string FileName,
    long Size,
    string Sha256,
    string EntryPath);

internal sealed record ModelDownloadItem(ModelAsset Asset, string TargetPath);

internal sealed record ModelDownloadPackage(
    string Id,
    string ProviderId,
    string ComponentId,
    string Variant,
    IReadOnlyList<ModelDownloadItem> Items);

internal sealed record ModelAssetStatus(
    string Id,
    string ProviderId,
    string ComponentId,
    string Variant,
    string TargetPath,
    long Size,
    bool Installed,
    bool UpdateAvailable);

internal static class ModelDownloadCatalog
{
    private const string ParaformerLowMemoryRepository = "AIRsLight/paraformer-zh-GGUF";
    private const string ParaformerLowMemoryRevision = "63bbcf93be5ddb3d6ccf9fa6865a16df82663e0a";
    private const string SenseVoiceLowMemoryRepository = "AIRsLight/SenseVoiceSmall-GGUF";
    private const string SenseVoiceLowMemoryRevision = "d6bfce12fb0369874d357e9ebeed012e6349fd25";
    private const string NanoLowMemoryRepository = "AIRsLight/Fun-ASR-Nano-2512-GGUF";
    private const string NanoLowMemoryRevision = "69680848e36e28090e7532842bfa9bb5c1ebde61";
    private const string RuntimeRepository = "AIRsLight/VRChatVoiceInput-Runtimes";
    private const string RuntimeRevision = "2a188dfe4289b3a37d374dbd2352f0fa9ac5f5d0";

    private static readonly ModelAsset ParaformerCpuRuntime = RuntimeAsset(
        "runtimes/paraformer-cpu/llama-funasr-paraformer.exe",
        1926144,
        "57cb7beed899cbc0914c498614c7dbc816d0deecf2f2cf8f0e915c41f3f62527");

    private static readonly ModelAsset SenseVoiceCpuRuntime = RuntimeAsset(
        "runtimes/sensevoice-cpu/llama-funasr-sensevoice.exe",
        1916416,
        "977fcd532c7b1465a6e5b73916e0db878001adea0cf5bf94621e3c36ec59a745");

    private static readonly ModelAsset SenseVoiceVulkanRuntime = RuntimeAsset(
        "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe",
        75918848,
        "583d4dcbea043c85d957758d08455dfc8ed2d50a626007db61d59a89cc257bf8");

    private static readonly ModelAsset NanoCpuRuntime = RuntimeAsset(
        "runtimes/funasr-nano-cpu/llama-funasr-cli.exe",
        3998720,
        "93092e523328d3af5579396755200c81efbe92b05f1f36b4e6b1a421f0353e9b");

    private static readonly ModelAsset NanoVulkanRuntime = RuntimeAsset(
        "runtimes/funasr-nano-vulkan/llama-funasr-cli.exe",
        77970944,
        "6fc43298ba433ac603eb40f193e0b5d3469dbb826961ace70a302aa46d87d0bd");

    private static readonly ModelAsset WhisperCpuServer = RuntimeAsset(
        "runtimes/whisper-cpu/whisper-server.exe", 725504, "2c1ef08694756eda280e79b8217da63ee2af33c87ac3d5f27d68f9f3f966fd32");
    private static readonly ModelAsset WhisperCpuLibrary = RuntimeAsset(
        "runtimes/whisper-cpu/whisper.dll", 1366016, "b31690c12461517fe9774e61318ab63a69972b948151feed98b913be35f708b6");
    private static readonly ModelAsset WhisperCpuGgml = RuntimeAsset(
        "runtimes/whisper-cpu/ggml.dll", 67584, "db753141098018ab482796052a61e727ee0106cbc280f28397f6a111b5e667d7");
    private static readonly ModelAsset WhisperCpuGgmlBase = RuntimeAsset(
        "runtimes/whisper-cpu/ggml-base.dll", 656384, "8be6f3e06388b3a9aac75d29bec86363e2e2f5b0cee86ce6438866bcac0bcf86");

    private static readonly ModelAsset[] WhisperCpuBackends =
    [
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-alderlake.dll", 790528, "323408503da53ccc67248b26d711f16d73d2d6239f7703a00a6a18b60ed5b8b8"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-cannonlake.dll", 833536, "0f659d98b823bb871c7845787bba7485facd220099cf58aa773652b9b842ab2e"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-cascadelake.dll", 830976, "8116b0e516134139de29400c536ecf06fe708ce1a078a96d30b562b30d524fbe"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-haswell.dll", 791552, "e5925923a47672392f9e9c8c92e4b9b65ea473948bf4f568a0300a3a42485135"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-icelake.dll", 830976, "b726d528bee0c811c6b2ad8775357379d651cabb487bbf800331697fe73da187"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-sandybridge.dll", 783360, "1c49c64817233b2447ca305b41c66afa4bed31b058bc190a98af2a30cc703542"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-skylakex.dll", 833536, "06082dc62a09a82fbba4aab49b2c049b96db84c5fc561a446a8ddbfb9b20bf86"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-sse42.dll", 772096, "9a8f55ff1dfad231aa6250ac52c330c5bfa5c4c37691c8b591a68b52090ce40c"),
        RuntimeAsset("runtimes/whisper-cpu/ggml-cpu-x64.dll", 776704, "45ff644d301b8a1fffc7c5e3864205047360eb197814c7311f366d106bb5b19f")
    ];

    private static readonly ModelAsset WhisperVulkanServer = RuntimeAsset(
        "runtimes/whisper-vulkan/whisper-server.exe", 725504, "2c1ef08694756eda280e79b8217da63ee2af33c87ac3d5f27d68f9f3f966fd32");
    private static readonly ModelAsset WhisperVulkanLibrary = RuntimeAsset(
        "runtimes/whisper-vulkan/whisper.dll", 1308160, "aa3052ad44b299379e86a3aac063794f138dda76d55b1a60d1c2dd588c344879");
    private static readonly ModelAsset WhisperVulkanGgml = RuntimeAsset(
        "runtimes/whisper-vulkan/ggml.dll", 67072, "973d7ff78e49fbc3917cda3f7a518bb5e022358991d41404dd2cc407f2ec54b2");
    private static readonly ModelAsset WhisperVulkanGgmlBase = RuntimeAsset(
        "runtimes/whisper-vulkan/ggml-base.dll", 639488, "280ee55bb770d4d22a11e62b08e40fa312f6167e0dacaf436f4cc367d85386b8");
    private static readonly ModelAsset WhisperVulkanCpuBackend = RuntimeAsset(
        "runtimes/whisper-vulkan/ggml-cpu.dll", 786944, "b756806d2dcbd68abd26b61f960684238f788bac026fa6487b462f0845690298");
    private static readonly ModelAsset WhisperVulkanBackend = RuntimeAsset(
        "runtimes/whisper-vulkan/ggml-vulkan.dll", 73818112, "34f0b5465ca4c9a4529a96d043f64ebd2ffc440182ca39fe55a1f73d79355b30");

    private static readonly ModelAsset ParaformerQ8 = new(
        "FunAudioLLM/Paraformer-GGUF",
        "de2cbaaa0f30b34f398d7a066fdfefb8e50d902c",
        "paraformer-q8.gguf",
        236929024,
        "42bf76ea1575a336aaca4c1b7c01a82b79113e6d04d0d6b799561bfcf07ee011");

    private static readonly ModelAsset ParaformerQ5 = new(
        ParaformerLowMemoryRepository,
        ParaformerLowMemoryRevision,
        "paraformer-q5_0.gguf",
        156967168,
        "1f2309eacd761c1f4184177c718321cd6ad3c07e7b17d6f796e5fb15565906ec");

    private static readonly ModelAsset ParaformerQ4 = new(
        ParaformerLowMemoryRepository,
        ParaformerLowMemoryRevision,
        "paraformer-q4_0.gguf",
        130313216,
        "992562722aa2c4e88158245a0fc5be0e1338c580db808a97c6262f6864317584");

    private static readonly ModelAsset SenseVoiceQ8 = new(
        "FunAudioLLM/SenseVoiceSmall-GGUF",
        "90c1c61912018b70ada0fcc024ea24aca62f2e63",
        "sensevoice-small-q8.gguf",
        254208320,
        "4ae45c94422de949b387e2e0fb10d7e14e4c42c69db30c3444ecc7d4b844b7c5");

    private static readonly ModelAsset SenseVoiceQ5 = new(
        SenseVoiceLowMemoryRepository,
        SenseVoiceLowMemoryRevision,
        "sensevoice-small-q5_0.gguf",
        167117312,
        "24114cc2663de19da1f8c53c2232d9c98f8a6d9e663b2ab4766b414e691b6818");

    private static readonly ModelAsset NanoEncoderF16 = new(
        "FunAudioLLM/Fun-ASR-Nano-GGUF",
        "c1629cbf83548ea0d92077c09d3541ce407ee643",
        "funasr-encoder-f16.gguf",
        469331008,
        "f92f91d01a24fbed6c863495b2ee8c6a6788144a02858b75743f0946668de8a2");

    private static readonly ModelAsset NanoEncoderQ8 = new(
        NanoLowMemoryRepository,
        NanoLowMemoryRevision,
        "funasr-encoder-q8_0.gguf",
        253553728,
        "5aa6af0e6efad175d66525b87b4531b162733f25449a74667c73c7c3bfd28e10");

    private static readonly ModelAsset NanoQ4Km = new(
        "FunAudioLLM/Fun-ASR-Nano-GGUF",
        "c1629cbf83548ea0d92077c09d3541ce407ee643",
        "qwen3-0.6b-q4km.gguf",
        484219776,
        "cc5057552aa9dddedcda73ea8889854e8a257eb07d0a561b7234465c1e856f22");

    private static readonly ModelAsset NanoQ5Km = new(
        "FunAudioLLM/Fun-ASR-Nano-GGUF",
        "c1629cbf83548ea0d92077c09d3541ce407ee643",
        "qwen3-0.6b-q5km.gguf",
        551377792,
        "dc2e6e195c534cbaea208c030d3ab55be4d3385ae4b726966cff8639a69aa2a2");

    private static readonly ModelAsset NanoQ8 = new(
        "FunAudioLLM/Fun-ASR-Nano-GGUF",
        "c1629cbf83548ea0d92077c09d3541ce407ee643",
        "qwen3-0.6b-q8_0.gguf",
        804753280,
        "819f385dc0e035dccc3d9e7edaf6b7b044b8ba7ace63cbcbf84c7e397eecbf27");

    private static readonly ModelAsset FsmnVad = new(
        "FunAudioLLM/fsmn-vad-GGUF",
        "6840bae4c5c92ee8c04faaf4db23dd0105098d7f",
        "fsmn-vad.gguf",
        1720512,
        "1270f2559c495f4e7b6e739541151027d360761a3fda43fc147034f5719f5479");

    private static readonly ModelAsset ParaformerPunctuationInt8 = new(
        ParaformerLowMemoryRepository,
        "5e1cbe68a235f6082a1868d50530e0a308cd1fd9",
        "paraformer-punctuation-int8.onnx",
        75519198,
        "65a3fb9f5ad7bfb96bf69e0dc4481df97f6ee60513c1d94ce981ba6effd524b1");

    private static readonly ModelAsset SileroVad = new(
        "k2-fsa/sherpa-onnx",
        "asr-models",
        "silero_vad.onnx",
        643854,
        "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx");

    private static readonly ModelAsset WhisperSmallQ5 = new(
        "ggerganov/whisper.cpp",
        "5359861c739e955e79d9a303bcbc70fb988958b1",
        "ggml-small-q5_1.bin",
        190085487,
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb");

    private const string Qwen3AsrRepository = "csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25";
    private const string Qwen3AsrRevision = "68818b2313fe77bd06f6a7c5068ff3ef59d02b8a";

    private static readonly ModelAsset Qwen3ConvFrontend = new(
        Qwen3AsrRepository,
        Qwen3AsrRevision,
        "conv_frontend.onnx",
        44148281,
        "d22dc4423e0940e49884e903d2ea2f7e5567c14fc1aed97e4e26d6b8f208ef9e");

    private static readonly ModelAsset Qwen3EncoderInt8 = new(
        Qwen3AsrRepository,
        Qwen3AsrRevision,
        "encoder.int8.onnx",
        182491662,
        "60748d3e6744a57c9c91e1b17424a6c2990567e8adceb0783940c03ed98fa9d9");

    private static readonly ModelAsset Qwen3DecoderInt8 = new(
        Qwen3AsrRepository,
        Qwen3AsrRevision,
        "decoder.int8.onnx",
        755914231,
        "4f6885be5959ae26af3089d38ee7972c5fafbeeb1cf8d5e76eab6d8b61ca5771");

    private static readonly ModelAsset Qwen3TokenizerMerges = new(
        Qwen3AsrRepository,
        Qwen3AsrRevision,
        "tokenizer/merges.txt",
        1671853,
        "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5");

    private static readonly ModelAsset Qwen3TokenizerConfiguration = new(
        Qwen3AsrRepository,
        Qwen3AsrRevision,
        "tokenizer/tokenizer_config.json",
        12487,
        "4942d005604266809309cabc9f4e9cb89ce855d59b14681fdc0e1cc62ea26c4c");

    private static readonly ModelAsset Qwen3TokenizerVocabulary = new(
        Qwen3AsrRepository,
        Qwen3AsrRevision,
        "tokenizer/vocab.json",
        2776833,
        "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910");

    public static IReadOnlyList<ModelAssetStatus> GetAssetStatuses() =>
        GetPackages().Select(package =>
        {
            var isRuntime = package.ComponentId.StartsWith("runtime-", StringComparison.Ordinal);
            var installed = package.Items.All(item => isRuntime
                ? File.Exists(Path.GetFullPath(item.TargetPath))
                : IsInstalled(item));
            return new ModelAssetStatus(
                package.Id,
                package.ProviderId,
                package.ComponentId,
                package.Variant,
                package.Items.Count == 1 ? package.Items[0].TargetPath : string.Empty,
                package.Items.Sum(item => item.Asset.Size),
                installed,
                isRuntime && installed && !package.Items.All(IsInstalled));
        }).ToArray();

    public static ModelDownloadPackage ResolvePackage(string assetId) =>
        GetPackages().FirstOrDefault(package =>
            string.Equals(package.Id, assetId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unknown downloadable model asset '{assetId}'.");

    private static IReadOnlyList<ModelDownloadPackage> GetPackages() =>
    [
        RuntimePackage(
            "paraformer-runtime-cpu", "paraformer-gguf", "runtime-cpu", "CPU",
            (ParaformerCpuRuntime, "runtimes/llama-funasr-paraformer.exe")),
        Package("paraformer-q5_0", "paraformer-gguf", "model", "Q5_0", ParaformerQ5, "models/paraformer-q5_0.gguf"),
        Package("paraformer-q8_0", "paraformer-gguf", "model", "Q8_0", ParaformerQ8, "models/paraformer-q8.gguf"),
        Package("paraformer-q4_0", "paraformer-gguf", "model", "Q4_0", ParaformerQ4, "models/paraformer-q4_0.gguf"),
        Package("paraformer-punctuation-int8", "paraformer-gguf", "punctuation", "INT8", ParaformerPunctuationInt8, "models/paraformer-punctuation-int8.onnx"),
        Package("paraformer-fsmn-vad", "paraformer-gguf", "vad", "FSMN", FsmnVad, "models/fsmn-vad.gguf"),
        Package("paraformer-silero-vad", "paraformer-gguf", "streaming-vad", "Silero", SileroVad, "models/silero_vad.onnx"),
        RuntimePackage(
            "sensevoice-runtime-cpu", "sensevoice-gguf", "runtime-cpu", "CPU",
            (SenseVoiceCpuRuntime, "runtimes/llama-funasr-sensevoice.exe")),
        RuntimePackage(
            "sensevoice-runtime-vulkan", "sensevoice-gguf", "runtime-vulkan", "Vulkan",
            (SenseVoiceVulkanRuntime, "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe")),
        Package("sensevoice-q8_0", "sensevoice-gguf", "model", "Q8_0", SenseVoiceQ8, "models/sensevoice-small-q8.gguf"),
        Package("sensevoice-q5_0", "sensevoice-gguf", "model", "Q5_0", SenseVoiceQ5, "models/sensevoice-small-q5_0.gguf"),
        Package("sensevoice-fsmn-vad", "sensevoice-gguf", "vad", "FSMN", FsmnVad, "models/fsmn-vad.gguf"),
        Package("sensevoice-silero-vad", "sensevoice-gguf", "streaming-vad", "Silero", SileroVad, "models/silero_vad.onnx"),
        RuntimePackage(
            "nano-runtime-cpu", "funasr-nano-gguf", "runtime-cpu", "CPU",
            (NanoCpuRuntime, "runtimes/llama-funasr-cli.exe")),
        RuntimePackage(
            "nano-runtime-vulkan", "funasr-nano-gguf", "runtime-vulkan", "Vulkan",
            (NanoVulkanRuntime, "runtimes/funasr-nano-vulkan/llama-funasr-cli.exe")),
        Package("nano-encoder-q8_0", "funasr-nano-gguf", "encoder", "Q8_0", NanoEncoderQ8, "models/funasr-encoder-q8_0.gguf"),
        Package("nano-encoder-f16", "funasr-nano-gguf", "encoder", "F16", NanoEncoderF16, "models/funasr-encoder-f16.gguf"),
        Package("nano-language-q4_k_m", "funasr-nano-gguf", "language-model", "Q4_K_M", NanoQ4Km, "models/qwen3-0.6b-q4km.gguf"),
        Package("nano-language-q5_k_m", "funasr-nano-gguf", "language-model", "Q5_K_M", NanoQ5Km, "models/qwen3-0.6b-q5km.gguf"),
        Package("nano-language-q8_0", "funasr-nano-gguf", "language-model", "Q8_0", NanoQ8, "models/qwen3-0.6b-q8_0.gguf"),
        Package("nano-fsmn-vad", "funasr-nano-gguf", "vad", "FSMN", FsmnVad, "models/fsmn-vad.gguf"),
        Package("nano-silero-vad", "funasr-nano-gguf", "streaming-vad", "Silero", SileroVad, "models/silero_vad.onnx"),
        new ModelDownloadPackage(
            "qwen3-asr-int8",
            "qwen3-asr",
            "model-bundle",
            "INT8",
            [
                new ModelDownloadItem(Qwen3ConvFrontend, "models/qwen3-asr/conv_frontend.onnx"),
                new ModelDownloadItem(Qwen3EncoderInt8, "models/qwen3-asr/encoder.int8.onnx"),
                new ModelDownloadItem(Qwen3DecoderInt8, "models/qwen3-asr/decoder.int8.onnx"),
                new ModelDownloadItem(Qwen3TokenizerMerges, "models/qwen3-asr/tokenizer/merges.txt"),
                new ModelDownloadItem(Qwen3TokenizerConfiguration, "models/qwen3-asr/tokenizer/tokenizer_config.json"),
                new ModelDownloadItem(Qwen3TokenizerVocabulary, "models/qwen3-asr/tokenizer/vocab.json")
            ]),
        Package("qwen3-silero-vad", "qwen3-asr", "streaming-vad", "Silero", SileroVad, "models/silero_vad.onnx"),
        WhisperCpuRuntimePackage(),
        RuntimePackage(
            "whisper-runtime-vulkan", "whisper-cpp", "runtime-vulkan", "Vulkan",
            (WhisperVulkanServer, "runtimes/whispercpp-vulkan/Release/whisper-server.exe"),
            (WhisperVulkanLibrary, "runtimes/whispercpp-vulkan/Release/whisper.dll"),
            (WhisperVulkanGgml, "runtimes/whispercpp-vulkan/Release/ggml.dll"),
            (WhisperVulkanGgmlBase, "runtimes/whispercpp-vulkan/Release/ggml-base.dll"),
            (WhisperVulkanCpuBackend, "runtimes/whispercpp-vulkan/Release/ggml-cpu.dll"),
            (WhisperVulkanBackend, "runtimes/whispercpp-vulkan/Release/ggml-vulkan.dll")),
        Package("whisper-small-q5_1", "whisper-cpp", "model", "Small Q5_1", WhisperSmallQ5, "models/ggml-small-q5_1.bin"),
        Package("whisper-silero-vad", "whisper-cpp", "streaming-vad", "Silero", SileroVad, "models/silero_vad.onnx")
    ];

    private static ModelDownloadPackage Package(
        string id,
        string providerId,
        string componentId,
        string variant,
        ModelAsset asset,
        string targetPath) =>
        new(id, providerId, componentId, variant, [new ModelDownloadItem(asset, targetPath)]);

    private static ModelDownloadPackage RuntimePackage(
        string id,
        string providerId,
        string componentId,
        string variant,
        params (ModelAsset Asset, string TargetPath)[] items) =>
        new(id, providerId, componentId, variant,
            items.Select(item => new ModelDownloadItem(item.Asset, item.TargetPath)).ToArray());

    private static ModelDownloadPackage WhisperCpuRuntimePackage()
    {
        var items = new List<ModelDownloadItem>
        {
            new(WhisperCpuServer, "runtimes/whispercpp/Release/whisper-server.exe"),
            new(WhisperCpuLibrary, "runtimes/whispercpp/Release/whisper.dll"),
            new(WhisperCpuGgml, "runtimes/whispercpp/Release/ggml.dll"),
            new(WhisperCpuGgmlBase, "runtimes/whispercpp/Release/ggml-base.dll")
        };
        items.AddRange(WhisperCpuBackends.Select(asset => new ModelDownloadItem(
            asset,
            $"runtimes/whispercpp/Release/{Path.GetFileName(asset.FileName)}")));
        return new ModelDownloadPackage(
            "whisper-runtime-cpu", "whisper-cpp", "runtime-cpu", "CPU", items);
    }

    private static ModelAsset RuntimeAsset(string fileName, long size, string sha256) =>
        new(RuntimeRepository, RuntimeRevision, fileName, size, sha256);

    private static bool IsInstalled(ModelDownloadItem item)
    {
        var path = Path.GetFullPath(item.TargetPath);
        return File.Exists(path) && new FileInfo(path).Length == item.Asset.Size;
    }

    public static IReadOnlyList<ModelDownloadItem> Resolve(string providerId, AsrConfiguration configuration)
    {
        IReadOnlyList<ModelDownloadItem> items = providerId.ToLowerInvariant() switch
        {
            "paraformer-gguf" => AddOptionalVad(
                AddParaformerPunctuation(
                    [new ModelDownloadItem(
                        ResolveParaformerModel(configuration.Paraformer.ModelPath),
                        RequirePath(configuration.Paraformer.ModelPath, "Paraformer model"))],
                    configuration.Paraformer),
                configuration.Paraformer.VadModelPath),
            "sensevoice-gguf" => AddOptionalVad(
                [new ModelDownloadItem(
                    ResolveSenseVoiceModel(configuration.SenseVoice.ModelPath),
                    RequirePath(configuration.SenseVoice.ModelPath, "SenseVoice model"))],
                configuration.SenseVoice.VadModelPath),
            "funasr-nano-gguf" => AddOptionalVad(
                [
                    new ModelDownloadItem(
                        ResolveNanoEncoder(configuration.FunAsrNano.EncoderModelPath),
                        RequirePath(configuration.FunAsrNano.EncoderModelPath, "Fun-ASR-Nano encoder model")),
                    new ModelDownloadItem(
                        ResolveNanoLanguageModel(configuration.FunAsrNano.LanguageModelPath),
                        RequirePath(configuration.FunAsrNano.LanguageModelPath, "Fun-ASR-Nano language model"))
                ],
                configuration.FunAsrNano.VadModelPath),
            "qwen3-asr" =>
                [
                    new ModelDownloadItem(Qwen3ConvFrontend, RequirePath(configuration.Qwen3Asr.ConvFrontendPath, "Qwen3-ASR convolution frontend")),
                    new ModelDownloadItem(Qwen3EncoderInt8, RequirePath(configuration.Qwen3Asr.EncoderPath, "Qwen3-ASR encoder model")),
                    new ModelDownloadItem(Qwen3DecoderInt8, RequirePath(configuration.Qwen3Asr.DecoderPath, "Qwen3-ASR decoder model")),
                    new ModelDownloadItem(Qwen3TokenizerMerges, TokenizerTarget(configuration.Qwen3Asr.TokenizerPath, "merges.txt")),
                    new ModelDownloadItem(Qwen3TokenizerConfiguration, TokenizerTarget(configuration.Qwen3Asr.TokenizerPath, "tokenizer_config.json")),
                    new ModelDownloadItem(Qwen3TokenizerVocabulary, TokenizerTarget(configuration.Qwen3Asr.TokenizerPath, "vocab.json"))
                ],
            "whisper-cpp" =>
                [new ModelDownloadItem(WhisperSmallQ5, RequirePath(configuration.WhisperCpp.ModelPath, "Whisper model"))],
            _ => throw new InvalidOperationException($"Automatic model download is not available for provider '{providerId}'.")
        };
        return AddStreamingVad(items, configuration.Streaming.SileroVadModelPath);
    }

    private static ModelAsset ResolveParaformerModel(string modelPath) =>
        Path.GetFileName(RequirePath(modelPath, "Paraformer model")).ToLowerInvariant() switch
        {
            "paraformer-q8.gguf" => ParaformerQ8,
            "paraformer-q5_0.gguf" => ParaformerQ5,
            "paraformer-q4_0.gguf" => ParaformerQ4,
            _ => throw new InvalidOperationException(
                "Automatic download is available only for the standard Paraformer Q8_0, Q5_0, and Q4_0 model paths.")
        };

    private static ModelAsset ResolveSenseVoiceModel(string modelPath) =>
        Path.GetFileName(RequirePath(modelPath, "SenseVoice model")).ToLowerInvariant() switch
        {
            "sensevoice-small-q8.gguf" => SenseVoiceQ8,
            "sensevoice-small-q5_0.gguf" => SenseVoiceQ5,
            _ => throw new InvalidOperationException(
                "Automatic download is available only for the standard SenseVoice Q8_0 and Q5_0 model paths.")
        };

    private static ModelAsset ResolveNanoEncoder(string modelPath) =>
        Path.GetFileName(RequirePath(modelPath, "Fun-ASR-Nano encoder model")).ToLowerInvariant() switch
        {
            "funasr-encoder-f16.gguf" => NanoEncoderF16,
            "funasr-encoder-q8_0.gguf" => NanoEncoderQ8,
            _ => throw new InvalidOperationException(
                "Automatic download is available only for the standard Fun-ASR-Nano F16 and Q8_0 encoder paths.")
        };

    private static ModelAsset ResolveNanoLanguageModel(string modelPath) =>
        Path.GetFileName(RequirePath(modelPath, "Fun-ASR-Nano language model")).ToLowerInvariant() switch
        {
            "qwen3-0.6b-q4km.gguf" => NanoQ4Km,
            "qwen3-0.6b-q5km.gguf" => NanoQ5Km,
            "qwen3-0.6b-q8_0.gguf" => NanoQ8,
            _ => throw new InvalidOperationException(
                "Automatic download is available only for the standard Fun-ASR-Nano Q4_K_M, Q5_K_M, and Q8_0 language model paths.")
        };

    private static IReadOnlyList<ModelDownloadItem> AddOptionalVad(
        IReadOnlyList<ModelDownloadItem> items,
        string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return items;
        }

        return [.. items, new ModelDownloadItem(FsmnVad, targetPath)];
    }

    private static IReadOnlyList<ModelDownloadItem> AddParaformerPunctuation(
        IReadOnlyList<ModelDownloadItem> items,
        ParaformerConfiguration configuration)
    {
        if (!configuration.UsePunctuation)
        {
            return items;
        }

        return
        [
            .. items,
            new ModelDownloadItem(
                ParaformerPunctuationInt8,
                RequirePath(configuration.PunctuationModelPath, "Paraformer punctuation model"))
        ];
    }

    private static IReadOnlyList<ModelDownloadItem> AddStreamingVad(
        IReadOnlyList<ModelDownloadItem> items,
        string targetPath) =>
        [.. items, new ModelDownloadItem(
            SileroVad,
            RequirePath(targetPath, "Streaming Silero VAD model"))];

    private static string RequirePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{description} path is not configured.");
        }

        return path;
    }

    private static string TokenizerTarget(string tokenizerPath, string fileName) =>
        Path.Combine(RequirePath(tokenizerPath, "Qwen3-ASR tokenizer directory"), fileName);
}
