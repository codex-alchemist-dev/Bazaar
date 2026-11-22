using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>The import job lifecycle (doc 44 sections 8.1 and 8.7). Pure rules, no database.</summary>
public class ImportJobStatesTests
{
    [Fact]
    public void Exactly_four_states_are_terminal()
    {
        var terminal = Enum.GetValues<ImportJobState>().Where(ImportJobStates.IsTerminal).ToHashSet();

        Assert.Equal(new HashSet<ImportJobState> { ImportJobState.Verified, ImportJobState.FailedVerify, ImportJobState.Failed, ImportJobState.Cancelled }, terminal);
    }

    [Fact]
    public void A_terminal_state_leads_nowhere()
    {
        foreach (var from in Enum.GetValues<ImportJobState>().Where(ImportJobStates.IsTerminal))
        {
            foreach (var to in Enum.GetValues<ImportJobState>())
            {
                Assert.False(ImportJobStates.CanTransition(from, to), from + " -> " + to + " must be refused");
            }
        }
    }

    [Fact]
    public void A_staged_job_cannot_skip_the_dry_run()
    {
        Assert.False(ImportJobStates.CanTransition(ImportJobState.Staged, ImportJobState.Committing));
        Assert.True(ImportJobStates.CanTransition(ImportJobState.Staged, ImportJobState.DryRun));
    }

    [Fact]
    public void A_committing_job_pauses_stalls_or_verifies()
    {
        Assert.True(ImportJobStates.CanTransition(ImportJobState.Committing, ImportJobState.Paused));
        Assert.True(ImportJobStates.CanTransition(ImportJobState.Committing, ImportJobState.Stalled));
        Assert.True(ImportJobStates.CanTransition(ImportJobState.Committing, ImportJobState.Verifying));
        Assert.False(ImportJobStates.CanTransition(ImportJobState.Committing, ImportJobState.Verified), "verification comes after the verifier runs");
    }

    [Fact]
    public void Commit_is_entered_only_from_dry_run_paused_or_stalled()
    {
        Assert.Equal(
            new HashSet<ImportJobState> { ImportJobState.DryRun, ImportJobState.Paused, ImportJobState.Stalled },
            ImportJobStates.SourcesFor(ImportJobState.Committing).ToHashSet());
    }

    [Fact]
    public void A_dry_run_can_return_to_staged_for_a_changed_mapping()
    {
        Assert.True(ImportJobStates.CanTransition(ImportJobState.DryRun, ImportJobState.Staged));
        Assert.Equal(new[] { ImportJobState.DryRun }, ImportJobStates.SourcesFor(ImportJobState.Staged));
    }

    [Fact]
    public void No_state_moves_to_itself()
    {
        foreach (var state in Enum.GetValues<ImportJobState>())
        {
            Assert.False(ImportJobStates.CanTransition(state, state), state + " -> itself must be refused");
        }
    }
}
