using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Asr;

public sealed class WhisperCppProvider : IAsrProvider, IExternalAsrProviderMetrics, IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly WhisperCppConfiguration _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _diagnosticsSync = new();
    private readonly StringBuilder _diagnostics = new();
    private Process? _process;
    private HttpClient? _client;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private bool _disposed;

    public WhisperCppProvider(WhisperCppConfiguration configuration)
    {
        _configuration = configuration;
        ValidateConfiguration();
        StartServer();
    }

    public string Id => "whisper-cpp";

    public AsrProviderCapabilities Capabilities => AsrProviderCapabilities.SegmentedStreaming;

    public long WorkingSetBytes
    {
        get
        {
            var process = _process;
            try
            {
                if (process is null || process.HasExited)
                {
                    return 0;
                }

                process.Refresh();
                return process.WorkingSet64;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    public async Task<RecognitionResult> TranscribeAsync(
        AudioInput audio,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RecognitionOptionsValidator.Validate(this, options);
        ExternalProcessAsrRunner.RequireFile(audio.FilePath, "Audio input");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureServer();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await using var stream = new FileStream(
                    Path.GetFullPath(audio.FilePath),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var form = new MultipartFormDataContent();
                using var audioContent = new StreamContent(stream);
                audioContent.Headers.ContentType = new("audio/wav");
                form.Add(audioContent, "file", Path.GetFileName(audio.FilePath));
                form.Add(new StringContent("json"), "response_format");

                using var response = await _client!.PostAsync("inference", form, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"whisper.cpp server returned HTTP {(int)response.StatusCode}: {body.Trim()}");
                }

                var result = JsonSerializer.Deserialize<WhisperServerResponse>(body);
                stopwatch.Stop();
                var text = TranscriptOutputParser.Parse(result?.Text ?? string.Empty);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException(
                        $"whisper.cpp returned no transcription text. {GetDiagnosticDetail()}");
                }

                return new RecognitionResult(text, null, stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                StopServer();
                throw;
            }
            catch (HttpRequestException exception)
            {
                var failure = CreateServerFailure("whisper.cpp server communication failed.", exception);
                StopServer();
                throw failure;
            }
            catch (IOException exception)
            {
                var failure = CreateServerFailure("whisper.cpp server communication failed.", exception);
                StopServer();
                throw failure;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopServer();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateConfiguration()
    {
        ExternalProcessAsrRunner.RequireFile(GetServerExecutablePath(), "Whisper server executable");
        ExternalProcessAsrRunner.RequireFile(_configuration.ModelPath, "Whisper model");
        if (_configuration.ThreadCount is < 0 or > 64)
        {
            throw new InvalidOperationException("Whisper threadCount must be between 0 and 64.");
        }

        if (_configuration.GpuDeviceIndex is < 0)
        {
            throw new InvalidOperationException("Whisper GPU device index cannot be negative.");
        }

        if (!string.IsNullOrWhiteSpace(_configuration.VadModelPath))
        {
            ExternalProcessAsrRunner.RequireFile(_configuration.VadModelPath, "Whisper VAD model");
        }
    }

    private string GetServerExecutablePath() => _configuration.UseGpu
        ? _configuration.VulkanServerExecutablePath
        : _configuration.ServerExecutablePath;

    private void EnsureServer()
    {
        if (_process is null || _process.HasExited)
        {
            StopServer();
            StartServer();
        }
    }

    private void StartServer()
    {
        var executablePath = GetServerExecutablePath();
        var port = ReserveLoopbackPort();
        var startInfo = ExternalProcessAsrRunner.CreateStartInfo(executablePath);
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(Path.GetFullPath(_configuration.ModelPath));
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(IPAddress.Loopback.ToString());
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(_configuration.Language))
        {
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(_configuration.Language);
        }

        if (_configuration.ThreadCount > 0)
        {
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(_configuration.ThreadCount.ToString(CultureInfo.InvariantCulture));
        }

        if (_configuration.UseGpu)
        {
            if (_configuration.GpuDeviceIndex is not null)
            {
                startInfo.ArgumentList.Add("--device");
                startInfo.ArgumentList.Add(
                    _configuration.GpuDeviceIndex.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            startInfo.ArgumentList.Add("--no-gpu");
        }

        if (!string.IsNullOrWhiteSpace(_configuration.VadModelPath))
        {
            startInfo.ArgumentList.Add("--vad");
            startInfo.ArgumentList.Add("--vad-model");
            startInfo.ArgumentList.Add(Path.GetFullPath(_configuration.VadModelPath));
        }

        lock (_diagnosticsSync)
        {
            _diagnostics.Clear();
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Unable to start whisper.cpp server executable '{startInfo.FileName}'.");
        _process = process;
        _stdoutPump = PumpOutputAsync(process.StandardOutput, process);
        _stderrPump = PumpOutputAsync(process.StandardError, process);
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://{IPAddress.Loopback}:{port}/"),
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            WaitForServerAsync(process, port).GetAwaiter().GetResult();
        }
        catch
        {
            StopServer();
            throw;
        }
    }

    private async Task WaitForServerAsync(Process process, int port)
    {
        using var timeout = new CancellationTokenSource(StartupTimeout);
        while (true)
        {
            if (process.HasExited)
            {
                throw CreateServerFailure("whisper.cpp server exited during startup.");
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException)
            {
                throw CreateServerFailure("Timed out waiting for whisper.cpp server startup.");
            }

            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task PumpOutputAsync(StreamReader reader, Process process)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_diagnosticsSync)
                {
                    _diagnostics.AppendLine(line);
                    if (_diagnostics.Length > 6000)
                    {
                        _diagnostics.Remove(0, _diagnostics.Length - 6000);
                    }
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void StopServer()
    {
        var process = _process;
        _process = null;
        _client?.Dispose();
        _client = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!process.HasExited && !process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
            }

            if (!process.HasExited)
            {
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            try
            {
                Task.WhenAll(
                    _stdoutPump ?? Task.CompletedTask,
                    _stderrPump ?? Task.CompletedTask).Wait(ShutdownTimeout);
            }
            catch (AggregateException)
            {
            }

            _stdoutPump = null;
            _stderrPump = null;
            process.Dispose();
        }
    }

    private InvalidOperationException CreateServerFailure(
        string message,
        Exception? innerException = null)
    {
        var process = _process;
        var exit = process is { HasExited: true } ? $" Exit code: {process.ExitCode}." : string.Empty;
        return new InvalidOperationException($"{message}{exit} {GetDiagnosticDetail()}", innerException);
    }

    private string GetDiagnosticDetail()
    {
        lock (_diagnosticsSync)
        {
            var detail = _diagnostics.ToString().Trim();
            return detail.Length == 0 ? "No server diagnostics were reported." : detail;
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record WhisperServerResponse(
        [property: JsonPropertyName("text")] string Text);
}
