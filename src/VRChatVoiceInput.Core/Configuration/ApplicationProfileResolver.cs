namespace VRChatVoiceInput.Core.Configuration;

public sealed class ApplicationProfileResolver
{
    private readonly IReadOnlyList<ApplicationProfileConfiguration> _profiles;
    private readonly ApplicationProfileConfiguration _defaultProfile;

    public ApplicationProfileResolver(
        IReadOnlyList<ApplicationProfileConfiguration> profiles,
        string defaultProfileId)
    {
        ProfileConfigurationValidator.Validate(profiles, defaultProfileId);
        _profiles = profiles;
        _defaultProfile = GetById(defaultProfileId);
    }

    public ApplicationProfileConfiguration Resolve(string? processName)
    {
        var normalizedProcessName = NormalizeProcessName(processName);
        if (!string.IsNullOrEmpty(normalizedProcessName))
        {
            var match = _profiles.FirstOrDefault(profile =>
                profile.Enabled && profile.Match.ProcessNames.Count > 0 &&
                profile.Match.ProcessNames.Any(name =>
                    string.Equals(
                        NormalizeProcessName(name),
                        normalizedProcessName,
                        StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                return match;
            }
        }

        if (_defaultProfile.Match.ProcessNames.Count == 0)
        {
            return _defaultProfile;
        }

        var foregroundProfile = _profiles.FirstOrDefault(profile =>
            profile.Enabled && profile.Match.ProcessNames.Count == 0);
        if (foregroundProfile is not null)
        {
            return foregroundProfile;
        }

        return _defaultProfile;
    }

    public ApplicationProfileConfiguration GetById(string profileId) =>
        _profiles.FirstOrDefault(profile =>
            profile.Enabled && string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Enabled application profile '{profileId}' was not found.");

    public static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(processName.Trim());
        return string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }
}
