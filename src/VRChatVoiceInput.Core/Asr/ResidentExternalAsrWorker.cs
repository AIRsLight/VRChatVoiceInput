using System.Diagnostics;
using System.Text;

namespace VRChatVoiceInput.Core.Asr;

internal sealed class ResidentExternalAsrWorker : IDisposable
{
    private const string ReadyResponse = "READY";
    private const string TranscribeCommand = "TRANSCRIBE\t";
    private const string ResultResponse = "RESULT\t";
    private const string ErrorResponse = "ERROR\t";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly string _providerName;
    private readonly string _executablePath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stderrSync = new();
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private Task? _stderrPump;
    private bool _disposed;

    public ResidentExternalAsrWorker(
        string providerName,
        string executablePath,
        IEnumerable<string> arguments)
    {
        _providerName = providerName;
        _executablePath = executablePath;
        _arguments = arguments.ToArray();
        StartWorker();
    }

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
        string audioPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWorker();

            var process = _process!;
            ClearWorkerDetail();
            var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(audioPath)));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await process.StandardInput.WriteLineAsync(
                    $"{TranscribeCommand}{encodedPath}".AsMemory(),
                    cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                var response = await process.StandardOutput.ReadLineAsync(cancellationToken);
                stopwatch.Stop();

                if (response is null)
                {
                    throw CreateWorkerFailure($"{_providerName} worker exited before returning a result.");
                }

                if (response.StartsWith(ResultResponse, StringComparison.Ordinal))
                {
                    var text = DecodeResponse(response[ResultResponse.Length..]).Trim();
                    if (text.Length == 0)
                    {
                        throw new NoSpeechRecognizedException(
                            $"{_providerName} returned no transcription text. {GetWorkerDetail()}");
                    }

                    return new RecognitionResult(text, null, stopwatch.Elapsed);
                }

                if (response.StartsWith(ErrorResponse, StringComparison.Ordinal))
                {
                    var error = DecodeResponse(response[ErrorResponse.Length..]);
                    throw new InvalidOperationException(
                        $"{_providerName} recognition failed: {error}. {GetWorkerDetail()}");
                }

                throw CreateWorkerFailure(
                    $"{_providerName} worker returned an invalid response: {response}");
            }
            catch (OperationCanceledException)
            {
                StopWorker(graceful: false);
                throw;
            }
            catch (IOException exception)
            {
                var failure = CreateWorkerFailure(
                    $"{_providerName} worker communication failed.",
                    exception);
                StopWorker(graceful: false);
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
            StopWorker(graceful: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureWorker()
    {
        if (_process is null || _process.HasExited)
        {
            StopWorker(graceful: false);
            StartWorker();
        }
    }

    private void StartWorker()
    {
        var startInfo = ExternalProcessAsrRunner.CreateStartInfo(_executablePath);
        startInfo.RedirectStandardInput = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var argument in _arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--worker");

        lock (_stderrSync)
        {
            _stderr.Clear();
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Unable to start {_providerName} executable '{startInfo.FileName}'.");
        _process = process;
        _stderrPump = PumpStandardErrorAsync(process);

        try
        {
            var ready = process.StandardOutput.ReadLineAsync()
                .WaitAsync(StartupTimeout)
                .GetAwaiter()
                .GetResult();
            if (!string.Equals(ready, ReadyResponse, StringComparison.Ordinal))
            {
                WaitForExitedWorkerDiagnostics(process);
                throw CreateWorkerFailure(
                    $"The configured {_providerName} executable does not support the resident worker protocol.");
            }
        }
        catch
        {
            StopWorker(graceful: false);
            throw;
        }
    }

    private async Task PumpStandardErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrSync)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 4000)
                    {
                        _stderr.Remove(0, _stderr.Length - 4000);
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

    private void StopWorker(bool graceful)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited && !graceful)
            {
                TryKill(process);
            }
            else if (!process.HasExited)
            {
                process.StandardInput.WriteLine("QUIT");
                process.StandardInput.Flush();
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
        catch (IOException)
        {
            TryKill(process);
        }
        finally
        {
            try
            {
                _stderrPump?.Wait(ShutdownTimeout);
            }
            catch (AggregateException)
            {
            }

            _stderrPump = null;
            process.Dispose();
        }
    }

    private InvalidOperationException CreateWorkerFailure(
        string message,
        Exception? innerException = null)
    {
        var process = _process;
        var exit = process is { HasExited: true } ? $" Exit code: {process.ExitCode}." : string.Empty;
        return new InvalidOperationException($"{message}{exit} {GetWorkerDetail()}", innerException);
    }

    private string GetWorkerDetail()
    {
        lock (_stderrSync)
        {
            var detail = _stderr.ToString().Trim();
            return detail.Length == 0 ? "No worker diagnostics were reported." : detail;
        }
    }

    private void WaitForExitedWorkerDiagnostics(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.WaitForExit(500);
            }

            if (process.HasExited)
            {
                _stderrPump?.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or AggregateException)
        {
        }
    }

    private void ClearWorkerDetail()
    {
        lock (_stderrSync)
        {
            _stderr.Clear();
        }
    }

    private string DecodeResponse(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{_providerName} worker returned invalid Base64 data.",
                exception);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
