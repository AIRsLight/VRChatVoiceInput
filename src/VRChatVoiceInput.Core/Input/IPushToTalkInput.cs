namespace VRChatVoiceInput.Core.Input;

public interface IPushToTalkInput
{
    event EventHandler<PushToTalkChangedEventArgs>? StateChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record PushToTalkChangedEventArgs(bool IsPressed, DateTimeOffset Timestamp);
