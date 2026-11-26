namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>Settings for the <c>tpm</c> provider. Every member has a safe default; tests inject a runner and the systemd choice.</summary>
/// <param name="Runner">The command runner; null runs the real tools from PATH.</param>
/// <param name="Pcrs">PCR list for the systemd-creds binding (<c>Bazaar:Identity:TpmPcrs</c>), shipped default <c>7</c>.</param>
/// <param name="SystemdPresent">Overrides the systemd check; null looks for <c>/run/systemd/system</c>.</param>
public sealed record TpmKekOptions(ITpmToolRunner? Runner = null, string Pcrs = "7", bool? SystemdPresent = null);
