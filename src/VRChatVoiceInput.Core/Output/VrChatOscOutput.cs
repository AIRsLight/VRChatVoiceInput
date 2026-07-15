using System.Net;
using System.Net.Sockets;
using System.Text;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Output;

public sealed class VrChatOscOutput : IStreamingTextOutput, IDisposable
{
    private readonly UdpClient _udpClient = new();
    private readonly IPEndPoint _endpoint;
    private readonly VrChatConfiguration _configuration;

    public VrChatOscOutput(VrChatConfiguration configuration)
    {
        _configuration = configuration;
        _endpoint = new IPEndPoint(IPAddress.Parse(configuration.Host), configuration.Port);
    }

    public TextOutputTarget CaptureTarget() => new("vrchat-osc", "VRChat OSC Chatbox");

    public bool SupportsStreamingOutput => _configuration.SendImmediately;

    public async Task SendAsync(string text, TextOutputTarget target, CancellationToken cancellationToken = default)
    {
        foreach (var chunk in TextChunker.Split(text, _configuration.MaxChatboxCharacters))
        {
            var message = OscChatboxMessage.Create(chunk, _configuration.SendImmediately);
            await _udpClient.SendAsync(message, _endpoint, cancellationToken);
        }
    }

    public Task BeginStreamAsync(TextOutputTarget target, CancellationToken cancellationToken = default) =>
        SendPacketAsync(OscTypingMessage.Create(isTyping: true), cancellationToken);

    public Task UpdateStreamAsync(
        string accumulatedText,
        TextOutputTarget target,
        CancellationToken cancellationToken = default)
    {
        var visibleText = TextChunker.Tail(accumulatedText, _configuration.MaxChatboxCharacters);
        return visibleText.Length == 0
            ? Task.CompletedTask
            : SendPacketAsync(
                OscChatboxMessage.Create(visibleText, sendImmediately: true, notificationSfx: false),
                cancellationToken);
    }

    public async Task CompleteStreamAsync(
        string finalText,
        TextOutputTarget target,
        CancellationToken cancellationToken = default)
    {
        var visibleText = TextChunker.Tail(finalText, _configuration.MaxChatboxCharacters);
        if (visibleText.Length > 0)
        {
            await SendPacketAsync(
                OscChatboxMessage.Create(visibleText, sendImmediately: true, notificationSfx: true),
                cancellationToken);
        }

        await SendPacketAsync(OscTypingMessage.Create(isTyping: false), cancellationToken);
    }

    public Task CancelStreamAsync(TextOutputTarget target, CancellationToken cancellationToken = default) =>
        SendPacketAsync(OscTypingMessage.Create(isTyping: false), cancellationToken);

    private Task SendPacketAsync(byte[] message, CancellationToken cancellationToken) =>
        _udpClient.SendAsync(message, _endpoint, cancellationToken).AsTask();

    public void Dispose() => _udpClient.Dispose();
}

public static class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == maximumCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
                count = 0;
            }

            current.Append(rune);
            count++;
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(chunk => chunk.Length > 0).ToArray();
    }

    public static string Tail(string text, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        var runes = text.Trim().EnumerateRunes().ToArray();
        return runes.Length <= maximumCharacters
            ? text.Trim()
            : string.Concat(runes[^maximumCharacters..]);
    }
}

public static class OscChatboxMessage
{
    public static byte[] Create(string text, bool sendImmediately, bool? notificationSfx = null)
    {
        using var stream = new MemoryStream();
        WritePaddedString(stream, "/chatbox/input");
        var typeTags = sendImmediately ? ",sT" : ",sF";
        if (notificationSfx.HasValue)
        {
            typeTags += notificationSfx.Value ? "T" : "F";
        }

        WritePaddedString(stream, typeTags);
        WritePaddedString(stream, text);
        return stream.ToArray();
    }

    private static void WritePaddedString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}

public static class OscTypingMessage
{
    public static byte[] Create(bool isTyping)
    {
        using var stream = new MemoryStream();
        WritePaddedString(stream, "/chatbox/typing");
        WritePaddedString(stream, isTyping ? ",T" : ",F");
        return stream.ToArray();
    }

    private static void WritePaddedString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}
