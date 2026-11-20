using Bazaar.Contracts.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Diagnostics.Tests;

/// <summary>Releasing a closed connection frees its open uploads, driven the way the wire's close hook will drive it.</summary>
public sealed class CrashConnectionReleaseTests
{
    private static readonly Guid _closing = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid _staying = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a2");
    private static readonly Guid _report = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private static CrashAttachmentBegin Begin(Guid id) => new(id, _report, "screenshot", "image/png", 6);

    [Fact]
    public void Release_frees_the_closed_connections_uploads_and_leaves_the_others()
    {
        var uploads = new AttachmentUploadAssembler(new AttachmentUploadOptions(), new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero)));
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        Assert.Null(uploads.Begin(_closing, Begin(gone)));
        Assert.Null(uploads.Begin(_staying, Begin(kept)));
        var release = new CrashConnectionRelease(uploads);

        release.Release(_closing);

        Assert.Equal(AttachmentUploadRefusal.UnknownSession, uploads.Chunk(_closing, new CrashAttachmentChunk(gone, 0, new byte[] { 1 })));
        Assert.Null(uploads.Chunk(_staying, new CrashAttachmentChunk(kept, 0, new byte[] { 1 })));
    }

    [Fact]
    public void Release_twice_and_for_an_unknown_connection_is_harmless()
    {
        var uploads = new AttachmentUploadAssembler(new AttachmentUploadOptions(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var release = new CrashConnectionRelease(uploads);

        release.Release(Guid.NewGuid());
        release.Release(Guid.NewGuid());

        Assert.Equal(0, uploads.OpenBytes);
    }
}
