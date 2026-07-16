using System.Text.Json.Serialization;

namespace VRChatVoiceInput.Core.Configuration;

public sealed class ProfilesConfiguration
{
    [JsonPropertyName("defaultProfileId")]
    public string DefaultProfileId { get; init; } = "desktop-default";

    [JsonPropertyName("items")]
    public IReadOnlyList<ApplicationProfileConfiguration> Items { get; init; } =
        Array.Empty<ApplicationProfileConfiguration>();
}

public sealed class ApplicationProfileConfiguration
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("builtIn")]
    public bool BuiltIn { get; init; }

    [JsonPropertyName("match")]
    public ApplicationMatchConfiguration Match { get; init; } = new();

    [JsonPropertyName("input")]
    public InputConfiguration Input { get; init; } = new();

    [JsonPropertyName("audio")]
    public ProfileAudioConfiguration Audio { get; init; } = new();

    [JsonPropertyName("recognition")]
    public ProfileRecognitionConfiguration Recognition { get; init; } = new();

    [JsonPropertyName("output")]
    public OutputConfiguration Output { get; init; } = new();
}

public sealed class ProfileAudioConfiguration
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("minimumDurationMs")]
    public int? MinimumDurationMs { get; init; }
}

public sealed class ApplicationMatchConfiguration
{
    [JsonPropertyName("processNames")]
    public IReadOnlyList<string> ProcessNames { get; init; } = Array.Empty<string>();
}

public sealed class ProfileRecognitionConfiguration
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; init; } = "auto";

    [JsonPropertyName("hotwords")]
    public IReadOnlyList<string> Hotwords { get; init; } = Array.Empty<string>();

    [JsonPropertyName("streamingEnabled")]
    public bool StreamingEnabled { get; init; }
}

internal static class ProfileConfigurationValidator
{
    private static readonly HashSet<string> SupportedInputModes = new(
        ["keyboard", "mouse", "xinput", "steamvr"],
        StringComparer.OrdinalIgnoreCase);

    public static void Validate(
        IReadOnlyList<ApplicationProfileConfiguration> profiles,
        string defaultProfileId)
    {
        if (profiles.Count == 0)
        {
            throw new InvalidOperationException("At least one application profile is required.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                throw new InvalidOperationException("Every application profile must have a non-empty name.");
            }
            if (!string.Equals(profile.Id, profile.Id.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Application profile name '{profile.Id}' cannot have leading or trailing whitespace.");
            }

            if (!ids.Add(profile.Id))
            {
                throw new InvalidOperationException($"Duplicate application profile name '{profile.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(profile.Recognition.Provider))
            {
                throw new InvalidOperationException($"Profile '{profile.Id}' must select an ASR provider.");
            }

            if (profile.Audio.MinimumDurationMs is <= 0)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' minimum recording duration must be greater than zero.");
            }

            var inputModes = profile.Input.GetEffectiveModes();
            if (inputModes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' must enable at least one input mode.");
            }

            if (inputModes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputModes.Count)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' contains duplicate input modes.");
            }

            var unsupportedInputMode = inputModes.FirstOrDefault(mode => !SupportedInputModes.Contains(mode));
            if (unsupportedInputMode is not null)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' has unsupported input mode '{unsupportedInputMode}'.");
            }

            if (inputModes.Contains("steamvr", StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(
                        profile.Input.SteamVr.ActionPath,
                        "/actions/voiceinput/in/ptt",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Profile '{profile.Id}' has an unsupported SteamVR action path.");
                }

                if (profile.Input.SteamVr.PollIntervalMs is < 1 or > 1000)
                {
                    throw new InvalidOperationException(
                        $"Profile '{profile.Id}' SteamVR poll interval must be between 1 and 1000 ms.");
                }
            }

            var hotwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hotword in profile.Recognition.Hotwords)
            {
                if (string.IsNullOrWhiteSpace(hotword))
                {
                    throw new InvalidOperationException($"Profile '{profile.Id}' contains an empty hotword.");
                }

                if (!hotwords.Add(hotword.Trim()))
                {
                    throw new InvalidOperationException(
                        $"Profile '{profile.Id}' contains duplicate hotword '{hotword}'.");
                }
            }

            var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var configuredName in profile.Match.ProcessNames)
            {
                var processName = ApplicationProfileResolver.NormalizeProcessName(configuredName);
                if (string.IsNullOrWhiteSpace(processName))
                {
                    throw new InvalidOperationException($"Profile '{profile.Id}' contains an empty process name.");
                }

                if (!processNames.Add(processName))
                {
                    throw new InvalidOperationException(
                        $"Profile '{profile.Id}' contains duplicate process name '{configuredName}'.");
                }
            }
        }

        var defaultProfile = profiles.FirstOrDefault(
            profile => string.Equals(profile.Id, defaultProfileId, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is null)
        {
            throw new InvalidOperationException($"Default profile '{defaultProfileId}' does not exist.");
        }

        if (!defaultProfile.Enabled)
        {
            throw new InvalidOperationException($"Default profile '{defaultProfileId}' is disabled.");
        }
    }
}
