namespace Bazaar.Server.Setup;

/// <summary>
/// The optional "Remote access" step of the setup wizard (doc 45 step 15b, PROPOSED, built on doc 39). Off by default: answering
/// nothing writes nothing, so a new install keeps no inbound internet connection (doc 03). Only a Cloudflare tunnel is offered here,
/// because it needs no router change and no extra module; the other Conduit modules are set later in the admin panel. Each answer is
/// written to a Conduit setting key (<c>settings.conduit.*</c>), so the plan validator holds the change for owner approval and the
/// registry floors apply as for any setting. The wizard shows the plaintext warning before it asks.
/// </summary>
public static class RemoteAccessStep
{
    /// <summary>The step id the progress bar and tests use.</summary>
    public const string StepId = "remote";

    private const string _enabledPath = "settings.conduit.cloudflared.enabled";
    private const string _modePath = "settings.conduit.cloudflared.mode";
    private const string _hostnamePath = "settings.conduit.cloudflared.hostname";
    private const string _named = "named";
    private const string _quick = "quick";
    private const string _modeMessage = "Answer named or quick.";
    private const string _hostMessage = "Give a host name such as bazaar.example.com.";

    /// <summary>The step, ready to append to the wizard's step list.</summary>
    public static WizardStep Create() => new(StepId, "Remote access", Questions());

    private static IReadOnlyList<WizardQuestion> Questions() =>
    [
        new WizardQuestion
        {
            Id = "remote.cloudflared",
            Prompt = "Let clients reach this server from outside through a Cloudflare tunnel? Cloudflare ends the encrypted connection and can see the traffic. Turning it on needs the owner's approval.",
            Kind = WizardQuestionKind.Confirm,
            PlanPath = _enabledPath,
            Default = "no",
            PlanValueWhenYes = "true",
            Intro = "Remote access is off by default: no inbound internet connection. Other ways in (relay, overlay, port forward) are set later in the admin panel.",
        },
        new WizardQuestion
        {
            Id = "remote.cloudflaredMode",
            Prompt = "Tunnel kind: named (your own domain, recommended) or quick (no account, the address changes on every start). Press Enter to skip or to keep named",
            Kind = WizardQuestionKind.Text,
            PlanPath = _modePath,
            Optional = true,
            Hint = _modeMessage,
            Validate = text => text is _named or _quick ? null : _modeMessage,
        },
        new WizardQuestion
        {
            Id = "remote.cloudflaredHostname",
            Prompt = "Tunnel host name for a named tunnel, or press Enter to skip. The tunnel token is never asked here; add it later as a secret",
            Kind = WizardQuestionKind.Text,
            PlanPath = _hostnamePath,
            Optional = true,
            Hint = _hostMessage,
            Validate = text => Uri.CheckHostName(text) == UriHostNameType.Dns ? null : _hostMessage,
        },
    ];
}
