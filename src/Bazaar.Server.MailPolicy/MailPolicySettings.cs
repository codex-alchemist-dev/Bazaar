using Bazaar.Server.Settings;

namespace Bazaar.Server.MailPolicy;

/// <summary>
/// The operator's mail choice keys (doc 22 settings table, doc 45 step 11): whether mail is on, and which back end. Both are
/// install scope, operator-changed, and default to the safest value: off, no back end. The operator domain is not here; it
/// is a <c>mail_domain</c> row (doc 22 data model) set in the mail panel after the flag is on. A back end may be chosen only
/// while mail is on, which is a rule across two keys and is enforced by the setup plan validator and the apply step.
/// </summary>
public static class MailPolicySettings
{
    public static readonly SettingKey<bool> Enabled = new(
        MailPolicySettingNames.Enabled, false, SettingScope.Install, SettingRole.Operator,
        "Mail is on. Off by default; turning it on needs a back end and an operator domain.");

    public static readonly SettingKey<MailBackendKind> Backend = new(
        MailPolicySettingNames.Backend, MailBackendKind.None, SettingScope.Install, SettingRole.Operator,
        "The mail back end: bazaar_client, imap_smtp, cloudflare or builtin_server. None while mail is off.");

    public static IReadOnlyList<SettingDefinition> All { get; } = [Enabled, Backend];
}
