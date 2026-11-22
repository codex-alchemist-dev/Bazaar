using Bazaar.Server.CompanyBuild.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The operator's company-build settings, read from the <c>Bazaar:CompanyBuild</c> configuration section (docs/design/37 section 4a).
/// They supply what the <c>server.identity</c> setup check compares: the company's output folder and the server's address and key pin.
/// Until the TLS component is wired into the host, the operator states the pin of the listener key here. It is a public hash, never a key.
/// </summary>
/// <remarks>
/// <c>CompanyId</c> and <c>ServerAddress</c> must both be present or the module registers nothing and the check reports not evaluated.
/// <c>ServerKeyHashPin</c> is required with them. <c>OutputRoot</c> defaults to <c>company-build</c> under <c>Bazaar:Host:DataRoot</c>.
/// A value that is present but malformed is a configuration error and stops startup, so a wrong pin never reads as "not set".
/// </remarks>
public static class CompanyBuildHostOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:CompanyBuild";

    /// <summary>Registers the output layout and the server identity source when the section is complete.</summary>
    /// <exception cref="InvalidOperationException">The section is partly filled in, or a value is malformed.</exception>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var companyId = Value(section["CompanyId"]);
        var address = Value(section["ServerAddress"]);
        var pin = Value(section["ServerKeyHashPin"]);
        if (companyId is null && address is null && pin is null)
            return;

        if (companyId is null || address is null || pin is null)
            throw new InvalidOperationException(
                $"{SectionName} needs CompanyId, ServerAddress and ServerKeyHashPin together, or none of them.");

        var dataRoot = Value(configuration["Bazaar:Host:DataRoot"]);
        var outputRoot = Value(section["OutputRoot"])
            ?? (dataRoot is null ? null : Path.Combine(dataRoot, "company-build"))
            ?? throw new InvalidOperationException($"{SectionName}:OutputRoot is required when Bazaar:Host:DataRoot is not set.");

        try
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                throw new ArgumentException($"{SectionName}:ServerAddress must be an absolute URL.");

            services.TryAddSingleton(new CompanyBuildOutputLayout(outputRoot, companyId));
            services.TryAddSingleton<IServerIdentitySource>(new StaticServerIdentitySource(uri, pin));
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"{SectionName} is not valid: {e.Message}", e);
        }
    }

    private static string? Value(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
