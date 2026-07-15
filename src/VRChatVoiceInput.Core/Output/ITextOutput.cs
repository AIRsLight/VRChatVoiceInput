namespace VRChatVoiceInput.Core.Output;

public interface ITextOutput
{
    TextOutputTarget CaptureTarget();

    Task SendAsync(string text, TextOutputTarget target, CancellationToken cancellationToken = default);
}

public interface IStreamingTextOutput : ITextOutput
{
    bool SupportsStreamingOutput { get; }

    Task BeginStreamAsync(TextOutputTarget target, CancellationToken cancellationToken = default);

    Task UpdateStreamAsync(
        string accumulatedText,
        TextOutputTarget target,
        CancellationToken cancellationToken = default);

    Task CompleteStreamAsync(
        string finalText,
        TextOutputTarget target,
        CancellationToken cancellationToken = default);

    Task CancelStreamAsync(TextOutputTarget target, CancellationToken cancellationToken = default);
}

public sealed record TextOutputTarget(
    string Kind,
    string DisplayName,
    nint NativeWindowHandle = 0,
    int ProcessId = 0);
