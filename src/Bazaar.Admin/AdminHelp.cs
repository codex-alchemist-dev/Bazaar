using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>The help text, in plain text. The exit code numbers come from <see cref="SetupExitCode"/>, not from literals.</summary>
internal static class AdminHelp
{
    public static string Top => $"""
        {AdminConstants.CommandName}: command-line administration for a Bazaar server.

        Usage:
          {AdminConstants.CommandName} setup validate --config <path> [--set path=value]... [--json]
          {AdminConstants.CommandName} setup --dry-run --config <path> [--set path=value]... [--json]
          {AdminConstants.CommandName} setup apply --config <path> --plan-hash <hash> [--set path=value]... [--json]
          {AdminConstants.CommandName} setup --help
          {AdminConstants.CommandName} --help

        Commands:
          setup    Validate, dry-run or apply a setup file (bazaar-setup.json).

        Exit codes:
          {(int)SetupExitCode.Ok}  OK: the input is valid, or the plan was applied.
          {(int)SetupExitCode.InvalidInput}  Invalid input, an unknown command, or a missing argument.
          {(int)SetupExitCode.Held}  Held: a floor or an approval blocks a change. Nothing was changed.
          {(int)SetupExitCode.ApplyFailedRolledBack}  Apply failed and was rolled back.

        Output: plain text, one fact per line. --json prints one JSON document on stdout, for scripts.
        Errors name the key and the rule, never a value.

        Values come from three doors, merged in this order (later wins): the file, then BAZAAR__SETUP__
        environment variables, then --set flags. The plan is the same whichever door was used.
        Environment: BAZAAR__SETUP__INSTALL__COMPANYNAME=Example, list items BAZAAR__SETUP__MODELS__SOURCES__0__ID.
        Lists take a JSON array. Empty variables are unset.

        Secrets: no option takes a secret value. The setup file holds references only: env:NAME,
        file:PATH, dpapi:PATH, systemd-creds:NAME (bootstrap values) or secret://bazaar/... (other secrets).

        """;

    public static string Setup => $"""
        {AdminConstants.CommandName} setup: validate, dry-run or apply a setup file.

        Commands:
          setup validate --config <path> [--set path=value]... [--json]
              Check the file, with any --set flags and BAZAAR__SETUP__ variables merged over it. Prints each error
              or hold with its key, and the plan hash.
          setup --dry-run --config <path> [--set path=value]... [--json]
              Check the file, then list each change as create, update or remove. Writes nothing.
              Current state: this build compares against an empty install only. A state reader for an
              existing install is not built yet, so every planned value is listed as a create.
          setup apply --config <path> --plan-hash <hash> [--set path=value]... [--json]
              Apply the plan only when the hash matches the one the dry run printed. The entry path recorded in the
              audit row is cli when --set flags were used, env when only environment variables were, and file otherwise.
              Apply is unavailable in this build: it has no durable audit store yet (doc 15 audit_log). After the
              file and hash checks pass, apply prints that it is unavailable, exits {(int)SetupExitCode.Held}, and changes nothing.
              The Host adapter provides the audit store and turns apply on. Even then, no setup steps are built yet,
              so a plan with changes would exit {(int)SetupExitCode.ApplyFailedRolledBack}.

        Options:
          --config <path>      The setup file. Optional when --set or a BAZAAR__SETUP__ variable gives the values;
                               then the plan starts empty and needs every required key supplied.
          --set path=value     Set one key, for example install.companyName=Example Studio. Repeat for more keys.
                               Use dotted camelCase names; list items take an index, for example models.sources.0.id.
                               Settings keys are written after settings, for example settings.host.shutdown_timeout_seconds=20.
          --plan-hash <hash>   The 64-character hash the dry run printed. Required for apply only.
          --dry-run            Run a dry run. Use it with setup, not with validate or apply.
          --json               Print one JSON document on stdout instead of plain text.
          --help               Show this help.

        Secrets: no option takes a secret value, and a literal value under a secret-typed key is an error.
        Values under secret-typed keys are never printed. A dry run shows only the reference scheme.

        Exit codes: {(int)SetupExitCode.Ok} OK, {(int)SetupExitCode.InvalidInput} invalid input, {(int)SetupExitCode.Held} held, {(int)SetupExitCode.ApplyFailedRolledBack} apply failed and rolled back.

        """;
}
