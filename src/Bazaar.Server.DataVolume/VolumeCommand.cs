using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.DataVolume;

/// <summary>
/// <c>bazaar-server volume open|close|status</c>, the privileged volume unit (doc 63 section 4). It reads the descriptor,
/// opens the existing KEK (never creates one), unwraps the volume key, calls the OS operator and clears the key. It has no
/// other verbs and no force option. Output names a refusal code and a one-line reason, never key material.
/// </summary>
public static class VolumeCommand
{
    /// <summary>Usage error (<c>EX_USAGE</c>).</summary>
    public const int UsageExitCode = 64;

    /// <summary><c>status</c> found the volume not mounted, not encrypted or without protection.</summary>
    public const int NotProtectedExitCode = 3;

    /// <summary>The operator failed, or <c>close</c> found the volume busy.</summary>
    public const int FailedExitCode = 1;

    private const string _usage = "usage: volume open | volume close | volume status";

    /// <param name="openKek">Opens the existing KEK (open only, never create). Disposed after use when it is disposable.</param>
    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        IEnumerable<IDataVolumeOperator> operators,
        IEnumerable<IDataVolumeProvider> providers,
        Func<IKekProvider> openKek,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(operators);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(openKek);
        ArgumentNullException.ThrowIfNull(output);

        var verb = args.Length == 1 ? args[0].ToLowerInvariant() : string.Empty;
        if (verb is not ("open" or "close" or "status"))
        {
            await output.WriteLineAsync(_usage, CancellationToken.None);
            return UsageExitCode;
        }

        try
        {
            var options = ReadOptions(configuration);
            var descriptor = await ReadDescriptorAsync(options, cancellationToken);
            if (verb == "status")
                return await StatusAsync(descriptor, options, providers, output, cancellationToken);

            RequireSameIdentity(descriptor, options);

            // A development instance has no volume to open or close; the descriptor, not the process, decides (doc 63 section 8).
            if (string.Equals(descriptor.ProviderKind, DevPlainDataVolumeProvider.ProviderKind, StringComparison.Ordinal))
            {
                if (descriptor.InstanceClass != InstanceClass.Development)
                    throw new DataVolumeRefusedException(DataVolumeRefusal.NotEncrypted, "The dev-plain provider is accepted only for a development instance.");

                await output.WriteLineAsync($"volume {verb}: dev-plain has no volume, nothing to do", CancellationToken.None);
                return 0;
            }

            var volumeOperator = operators.FirstOrDefault(o => string.Equals(o.Kind, descriptor.ProviderKind, StringComparison.Ordinal))
                ?? throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"The volume provider '{descriptor.ProviderKind}' is not available on this host.");

            return verb == "open"
                ? await OpenAsync(descriptor, options, volumeOperator, openKek, output, cancellationToken)
                : await CloseAsync(descriptor, options, volumeOperator, output, cancellationToken);
        }
        catch (DataVolumeRefusedException e)
        {
            await output.WriteLineAsync($"volume {verb} refused: {e.Code}: {OneLine(e.Message)}", CancellationToken.None);
            return DataVolumeRefusedException.ExitCode;
        }
    }

    private static async Task<int> OpenAsync(
        DataVolumeDescriptor descriptor, DataVolumeOptions options, IDataVolumeOperator volumeOperator, Func<IKekProvider> openKek,
        TextWriter output, CancellationToken cancellationToken)
    {
        byte[]? key = null;
        try
        {
            key = await UnwrapAsync(descriptor, openKek, cancellationToken);
            await volumeOperator.OpenAsync(descriptor, options.DataRoot, key, cancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            await output.WriteLineAsync($"volume open failed: {OneLine(e.Message)}", CancellationToken.None);
            return FailedExitCode;
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }

        await output.WriteLineAsync("volume open: ok", CancellationToken.None);
        return 0;
    }

    private static async Task<int> CloseAsync(
        DataVolumeDescriptor descriptor, DataVolumeOptions options, IDataVolumeOperator volumeOperator, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            await volumeOperator.CloseAsync(descriptor, options.DataRoot, cancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // A busy volume. There is no force option: a forced lock under a running PostgreSQL kills the cluster (doc 63 section 3.1).
            await output.WriteLineAsync($"volume close failed: {OneLine(e.Message)}", CancellationToken.None);
            return FailedExitCode;
        }

        await output.WriteLineAsync("volume close: ok", CancellationToken.None);
        return 0;
    }

    private static async Task<int> StatusAsync(
        DataVolumeDescriptor descriptor, DataVolumeOptions options, IEnumerable<IDataVolumeProvider> providers, TextWriter output, CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(p => string.Equals(p.Kind, descriptor.ProviderKind, StringComparison.Ordinal))
            ?? throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"The volume provider '{descriptor.ProviderKind}' is not available on this host.");

        var status = await provider.InspectAsync(options.DataRoot, cancellationToken);
        var report = new VolumeStatusReport(status.Mounted, status.VolumeId, status.Encrypted, status.ProtectionOn, status.Cipher);
        await output.WriteLineAsync(JsonSerializer.Serialize(report, VolumeCommandJsonContext.Default.VolumeStatusReport), CancellationToken.None);
        return status.Mounted && status.Encrypted && status.ProtectionOn ? 0 : NotProtectedExitCode;
    }

    private static DataVolumeOptions ReadOptions(IConfiguration configuration)
    {
        DataVolumeOptions options;
        try
        {
            options = DataVolumeComponent.ReadOptions(configuration);
        }
        catch (InvalidOperationException e)
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, e.Message, e);
        }

        if (string.IsNullOrWhiteSpace(options.BootRoot) || string.IsNullOrWhiteSpace(options.DataRoot))
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"{DataVolumeOptions.BootRootKey} and {DataVolumeOptions.DataRootKey} must both be set.");

        return options;
    }

    private static async Task<DataVolumeDescriptor> ReadDescriptorAsync(DataVolumeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await DescriptorStore.ReadAsync(options.DescriptorPath, cancellationToken)
                ?? throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, $"No volume descriptor at '{options.DescriptorPath}'. Run setup, or mount the boot root.");
        }
        catch (InvalidDataException e)
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.IdentityMismatch, "The volume descriptor is damaged.", e);
        }
    }

    /// <summary>When host config names an install or volume id it must equal the descriptor's; an id left blank is not checked here.</summary>
    private static void RequireSameIdentity(DataVolumeDescriptor descriptor, DataVolumeOptions options)
    {
        if ((!string.IsNullOrWhiteSpace(options.InstallId) && !string.Equals(descriptor.InstallId, options.InstallId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(options.VolumeId) && !string.Equals(descriptor.VolumeId, options.VolumeId, StringComparison.Ordinal)))
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.IdentityMismatch, "The volume descriptor belongs to another install or volume than host config names.");
        }
    }

    private static async Task<byte[]> UnwrapAsync(DataVolumeDescriptor descriptor, Func<IKekProvider> openKek, CancellationToken cancellationToken)
    {
        IKekProvider kek;
        try
        {
            kek = openKek();
        }
        catch (KekMissingException e)
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, "The KEK is missing. The volume unit never creates one.", e);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidDataException or IOException or InvalidOperationException)
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.NotInitialised, "The KEK could not be opened.", e);
        }

        try
        {
            if (!string.Equals(descriptor.KekId, kek.KekId, StringComparison.Ordinal))
                throw new DataVolumeRefusedException(DataVolumeRefusal.IdentityMismatch, "The descriptor was wrapped under another KEK than the one opened.");

            return await VolumeKeyCustody.UnwrapVolumeKeyAsync(kek, descriptor, cancellationToken);
        }
        catch (KekUnwrapException e)
        {
            throw new DataVolumeRefusedException(DataVolumeRefusal.IdentityMismatch, "The volume descriptor does not unwrap under this host's KEK.", e);
        }
        finally
        {
            (kek as IDisposable)?.Dispose();
        }
    }

    private static string OneLine(string message) => message.ReplaceLineEndings(" ").Trim();
}

/// <summary>The one JSON line <c>volume status</c> prints.</summary>
public sealed record VolumeStatusReport(bool Mounted, string? VolumeId, bool Encrypted, bool ProtectionOn, string? Cipher);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VolumeStatusReport))]
internal sealed partial class VolumeCommandJsonContext : JsonSerializerContext;
