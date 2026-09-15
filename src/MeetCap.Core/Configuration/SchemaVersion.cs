namespace MeetCap.Core.Configuration;

/// <summary>
/// Canonical configuration schema version. Matches the <c>config_version</c> key in
/// <c>config.toml</c>. Breaking changes to the schema require a migration or an
/// actionable validation error (see docs/CONFIGURATION.md section 13).
/// </summary>
public static class SchemaVersion
{
    /// <summary>The only version supported by the current codebase.</summary>
    public const int Current = 1;
}
