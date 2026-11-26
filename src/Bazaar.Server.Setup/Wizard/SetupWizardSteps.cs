using System.Globalization;

namespace Bazaar.Server.Setup;

/// <summary>
/// The ordered question model of the setup wizard (doc 45 sections 1.3 and 1.4). A UI reads <see cref="Create"/> to draw the steps
/// and a progress bar; the engine walks the same list. Questions that depend on an earlier answer carry a
/// <see cref="WizardQuestion.Refine"/> hook.
/// </summary>
public static class SetupWizardSteps
{
    /// <summary>The module the install always has. It is added to the list the operator picks.</summary>
    public const string AlwaysModule = "Bazaar.GC";

    /// <summary>The shortest plan-hash prefix the operator may type to apply.</summary>
    public const int MinimumHashPrefix = 8;

    /// <summary>The id of the apply question that asks for the plan-hash prefix.</summary>
    public const string PlanHashQuestionId = "review.planHash";

    private const int _maxSlugLength = 63;
    private const int _maxPort = 65535;
    private const int _defaultPort = 8443;
    private const string _defaultBind = "loopback";
    private const string _defaultChannel = "beta";
    private const string _defaultSource = "blank";
    private const string _defaultProvider = "systemd-creds";
    private const string _windowsProvider = "dpapi";
    private const string _requiredMessage = "This answer is required.";

    private static readonly string[] _releaseChannels = ["alpha", "beta", "stable", "ltsc", "latest-commit"];
    private static readonly string[] _kekProviders = ["env", "file", "dpapi", "systemd-creds"];
    private static readonly string[] _statusBinds = ["loopback", "lan", "tailscale"];

    /// <summary>The modules the operator may add. Bazaar.GC is always present. The full catalogue is not settled.</summary>
    private static readonly string[] _optionalModules = ["Bazaar.Constellation", "Bazaar.Conduit", "Bazaar.KODO", "Bazaar.RyuseiFuda"];

    /// <summary>The steps, in the order they are asked.</summary>
    public static IReadOnlyList<WizardStep> Create() =>
    [
        new("company", "Company and owner",
        [
            new WizardQuestion
            {
                Id = "install.companyName",
                Prompt = "Company name",
                Kind = WizardQuestionKind.Text,
                PlanPath = "install.companyName",
                Validate = RequiredText,
            },
            new WizardQuestion
            {
                Id = "install.companySlug",
                Prompt = "Company slug: lower-case letters, digits and dashes. It cannot change later",
                Kind = WizardQuestionKind.Text,
                PlanPath = "install.companySlug",
                Hint = _slugMessage,
                Validate = text => IsSlug(text) ? null : _slugMessage,
                Refine = answers => SlugQuestion(answers),
            },
            new WizardQuestion
            {
                Id = "owner.displayName",
                Prompt = "Owner display name",
                Kind = WizardQuestionKind.Text,
                PlanPath = "owner.displayName",
                Validate = RequiredText,
            },
        ]),
        new("channel", "Release channel",
        [
            new WizardQuestion
            {
                Id = "install.releaseChannel",
                Prompt = "Release channel",
                Kind = WizardQuestionKind.Choice,
                PlanPath = "install.releaseChannel",
                Options = _releaseChannels,
                Default = _defaultChannel,
            },
        ]),
        new("custody", "Key custody",
        [
            new WizardQuestion
            {
                Id = "custody.kekProvider",
                Prompt = "Key-encryption key provider",
                Kind = WizardQuestionKind.Choice,
                PlanPath = "custody.kekProvider",
                Options = _kekProviders,
                Default = OperatingSystem.IsWindows() ? _windowsProvider : _defaultProvider,
            },
            new WizardQuestion
            {
                Id = "custody.kekRef",
                Prompt = "Key reference",
                Kind = WizardQuestionKind.Text,
                PlanPath = "custody.kekRef",
                Refine = answers => KekRefQuestion(answers),
            },
        ]),
        new("source", "Company source",
        [
            new WizardQuestion
            {
                Id = "company.source",
                Prompt = "Company source: blank, template:<id>@<semver>, or import:<id>",
                Kind = WizardQuestionKind.Text,
                PlanPath = "company.source",
                Default = _defaultSource,
                Hint = _sourceMessage,
                Validate = text => IsSource(text) ? null : _sourceMessage,
            },
        ]),
        new("audit", "Audit anchor",
        [
            new WizardQuestion
            {
                Id = "custody.auditAnchorTarget",
                Prompt = "Audit anchor URL (https), or press Enter for none; mounts stay disabled until one is set",
                Kind = WizardQuestionKind.Text,
                PlanPath = "custody.auditAnchorTarget",
                Default = string.Empty,
                Optional = true,
                EmptyMessage = "No audit anchor is set. Mounts stay disabled until one is set.",
            },
        ]),
        new("backups", "Backups",
        [
            new WizardQuestion
            {
                Id = "backups.target",
                Prompt = "Backup target, such as file:/mnt/backup/bazaar, or press Enter for none",
                Kind = WizardQuestionKind.Text,
                PlanPath = "backups.target",
                Default = string.Empty,
                Optional = true,
                IfEmpty = new WizardQuestion
                {
                    Id = "backups.acknowledgeNoTarget",
                    Prompt = "Finish without a backup target? This is recorded as an acknowledgement, and Kodo keeps flagging it.",
                    Kind = WizardQuestionKind.Confirm,
                    PlanPath = "backups.acknowledgeNoTarget",
                    Default = "no",
                    PlanValueWhenYes = "true",
                },
            },
        ]),
        new("secondFactor", "Second factor",
        [
            new WizardQuestion
            {
                Id = "owner.requireSecondFactor",
                Prompt = "Require a second factor (passkey or hardware key) to finish setup? Recovery and high-risk actions stay blocked until one is added.",
                Kind = WizardQuestionKind.Confirm,
                PlanPath = "owner.requireSecondFactor",
                Default = "yes",
                PlanValueWhenNo = "false",
                NoMessage = "Warning: setup will finish without a second factor. Recovery and high-risk actions stay blocked until one is added.",
            },
        ]),
        new("network", "Status listener",
        [
            new WizardQuestion
            {
                Id = "network.statusBind",
                Prompt = "Status listener bind. Anything but loopback needs owner approval",
                Kind = WizardQuestionKind.Choice,
                PlanPath = "network.statusBind",
                Options = _statusBinds,
                Default = _defaultBind,
            },
            new WizardQuestion
            {
                Id = "network.statusPort",
                Prompt = "Status listener port",
                Kind = WizardQuestionKind.Text,
                PlanPath = "network.statusPort",
                Default = _defaultPort.ToString(CultureInfo.InvariantCulture),
                Hint = _portMessage,
                Validate = text => ParsePort(text) is not null ? null : _portMessage,
            },
        ]),
        new("modules", "Modules",
        [
            new WizardQuestion
            {
                Id = "modules",
                Prompt = "Numbers to add, separated by commas, or press Enter for none",
                Kind = WizardQuestionKind.MultiChoice,
                PlanPath = "modules",
                Intro = "Optional modules. Bazaar.GC is always included.",
                Options = _optionalModules,
                JoinChoices = JoinModules,
            },
        ]),
    ];

    /// <summary>The apply question: the operator types the start of the plan hash the review printed, or Enter to stop.</summary>
    public static WizardQuestion PlanHashQuestion(string planHash)
    {
        ArgumentNullException.ThrowIfNull(planHash);
        var hint = planHash.Length > MinimumHashPrefix ? planHash[..MinimumHashPrefix] + "..." : planHash;
        return new WizardQuestion
        {
            Id = PlanHashQuestionId,
            Prompt = $"Apply this plan: type the first {MinimumHashPrefix} or more characters of its plan hash ({hint}), or press Enter to stop",
            Kind = WizardQuestionKind.Text,
            Optional = true,
        };
    }

    /// <summary>True when <paramref name="typed"/> is at least <see cref="MinimumHashPrefix"/> hex characters and starts the plan hash.</summary>
    public static bool MatchesHashPrefix(string typed, string planHash)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(planHash);
        return typed.Length >= MinimumHashPrefix
            && typed.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))
            && planHash.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The module list as a JSON array of the chosen names, with Bazaar.GC first. Names are constants, so no escaping is needed.</summary>
    private static string JoinModules(IReadOnlyList<string> chosen)
    {
        var all = new List<string> { AlwaysModule };
        all.AddRange(chosen.Where(module => module != AlwaysModule));
        return "[" + string.Join(",", all.Select(module => "\"" + module + "\"")) + "]";
    }

    private const string _slugMessage = "The slug must be lower-case letters, digits and dashes, starting with a letter or digit, up to 63 characters.";

    private const string _sourceMessage = "The source must be blank, template:<id>@<semver>, or import:<id>.";

    private static readonly string _portMessage = $"The port must be a whole number from 1 to {_maxPort}.";

    private static string? RequiredText(string text) => text.Length > 0 ? null : _requiredMessage;

    private static bool IsSource(string text) =>
        text == _defaultSource || text.StartsWith("template:", StringComparison.Ordinal) || text.StartsWith("import:", StringComparison.Ordinal);

    private static int? ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= _maxPort ? port : null;

    private static WizardQuestion SlugQuestion(IReadOnlyDictionary<string, string> answers)
    {
        var suggested = answers.TryGetValue("install.companyName", out var name) ? SlugOf(name) : string.Empty;
        return Find("install.companySlug") with { Default = suggested.Length == 0 ? null : suggested, Refine = null };
    }

    private static WizardQuestion KekRefQuestion(IReadOnlyDictionary<string, string> answers)
    {
        var provider = answers.TryGetValue("custody.kekProvider", out var chosen) ? chosen : _defaultProvider;
        var family = FamilyOf(provider);
        var message = $"The reference must use the {provider} form, and must not be a literal value.";
        return Find("custody.kekRef") with
        {
            Prompt = $"Key reference for {provider}, such as {ExampleReference(provider)}",
            Hint = message,
            Validate = text => SecretReferences.IsValid(text, [family]) ? null : message,
            Refine = null,
        };
    }

    /// <summary>The unrefined question with this id, from a fresh list. Used by the refine hooks, so a hook never loops on itself.</summary>
    private static WizardQuestion Find(string id) =>
        Create().SelectMany(step => step.Questions).Single(question => question.Id == id);

    /// <summary>The lower-case slug of a company name, or "" when nothing usable remains.</summary>
    internal static string SlugOf(string name)
    {
        var chars = new List<char>();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                chars.Add(c);
            }
            else if (chars.Count > 0 && chars[^1] != '-')
            {
                chars.Add('-');
            }
        }

        var slug = new string(chars.ToArray()).TrimEnd('-');
        return slug.Length > _maxSlugLength ? slug[.._maxSlugLength].TrimEnd('-') : slug;
    }

    /// <summary>Lower-case letters, digits and dashes, starting with a letter or digit, at most 63 characters. The plan validator has the final word.</summary>
    private static bool IsSlug(string text) =>
        text.Length is >= 1 and <= _maxSlugLength
        && text[0] is (>= 'a' and <= 'z') or (>= '0' and <= '9')
        && text.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static SecretReferenceFamily FamilyOf(string provider) => provider switch
    {
        "env" => SecretReferenceFamily.Env,
        "file" => SecretReferenceFamily.File,
        "dpapi" => SecretReferenceFamily.Dpapi,
        "systemd-creds" => SecretReferenceFamily.SystemdCreds,
        _ => SecretReferenceFamily.None,
    };

    private static string ExampleReference(string provider) => provider switch
    {
        "env" => "env:BAZAAR_KEK",
        "file" => "file:/etc/bazaar/kek",
        "dpapi" => "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
        _ => "systemd-creds:bazaar-kek",
    };
}
