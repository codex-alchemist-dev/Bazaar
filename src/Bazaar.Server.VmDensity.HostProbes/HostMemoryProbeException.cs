namespace Bazaar.Server.VmDensity.HostProbes;

/// <summary>
/// Thrown when host memory cannot be read or the platform data is malformed. The governor catches probe
/// exceptions, records a probe-failed event and admits the VM without density control, so a bad reading is
/// never turned into a made-up number.
/// </summary>
public sealed class HostMemoryProbeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong.</param>
    public HostMemoryProbeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="innerException">The cause.</param>
    public HostMemoryProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
