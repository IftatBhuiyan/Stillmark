namespace WinShot.Core.Startup;

public enum StartupPreference
{
    Unconfigured,
    Disabled,
    Enabled,
}

public enum StartupRegistrationAction
{
    None,
    Remove,
    WriteExpected,
}

/// <summary>
/// Determines registry repair without touching the registry. An installer must receive the
/// persisted preference explicitly; absence or a stale registration is never treated as consent.
/// A disabled preference removes only the exact owned command, never a mismatched same-name value.
/// </summary>
public static class StartupRegistrationPolicy
{
    public static StartupRegistrationAction Decide(
        StartupPreference preference,
        bool registrationExists,
        bool registrationMatches)
    {
        if (!Enum.IsDefined(preference))
            throw new ArgumentOutOfRangeException(nameof(preference));
        if (registrationMatches && !registrationExists)
        {
            throw new ArgumentException(
                "A missing registration cannot match the expected command.",
                nameof(registrationMatches));
        }

        return preference switch
        {
            StartupPreference.Unconfigured => StartupRegistrationAction.None,
            StartupPreference.Disabled when registrationMatches =>
                StartupRegistrationAction.Remove,
            StartupPreference.Disabled => StartupRegistrationAction.None,
            StartupPreference.Enabled when !registrationMatches =>
                StartupRegistrationAction.WriteExpected,
            _ => StartupRegistrationAction.None,
        };
    }
}
