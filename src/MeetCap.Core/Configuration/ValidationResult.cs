namespace MeetCap.Core.Configuration;

/// <summary>Outcome of validating a configuration.</summary>
public sealed class ValidationResult
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;

    public bool IsValid => _errors.Count == 0;

    public bool HasWarnings => _warnings.Count > 0;

    internal void AddError(string message) => _errors.Add(message);
    internal void AddWarning(string message) => _warnings.Add(message);

    public static ValidationResult Success() => new();
}
