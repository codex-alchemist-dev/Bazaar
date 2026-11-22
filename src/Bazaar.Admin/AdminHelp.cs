using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>The help text, in plain text. The exit code numbers come from <see cref="SetupExitCode"/>, not from literals.</summary>
internal static class AdminHelp
{
    public static string Top => $"""
        {AdminConstants.CommandName}: command-line administration for a Bazaar server.

        Usage:
          {AdminConstants.CommandName} setup validate --config <path> [--set path=value]... [--json]
          {AdminConstants.CommandName} setup                     Interactive wizard: plain prompts, then a review and an optional apply.
          {AdminConstants.CommandName} setup --dry-run --config <path> [--set path=value]... [--json]
          {AdminConstants.CommandName} setup apply --config <path> --plan-hash <hash> [--set path=value]... [--json]
          {AdminConstants.CommandName} kodo verify --config <path> [--json]
          {AdminConstants.CommandName} secret set <reference>
          {AdminConstants.CommandName} setup --help
          {AdminConstants.CommandName} secret --help
          {AdminConstants.CommandName} kodo --help
          {AdminConstants.CommandName} --help

        Commands:
          setup    Validate, dry-run or apply a setup file (bazaar-setup.json), or run the wizard with no options.
          secret   Store one secret value under a secret://bazaar/ reference. The value is typed, never an argument.
          kodo     Verify a finished setup: read-only checks, a score and the amber items (doc 45 section 1.12).

        Exit codes:
          {(int)SetupExitCode.Ok}  OK: the input is valid, or the plan was applied.
          {(int)SetupExitCode.InvalidInput}  Invalid input, an unknown command, or a missing argument.
          {(int)SetupExitCode.Held}  Held: a floor or an approval blocks a change. Nothing was changed.
          {(int)SetupExitCode.ApplyFailedRolledBack}  Apply failed and was rolled back. For kodo verify, red (see the kodo help).

        Output: plain text, one fact per line. --json prints one JSON document on stdout, for scripts.
        Errors name the key and the rule, never a value.

        Values come from three doors, merged in this order (later wins): the file, then BAZAAR__SETUP__
        environment variables, then --set flags. The plan is the same whichever door was used.
        Environment: BAZAAR__SETUP__INSTALL__COMPANYNAME=Example, list items BAZAAR__SETUP__MODELS__SOURCES__0__ID.
        Lists take a JSON array. Empty variables are unset.

        Secrets: no option takes a secret value. The setup file holds references only: env:NAME,
        file:PATH, dpapi:PATH, systemd-creds:NAME (bootstrap values) or secret://bazaar/... (other secrets).

        """;

    public static string Secret => $"""
        {AdminConstants.CommandName} secret: store one secret value (doc 45 section 1.6).

        Commands:
          secret set <reference>
              Store the value under a secret://bazaar/ reference, for example secret://bazaar/providers/anthropic/key.
              The value is read from standard input when it is piped, otherwise from a prompt that does not echo.
              It is never an argument, so it does not reach shell history or the process list. The reference is checked
              before the value is read. Nothing prints the value.
              Store availability: this build has no secret store, so the command reports that and stores nothing (exit {(int)SetupExitCode.Held}).

        Exit codes: {(int)SetupExitCode.Ok} stored, {(int)SetupExitCode.InvalidInput} invalid input or no value, {(int)SetupExitCode.Held} no secret store in this build, {(int)SetupExitCode.ApplyFailedRolledBack} the store did not accept the value.

        """;

    public static string Setup => $"""
        {AdminConstants.CommandName} setup: validate, dry-run or apply a setup file, or run the wizard.

        The wizard: run {AdminConstants.CommandName} setup with no other option. It asks one question at a time, shows the same
        review as setup --dry-run, and applies only when you type the start of the plan hash it printed. Its answers go through
        the same checks as --set flags, so the same answers give the same plan hash. It never writes a file.

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
              Apply needs a reachable, migrated database (doc 15 audit_log). Set BAZAAR__HOST__DATABASECONNECTIONREFERENCE to an
              env: or file: reference for its connection string. Apply is unavailable when that database cannot be reached: after the
              file and hash checks pass, apply prints that it is unavailable, exits {(int)SetupExitCode.Held}, and changes nothing.
              It runs under the setup instance lock and writes one hash-chained audit row. No setup steps are built yet,
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

    public static string Kodo => $"""
        {AdminConstants.CommandName} kodo: verify a finished setup (doc 45 section 1.12; health module doc 46).

        Commands:
          kodo verify --config <path> [--json]
              Run the read-only checks on the setup file. It lists each check with its status and message,
              the score (0 to 100), and the amber items that need acknowledgement. Writes nothing.
              Checks that need a running service (schema, PostgreSQL, providers, backups, clocks and so on) are
              listed as not evaluated until the Host adapter provides them. Not evaluated counts as amber, never green.
              A validator hold in the file also makes the result amber.

        Options:
          --config <path>      The setup file. Required.
          --json               Print one JSON document on stdout instead of plain text.
          --help               Show this help.

        Exit codes:
          {(int)KodoVerifyExitCodes.Green}  Green: every check passed.
          {(int)KodoVerifyExitCodes.InvalidInput}  Invalid input: the file cannot be read or does not validate. No checks ran.
          {(int)KodoVerifyExitCodes.Amber}  Amber: held for acknowledgement. Setup completes only when each amber item is acknowledged.
          {(int)KodoVerifyExitCodes.Red}  Red: a check failed. Setup cannot be marked complete. (Setup apply uses this number for a rollback.)

        Secrets: no option takes a secret value. Checks read references only and never resolve them, and no message shows a value.

        """;
}
