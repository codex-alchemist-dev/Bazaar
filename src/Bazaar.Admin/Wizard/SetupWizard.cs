using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>The outcome of one wizard run: the exit code, the <c>path=value</c> answers, and the plan hash once a review ran.</summary>
public sealed record SetupWizardResult(int ExitCode, IReadOnlyList<string> SetArguments, string? PlanHash);

/// <summary>
/// The interactive setup wizard (doc 45 section 1.1): plain prompts, one question at a time, keyboard only. Each answer becomes a
/// <c>path=value</c> flag, and the whole set goes through the same overlay, validation, dry run and apply as <c>setup --set</c>
/// (<see cref="SetupCommands"/>). So a wizard run and a flag run with the same answers give the same plan hash. The wizard never
/// writes a file. It applies only when the operator types the start of the plan hash that the review printed.
/// </summary>
internal sealed class SetupWizard(ISetupPrompter prompter, IReadOnlyDictionary<string, string?> environment, ICliApplyGate applyGate)
{
    private const string _alwaysModule = "Bazaar.GC";
    private const int _minimumHashPrefix = 8;
    private const int _maxSlugLength = 63;
    private const int _maxPort = 65535;
    private const int _defaultPort = 8443;
    private const string _defaultBind = "loopback";
    private const string _defaultChannel = "beta";
    private const string _defaultSource = "blank";
    private const string _defaultProvider = "systemd-creds";
    private const string _windowsProvider = "dpapi";

    private static readonly string[] _releaseChannels = ["alpha", "beta", "stable", "ltsc", "latest-commit"];
    private static readonly string[] _kekProviders = ["env", "file", "dpapi", "systemd-creds"];
    private static readonly string[] _statusBinds = ["loopback", "lan", "tailscale"];

    /// <summary>The modules the operator may add. Bazaar.GC is always present. The full catalogue is not settled (see the report).</summary>
    private static readonly string[] _optionalModules = ["Bazaar.Constellation", "Bazaar.Conduit", "Bazaar.KODO", "Bazaar.RyuseiFuda"];

    private readonly List<string> _sets = [];

    /// <summary>Runs the questions, then the review, then the optional apply. Ends early (exit 1) when the input ends.</summary>
    public async Task<SetupWizardResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunQuestionsAndReviewAsync(cancellationToken);
        }
        catch (InputEndedException)
        {
            prompter.Say("The wizard stopped before the review. Nothing was changed.");
            return Finish((int)SetupExitCode.InvalidInput, null);
        }
    }

    private async Task<SetupWizardResult> RunQuestionsAndReviewAsync(CancellationToken cancellationToken)
    {
        prompter.Say("Bazaar server setup. Answer each question. Press Enter to take the value in brackets.");
        prompter.Say("Nothing is written until the review is shown and you type the start of its plan hash.");

        // The plan's schema version is not a question. It is set the way every door sets it, so the plan hash matches.
        Set("schemaVersion", SetupConstants.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var name = RequiredText("Company name", null);
        Set("install.companyName", name);
        Set("install.companySlug", RequiredSlug(SlugOf(name)));
        Set("owner.displayName", RequiredText("Owner display name", null));
        Set("install.releaseChannel", Choice("Release channel", _releaseChannels, _defaultChannel));

        var provider = Choice("Key-encryption key provider", _kekProviders, DefaultProvider());
        Set("custody.kekProvider", provider);
        Set("custody.kekRef", RequiredReference(provider));

        Set("company.source", SourceAnswer());
        AskAuditAnchor();
        AskBackupTarget();
        AskSecondFactor();

        Set("network.statusBind", Choice("Status listener bind. Anything but loopback needs owner approval", _statusBinds, _defaultBind));
        Set("network.statusPort", PortAnswer());
        Set("modules", ModulesAnswer());

        return await ReviewAndApplyAsync(cancellationToken);
    }

    /// <summary>Shows the same checks as <c>setup --dry-run</c>, then offers the apply. Holds and errors stop before the offer.</summary>
    private async Task<SetupWizardResult> ReviewAndApplyAsync(CancellationToken cancellationToken)
    {
        prompter.Say(string.Empty);
        prompter.Say("Review. These are the same checks as setup validate and setup --dry-run. Nothing has been written.");
        var dryRun = SetupCommands.DryRun(null, _sets, environment);
        Emit(dryRun);

        if (dryRun.ExitCode == (int)SetupExitCode.Held)
        {
            prompter.Say("A held item needs approval before it can apply. Nothing was changed.");
            return Finish(dryRun.ExitCode, dryRun.PlanHash);
        }

        if (dryRun.ExitCode != (int)SetupExitCode.Ok)
        {
            prompter.Say("Fix the errors above and run the wizard again. Nothing was changed.");
            return Finish(dryRun.ExitCode, dryRun.PlanHash);
        }

        var typed = Ask($"Apply this plan: type the first {_minimumHashPrefix} or more characters of its plan hash ({PrefixHint(dryRun.PlanHash!)}), or press Enter to stop", null);
        if (typed.Length == 0)
        {
            prompter.Say("Stopped without applying. Nothing was changed.");
            return Finish((int)SetupExitCode.Ok, dryRun.PlanHash);
        }

        if (!MatchesHashPrefix(typed, dryRun.PlanHash!))
        {
            prompter.Say("The text does not match the start of the plan hash. Nothing was applied. Run the wizard again to review the plan.");
            return Finish((int)SetupExitCode.InvalidInput, dryRun.PlanHash);
        }

        var applied = await SetupCommands.ApplyAsync(null, _sets, environment, dryRun.PlanHash!, applyGate, cancellationToken, SetupEntryPath.Wizard);
        Emit(applied);
        return Finish(applied.ExitCode, applied.PlanHash);
    }

    private void AskAuditAnchor()
    {
        var anchor = Text("Audit anchor URL (https), or press Enter for none; mounts stay disabled until one is set", "");
        if (anchor.Length > 0)
        {
            Set("custody.auditAnchorTarget", anchor);
        }
        else
        {
            prompter.Say("No audit anchor is set. Mounts stay disabled until one is set.");
        }
    }

    /// <summary>Asks until there is a backup target or a recorded acknowledgement that the install has none (doc 45 section 1.4).</summary>
    private void AskBackupTarget()
    {
        while (true)
        {
            var target = Text("Backup target, such as file:/mnt/backup/bazaar, or press Enter for none", "");
            if (target.Length > 0)
            {
                Set("backups.target", target);
                return;
            }

            if (YesNo("Finish without a backup target? This is recorded as an acknowledgement, and Kodo keeps flagging it. (yes/no)", defaultValue: false))
            {
                Set("backups.acknowledgeNoTarget", "true");
                return;
            }
        }
    }

    private void AskSecondFactor()
    {
        var required = YesNo("Require a second factor (passkey or hardware key) to finish setup? Recovery and high-risk actions stay blocked until one is added. (yes/no)", defaultValue: true);
        if (!required)
        {
            Set("owner.requireSecondFactor", "false");
            prompter.Say("Warning: setup will finish without a second factor. Recovery and high-risk actions stay blocked until one is added.");
        }
    }

    private string SourceAnswer()
    {
        while (true)
        {
            var source = Text("Company source: blank, template:<id>@<semver>, or import:<id>", _defaultSource);
            if (source == _defaultSource || source.StartsWith("template:", StringComparison.Ordinal) || source.StartsWith("import:", StringComparison.Ordinal))
            {
                return source;
            }

            prompter.Say("The source must be blank, template:<id>@<semver>, or import:<id>.");
        }
    }

    private string PortAnswer()
    {
        while (true)
        {
            var text = Text("Status listener port", _defaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= _maxPort)
            {
                return port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            prompter.Say($"The port must be a whole number from 1 to {_maxPort}.");
        }
    }

    /// <summary>The module list as a JSON array of the chosen names, with Bazaar.GC first. Names are constants, so no escaping is needed.</summary>
    private string ModulesAnswer()
    {
        prompter.Say("Optional modules. Bazaar.GC is always included.");
        for (var i = 0; i < _optionalModules.Length; i++)
        {
            prompter.Say($"  {i + 1}. {_optionalModules[i]}");
        }

        while (true)
        {
            var text = Text("Numbers to add, separated by commas, or press Enter for none", "");
            var chosen = new List<string> { _alwaysModule };
            if (TryParseChoices(text, out var indexes))
            {
                chosen.AddRange(indexes.Select(index => _optionalModules[index]));
                return "[" + string.Join(",", chosen.Select(module => "\"" + module + "\"")) + "]";
            }

            prompter.Say($"Give numbers from 1 to {_optionalModules.Length}, separated by commas.");
        }
    }

    private string RequiredReference(string provider)
    {
        var family = FamilyOf(provider);
        while (true)
        {
            var text = Text($"Key reference for {provider}, such as {ExampleReference(provider)}", null);
            if (SecretReferences.IsValid(text, [family]))
            {
                return text;
            }

            prompter.Say($"The reference must use the {provider} form, and must not be a literal value.");
        }
    }

    private string RequiredSlug(string suggested)
    {
        while (true)
        {
            var slug = Text("Company slug: lower-case letters, digits and dashes. It cannot change later", suggested.Length == 0 ? null : suggested);
            if (IsSlug(slug))
            {
                return slug;
            }

            prompter.Say("The slug must be lower-case letters, digits and dashes, starting with a letter or digit, up to 63 characters.");
        }
    }

    private string RequiredText(string question, string? defaultValue)
    {
        while (true)
        {
            var text = Text(question, defaultValue);
            if (text.Length > 0)
            {
                return text;
            }

            prompter.Say("This answer is required.");
        }
    }

    private string Choice(string question, IReadOnlyList<string> options, string defaultValue)
    {
        prompter.Say($"{question}: {string.Join(", ", options)}.");
        while (true)
        {
            var text = Text(question, defaultValue);
            var match = options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            prompter.Say("Choose one of the options listed above.");
        }
    }

    private bool YesNo(string question, bool defaultValue)
    {
        var fallback = defaultValue ? "yes" : "no";
        while (true)
        {
            var text = Ask(question, fallback);
            switch (text.ToLowerInvariant())
            {
                case "y" or "yes":
                    return true;
                case "n" or "no":
                    return false;
                default:
                    prompter.Say("Answer yes or no.");
                    break;
            }
        }
    }

    /// <summary>One text answer, trimmed. An empty answer is the default, or "" when there is none.</summary>
    private string Text(string question, string? defaultValue) => Ask(question, defaultValue);

    /// <summary>The one place the wizard reads a line. The input ending is the only way out of the questions.</summary>
    private string Ask(string question, string? defaultValue)
    {
        var answer = prompter.Ask(question, defaultValue) ?? throw new InputEndedException();
        return answer;
    }

    /// <summary>Records one answer as a <c>path=value</c> flag, the same form the flags door takes.</summary>
    private void Set(string path, string value)
    {
        _sets.Add(path + "=" + value);
    }

    private SetupWizardResult Finish(int exitCode, string? planHash) => new(exitCode, _sets.ToList(), planHash);

    private void Emit(AdminReport report)
    {
        using var writer = new StringWriter();
        AdminTextRenderer.Write(report, writer);
        foreach (var line in writer.ToString().Split('\n'))
        {
            prompter.Say(line.TrimEnd('\r'));
        }
    }

    private static bool TryParseChoices(string text, out List<int> indexes)
    {
        indexes = [];
        if (text.Length == 0)
        {
            return true;
        }

        foreach (var part in text.Split(','))
        {
            if (!int.TryParse(part.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                || number < 1 || number > _optionalModules.Length)
            {
                return false;
            }

            if (!indexes.Contains(number - 1))
            {
                indexes.Add(number - 1);
            }
        }

        return true;
    }

    private static bool MatchesHashPrefix(string typed, string planHash) =>
        typed.Length >= _minimumHashPrefix
        && typed.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))
        && planHash.StartsWith(typed, StringComparison.OrdinalIgnoreCase);

    private static string PrefixHint(string planHash) => planHash[.._minimumHashPrefix] + "...";

    /// <summary>The lower-case slug of a company name, or "" when nothing usable remains.</summary>
    private static string SlugOf(string name)
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

    private static string DefaultProvider() => OperatingSystem.IsWindows() ? _windowsProvider : _defaultProvider;

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

    /// <summary>Thrown when the input ends. The wizard then stops without applying anything.</summary>
    private sealed class InputEndedException : Exception
    {
    }
}
