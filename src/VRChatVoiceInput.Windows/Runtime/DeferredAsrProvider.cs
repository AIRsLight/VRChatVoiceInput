using VRChatVoiceInput.Core.Asr;

namespace VRChatVoiceInput.Windows.Runtime;

internal sealed class DeferredAsrProvider : IAsrProvider, IExternalAsrProviderMetrics, IDisposable
{
    private readonly Func<IAsrProvider> _factory;
    private readonly object _sync = new();
    private IAsrProvider? _provider;
    private bool _disposed;

    public DeferredAsrProvider(
        string id,
        AsrProviderCapabilities capabilities,
        Func<IAsrProvider> factory)
    {
        Id = id;
        Capabilities = capabilities;
        _factory = factory;
    }

    public string Id { get; }

    public AsrProviderCapabilities Capabilities { get; }

    public long WorkingSetBytes
    {
        get
        {
            lock (_sync)
            {
                return _provider is IExternalAsrProviderMetrics metrics
                    ? metrics.WorkingSetBytes
                    : 0;
            }
        }
    }

    public Task<RecognitionResult> TranscribeAsync(
        AudioInput audio,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateProvider().TranscribeAsync(audio, options, cancellationToken);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (_provider as IDisposable)?.Dispose();
            _provider = null;
        }
    }

    private IAsrProvider GetOrCreateProvider()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _provider ??= _factory();
        }
    }
}
