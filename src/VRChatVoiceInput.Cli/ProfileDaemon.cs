using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Windows.Runtime;

internal sealed class ProfileDaemon
{
    private readonly ProfileRuntimeHost _host;

    public ProfileDaemon(
        AppConfiguration configuration,
        string? profileOverride,
        string? providerOverride)
    {
        _host = new ProfileRuntimeHost(configuration, profileOverride, providerOverride);
        _host.LogReceived += (_, entry) =>
        {
            var profile = entry.ProfileId is null ? string.Empty : $" [{entry.ProfileId}]";
            Console.WriteLine($"[{entry.Timestamp:HH:mm:ss}]{profile} {entry.Code}: {entry.Message}");
        };
    }

    public async Task RunAsync()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            exit.TrySetResult();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await _host.StartAsync();
            Console.WriteLine("Ready. The foreground application selects the active profile; press Ctrl+C to exit.");
            await exit.Task;
        }
        finally
        {
            await _host.DisposeAsync();
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
