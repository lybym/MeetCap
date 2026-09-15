namespace MeetCap.Core.Diagnostics;

/// <summary>
/// Base type for MeetCap errors that are expected operational outcomes rather than
/// defects. The CLI turns these into actionable messages and a non-zero exit code
/// (docs/DEVELOPMENT.md section 8) instead of a stack trace.
/// </summary>
public class MeetCapException : Exception
{
    public MeetCapException()
    {
    }

    public MeetCapException(string message)
        : base(message)
    {
    }

    public MeetCapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The configured (or default) capture device cannot be resolved, or capture could
/// not be kept alive because the device disappeared.
/// </summary>
/// <remarks>
/// docs/RELIABILITY.md section 8 requires device loss to be visible: this exception
/// is only thrown when capture could not be started or could not be recovered, and
/// the session records the transition either way.
/// </remarks>
public sealed class DeviceUnavailableException : MeetCapException
{
    public DeviceUnavailableException(string message)
        : base(message)
    {
    }

    public DeviceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The configured minimum free space is not available, so recording must not start
/// or must stop before the volume is exhausted.
/// </summary>
/// <remarks>
/// docs/RELIABILITY.md section 10: fail visibly, preserve already closed chunks, and
/// never auto-delete older recordings.
/// </remarks>
public sealed class InsufficientDiskSpaceException : MeetCapException
{
    public InsufficientDiskSpaceException(string message)
        : base(message)
    {
    }

    public InsufficientDiskSpaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A capture session could not be started, or had to end because the capture source
/// failed in a way that is not a recoverable device transition.
/// </summary>
public sealed class CaptureFailedException : MeetCapException
{
    public CaptureFailedException(string message)
        : base(message)
    {
    }

    public CaptureFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
