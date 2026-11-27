using Bazaar.Server.AccountApproval.Postgres;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Calendar;
using Bazaar.Server.CodingStore.Data;
using Bazaar.Server.Company;
using Bazaar.Server.CompanyBuild.Postgres;
using Bazaar.Server.CompanyMemory;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Council.Data;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Diagnostics.Persistence;
using Bazaar.Server.DeviceEnrolment.Postgres;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.GcReactions;
using Bazaar.Server.GeneticMemory;
using Bazaar.Server.Health;
using Bazaar.Server.Hive;
using Bazaar.Server.Kodo.Postgres;
using Bazaar.Server.Mail;
using Bazaar.Server.Mirror;
using Bazaar.Server.Modules;
using Bazaar.Server.OfflineSync.Postgres;
using Bazaar.Server.Portability.Postgres;
using Bazaar.Server.PsychWard;
using Bazaar.Server.Presence.Postgres;
using Bazaar.Server.Projects;
using Bazaar.Server.Setup.FirstRun.Postgres;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Server.SignIn.Postgres;
using Bazaar.Server.TalentPool.Component;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Walkthrough;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, the module manager's <c>modules</c> namespace, the Gateway module's <c>gateway</c> namespace, and the module component files under Sql/components/ (Vms, Council, Hive, Talent Pool, Account Approval, Setup, Setup first-run, Company, Agents, Calendar, Constellation, Health, GitHub, Lucky, and the rest), plus the portability namespace (import jobs, doc 44) from the Portability.Postgres assembly.</summary>
public static class HostMigrations
{
    /// <summary>Every module's migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly),
        new EmbeddedMigrationSource(typeof(ModuleManagerComponent).Assembly),
        new EmbeddedMigrationSource(typeof(UsageRepository).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(typeof(CouncilStore).Assembly),
        new ComponentMigrationSource(typeof(HiveComponent).Assembly),
        new ComponentMigrationSource(typeof(TalentPoolComponent).Assembly),
        new ComponentMigrationSource(typeof(PostgresApplyResultStore).Assembly),
        new EmbeddedMigrationSource(typeof(PostgresImportJobStore).Assembly),
        new ComponentMigrationSource(typeof(Bazaar.Server.SignIn.WebAuthn.Credentials.PostgresPasskeyCredentialStore).Assembly),
        new ComponentMigrationSource(typeof(Bazaar.Server.TimeMachine.TimeMachineComponent).Assembly),
        new ComponentMigrationSource(typeof(Bazaar.Server.Playtest.PlaytestComponent).Assembly),
        new ComponentMigrationSource(typeof(Bazaar.Server.WhatIf.WhatIfComponent).Assembly),
        new ComponentMigrationSource(typeof(PostgresEnrolmentStore).Assembly),
        new ComponentMigrationSource(typeof(PsychWardService).Assembly),
        new ComponentMigrationSource(typeof(GeneticMemoryComponent).Assembly),
        new ComponentMigrationSource(typeof(ProjectsComponent).Assembly),
        new ComponentMigrationSource(typeof(HealthServerComponent).Assembly),
        new ComponentMigrationSource(typeof(CompanyMemoryComponent).Assembly),
        new ComponentMigrationSource(typeof(MirrorComponent).Assembly),
        new ComponentMigrationSource(typeof(MailComponent).Assembly),
        new ComponentMigrationSource(typeof(GcReactionsComponent).Assembly),
        new ComponentMigrationSource(typeof(PostgresAccountApprovalStore).Assembly),
        new ComponentMigrationSource(typeof(PostgresCompanyProfileStore).Assembly),
        new ComponentMigrationSource(typeof(PostgresFirstRunStore).Assembly),
        new ComponentMigrationSource(typeof(PostgresToolCallStore).Assembly),
        new ComponentMigrationSource(typeof(PgAgentFirstRunSeeds).Assembly),
        new EmbeddedMigrationSource(typeof(PgPresenceStore).Assembly),
        new EmbeddedMigrationSource(typeof(Bazaar.Server.Engines.EnginesComponent).Assembly),
        new EmbeddedMigrationSource(typeof(PostgresHandoffCodeStore).Assembly),
        new EmbeddedMigrationSource(typeof(PostgresModuleSelection).Assembly),
        new EmbeddedMigrationSource(typeof(PgKodoJournalStore).Assembly),
        new ComponentMigrationSource(typeof(Bazaar.Server.GitHub.GitHubComponent).Assembly),
        new ComponentMigrationSource(typeof(CalendarComponent).Assembly),
        new ComponentMigrationSource(typeof(PostgresWalkthroughPrefsStore).Assembly),
        new ComponentMigrationSource(typeof(PgMemoryGraphStore).Assembly),
        new ComponentMigrationSource(OfflineSyncPostgresServiceCollectionExtensions.MigrationAssembly),
        new ComponentMigrationSource(typeof(Bazaar.Lucky.LuckyComponent).Assembly),
        new EmbeddedMigrationSource(typeof(PostgresCrashStore).Assembly));
}
