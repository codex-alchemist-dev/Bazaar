namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>A pool host operation failed. The message never contains command arguments beyond the program name.</summary>
public class PoolHostException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public PoolHostException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    public PoolHostException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A command exited with a code outside its allowed list.</summary>
public sealed class PoolCommandFailedException : PoolHostException
{
    /// <summary>Creates the exception.</summary>
    public PoolCommandFailedException(string program, int exitCode, string stderr)
        : base($"{program} exited with code {exitCode}." + (stderr.Length == 0 ? string.Empty : " stderr: " + stderr))
    {
        Program = program;
        ExitCode = exitCode;
        StdErr = stderr;
    }

    /// <summary>The program that failed.</summary>
    public string Program { get; }

    /// <summary>The exit code.</summary>
    public int ExitCode { get; }

    /// <summary>The bounded stderr text.</summary>
    public string StdErr { get; }
}

/// <summary>The firewall could not be applied, or what is in force is not the expected set. The launch must not happen.</summary>
public sealed class PoolFirewallException : PoolHostException
{
    /// <summary>Creates the exception.</summary>
    public PoolFirewallException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    public PoolFirewallException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
