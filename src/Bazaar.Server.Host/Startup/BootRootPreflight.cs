using Bazaar.Server.DataVolume;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Checks, before the host is built, that the KEK of a doc 63 install can be opened. The service graph opens the KEK while it
/// is being built, so a missing key would otherwise end the process with an unhandled exception instead of the refusal doc 63
/// section 5 asks for: one line that names the reason and exit code 78. Nothing is created here.
/// </summary>
public static class BootRootPreflight
{
    /// <summary>Returns 0 when the start may go on, otherwise the exit code after writing the refusal line.</summary>
    public static int Check(IConfiguration configuration, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        if (!DataVolumeCompositionExtensions.HasBootRoot(configuration))
        {
            return 0;
        }

        try
        {
            (KekComponent.OpenExisting(configuration) as IDisposable)?.Dispose();
            return 0;
        }
        catch (KekMissingException e)
        {
            var refusal = new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, e.Message, e);
            output.WriteLine($"Data volume check refused the start ({refusal.Code}): {refusal.Message}");
            return DataVolumeRefusedException.ExitCode;
        }
    }
}
