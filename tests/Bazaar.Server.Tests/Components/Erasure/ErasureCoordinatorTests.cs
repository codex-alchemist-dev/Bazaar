using Bazaar.Server.Components.Erasure;

namespace Bazaar.Server.Tests.Components.Erasure;

/// <summary>Coordinator order, failure handling and idempotence, with fakes for the key eraser and participants.</summary>
public sealed class ErasureCoordinatorTests
{
    private static readonly Guid _person = Guid.Parse("0197a000-0000-7000-8000-0000000000e1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FakeLegalHoldReader NotHeld = new(held: false);

    [Fact]
    public async Task Key_destruction_runs_first_then_participants_in_registration_order()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 1, AlreadyDestroyed: 0));
        var coordinator = new ErasureCoordinator(keys, NotHeld, [
            new FakeParticipant("memory", log, ErasureParticipantResult.Removed(3)),
            new FakeParticipant("vault", log, ErasureParticipantResult.NothingToDo()),
            new FakeParticipant("mail", log, ErasureParticipantResult.Removed(1)),
        ]);

        var report = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(["key", "memory", "vault", "mail"], log);
        Assert.Equal(_person, report.PersonId);
        Assert.Equal(["memory", "vault", "mail"], report.Participants.Select(p => p.Name));
        Assert.Equal(3, report.Participants[0].Result.RemovedCount);
        Assert.True(report.IsComplete);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task A_failing_participant_keeps_the_key_result_and_marks_the_report_incomplete()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 2, AlreadyDestroyed: 0));
        var coordinator = new ErasureCoordinator(keys, NotHeld, [
            new FakeParticipant("first", log, ErasureParticipantResult.Removed(1)),
            new FakeParticipant("broken", log, new InvalidOperationException("disk unavailable")),
            new FakeParticipant("last", log, ErasureParticipantResult.NothingToDo()),
        ]);

        var report = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(["key", "first", "broken", "last"], log);
        Assert.Equal(PersonKeyOutcome.Destroyed, report.KeyOutcome);
        Assert.True(report.KeyDestroyed);
        Assert.False(report.IsComplete);
        Assert.Equal(ErasureParticipantStatus.Failed, report.Participants[1].Result.Status);
        Assert.Equal(nameof(InvalidOperationException), report.Participants[1].Result.Error);
        Assert.Equal(ErasureParticipantStatus.NothingToDo, report.Participants[2].Result.Status);
        Assert.Contains(report.Failures, f => f.Contains("broken", StringComparison.Ordinal) && f.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_key_destruction_is_reported_and_participants_still_run()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new InvalidOperationException("permission denied to set role"));
        var coordinator = new ErasureCoordinator(keys, NotHeld, [new FakeParticipant("memory", log, ErasureParticipantResult.Removed(4))]);

        var report = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(["key", "memory"], log);
        Assert.Equal(PersonKeyOutcome.Failed, report.KeyOutcome);
        Assert.False(report.KeyDestroyed);
        Assert.False(report.IsComplete);
        Assert.Equal(ErasureParticipantStatus.Removed, report.Participants[0].Result.Status);
        Assert.Contains(report.Failures, f => f.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_run_reports_already_destroyed_and_does_not_throw()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 0, AlreadyDestroyed: 1));
        var coordinator = new ErasureCoordinator(keys, NotHeld, [new FakeParticipant("memory", log, ErasureParticipantResult.NothingToDo())]);

        var first = await coordinator.EraseAsync(_person, Ct);
        var second = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(PersonKeyOutcome.AlreadyDestroyed, first.KeyOutcome);
        Assert.Equal(PersonKeyOutcome.AlreadyDestroyed, second.KeyOutcome);
        Assert.True(second.KeyDestroyed);
        Assert.True(second.IsComplete);
        Assert.Equal(4, log.Count);
    }

    [Fact]
    public async Task A_person_with_no_key_on_file_is_not_reported_as_destroyed()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 0, AlreadyDestroyed: 0));
        var coordinator = new ErasureCoordinator(keys, NotHeld, []);

        var report = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(PersonKeyOutcome.NoKeyOnFile, report.KeyOutcome);
        Assert.False(report.KeyDestroyed);
        Assert.False(report.IsComplete);
        Assert.Contains("no key on file for this person", report.Failures);
    }

    [Fact]
    public void Duplicate_participant_names_are_refused()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(1, 0));

        Assert.Throws<ArgumentException>(() => new ErasureCoordinator(keys, NotHeld, [
            new FakeParticipant("memory", log, ErasureParticipantResult.NothingToDo()),
            new FakeParticipant("memory", log, ErasureParticipantResult.NothingToDo()),
        ]));
    }

    [Fact]
    public async Task An_empty_person_id_is_refused()
    {
        var coordinator = new ErasureCoordinator(new FakeKeyEraser([], new PersonKeyEraseResult(1, 0)), NotHeld, []);

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.EraseAsync(Guid.Empty, Ct));
    }

    [Fact]
    public async Task A_held_person_runs_no_key_destruction_and_no_participant()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 1, AlreadyDestroyed: 0));
        var coordinator = new ErasureCoordinator(keys, new FakeLegalHoldReader(held: true, log), [
            new FakeParticipant("memory", log, ErasureParticipantResult.Removed(3)),
        ]);

        var report = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(["hold"], log);
        Assert.Equal(PersonKeyOutcome.HeldQueued, report.KeyOutcome);
        Assert.False(report.KeyDestroyed);
        Assert.False(report.IsComplete);
        Assert.Empty(report.Participants);
        Assert.Contains("legal hold active; erasure queued", report.Failures);
    }

    [Fact]
    public async Task The_hold_is_consulted_before_any_destruction_and_an_unheld_person_runs_the_normal_path()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 1, AlreadyDestroyed: 0));
        var coordinator = new ErasureCoordinator(keys, new FakeLegalHoldReader(held: false, log), [
            new FakeParticipant("memory", log, ErasureParticipantResult.Removed(3)),
        ]);

        var report = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(["hold", "key", "memory"], log);
        Assert.Equal(PersonKeyOutcome.Destroyed, report.KeyOutcome);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task A_repeat_run_while_held_stays_queued_and_destroys_nothing()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 1, AlreadyDestroyed: 0));
        var coordinator = new ErasureCoordinator(keys, new FakeLegalHoldReader(held: true, log), []);

        var first = await coordinator.EraseAsync(_person, Ct);
        var second = await coordinator.EraseAsync(_person, Ct);

        Assert.Equal(PersonKeyOutcome.HeldQueued, first.KeyOutcome);
        Assert.Equal(PersonKeyOutcome.HeldQueued, second.KeyOutcome);
        Assert.False(second.IsComplete);
        Assert.Equal(["hold", "hold"], log);
    }

    [Fact]
    public async Task A_failing_hold_check_propagates_and_destroys_nothing()
    {
        var log = new List<string>();
        var keys = new FakeKeyEraser(log, new PersonKeyEraseResult(Destroyed: 1, AlreadyDestroyed: 0));
        var coordinator = new ErasureCoordinator(keys, new FakeLegalHoldReader(new InvalidOperationException("db down"), log), [
            new FakeParticipant("memory", log, ErasureParticipantResult.Removed(3)),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EraseAsync(_person, Ct));

        Assert.Equal(["hold"], log);
    }

    private sealed class FakeLegalHoldReader : ILegalHoldReader
    {
        private readonly bool _held;
        private readonly List<string>? _log;
        private readonly Exception? _error;

        public FakeLegalHoldReader(bool held, List<string>? log = null)
        {
            _held = held;
            _log = log;
        }

        public FakeLegalHoldReader(Exception error, List<string> log)
        {
            _error = error;
            _log = log;
        }

        public Task<bool> IsPersonHeldAsync(Guid personId, CancellationToken ct = default)
        {
            _log?.Add("hold");
            if (_error is not null)
                throw _error;
            return Task.FromResult(_held);
        }
    }

    private sealed class FakeKeyEraser : IPersonKeyEraser
    {
        private readonly List<string> _log;
        private readonly PersonKeyEraseResult? _result;
        private readonly Exception? _error;

        public FakeKeyEraser(List<string> log, PersonKeyEraseResult result)
        {
            _log = log;
            _result = result;
        }

        public FakeKeyEraser(List<string> log, Exception error)
        {
            _log = log;
            _error = error;
        }

        public Task<PersonKeyEraseResult> EraseAsync(Guid personId, CancellationToken ct = default)
        {
            _log.Add("key");
            if (_error is not null)
                throw _error;
            return Task.FromResult(_result!);
        }
    }

    private sealed class FakeParticipant : IErasureParticipant
    {
        private readonly List<string> _log;
        private readonly ErasureParticipantResult? _result;
        private readonly Exception? _error;

        public FakeParticipant(string name, List<string> log, ErasureParticipantResult result)
        {
            Name = name;
            _log = log;
            _result = result;
        }

        public FakeParticipant(string name, List<string> log, Exception error)
        {
            Name = name;
            _log = log;
            _error = error;
        }

        public string Name { get; }

        public Task<ErasureParticipantResult> EraseAsync(Guid personId, CancellationToken ct = default)
        {
            _log.Add(Name);
            if (_error is not null)
                throw _error;
            return Task.FromResult(_result!);
        }
    }
}
