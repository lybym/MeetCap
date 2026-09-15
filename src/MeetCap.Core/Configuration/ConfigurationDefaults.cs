namespace MeetCap.Core.Configuration;

/// <summary>
/// Produces the documented baseline configuration. Defaults live as property
/// initializers on the section types; this class is the single named entry point.
/// </summary>
public static class ConfigurationDefaults
{
    /// <summary>Returns a fresh, fully-defaulted configuration instance.</summary>
    public static MeetCapConfiguration Default() => new();
}
