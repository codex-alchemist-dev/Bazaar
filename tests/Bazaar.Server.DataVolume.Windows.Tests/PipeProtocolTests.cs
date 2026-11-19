using System.IO.Pipelines;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Bazaar.Server.DataVolume.Windows.Tests;

/// <summary>The status pipe protocol, server and client, over in-memory streams so it runs on Linux.</summary>
public sealed class PipeProtocolTests
{
    private const string _guid = FakeWorld.Guid;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("status 0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70", true)]
    [InlineData("status  0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70", false)]
    [InlineData("status 0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70 ", false)]
    [InlineData("Status 0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70", false)]
    [InlineData("status 0B6F2A1E-5C3D-4E7A-9B1F-3A2C4D5E6F70", false)]
    [InlineData("status {0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70}", false)]
    [InlineData("status \\\\?\\Volume{0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70}\\", false)]
    [InlineData("status 0b6f2a1e5c3d4e7a9b1f3a2c4d5e6f70", false)]
    [InlineData("status", false)]
    [InlineData("status ", false)]
    [InlineData("lock 0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70", false)]
    [InlineData("status 0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70\tlock", false)]
    [InlineData("", false)]
    public void Only_the_exact_request_form_parses(string line, bool valid)
    {
        Assert.Equal(valid, VolumeStatusProtocol.TryParseRequest(line, out var guid));
        Assert.Equal(valid ? _guid : string.Empty, guid);
    }

    [Fact]
    public void A_null_request_does_not_parse()
    {
        Assert.False(VolumeStatusProtocol.TryParseRequest(null, out _));
    }

    [Fact]
    public void The_reply_round_trips_as_one_camel_case_json_line()
    {
        var reply = new VolumeStatusReply(_guid, true, true, 7, false);

        var line = VolumeStatusProtocol.SerializeReply(reply);

        Assert.Equal($"{{\"volume\":\"{_guid}\",\"conversionComplete\":true,\"protectionOn\":true,\"encryptionMethod\":7,\"locked\":false}}", line);
        Assert.DoesNotContain('\n', line);
        Assert.Equal(reply, VolumeStatusProtocol.ParseReply(line));
    }

    [Fact]
    public void An_unreadable_reply_parses_to_null()
    {
        Assert.Null(VolumeStatusProtocol.ParseReply("not json"));
    }

    [Fact]
    public async Task The_client_gets_the_servers_answer()
    {
        var volumes = new FakeStatusVolumes();

        var reply = await Roundtrip(volumes, _guid);

        Assert.Equal(new VolumeStatusReply(_guid, true, true, 7, false), reply);
    }

    [Fact]
    public async Task A_locked_volume_is_reported_as_locked()
    {
        var volumes = new FakeStatusVolumes { Lock = 1 };

        var reply = await Roundtrip(volumes, _guid);

        Assert.NotNull(reply);
        Assert.True(reply.Locked);
    }

    [Fact]
    public async Task A_bitlocker_failure_gives_an_error_reply_and_the_client_sees_no_status()
    {
        var volumes = new FakeStatusVolumes { Fail = true };

        Assert.Null(await Roundtrip(volumes, _guid));
    }

    [Fact]
    public async Task The_client_rejects_an_answer_for_another_volume()
    {
        var (client, server) = Duplex();
        var serving = Task.Run(async () =>
        {
            await VolumeStatusProtocol.ReadLineAsync(server, 64, Ct);
            await VolumeStatusProtocol.WriteLineAsync(server, VolumeStatusProtocol.SerializeReply(new VolumeStatusReply("11111111-2222-4333-8444-555555555555", true, true, 7, false)), Ct);
        }, Ct);

        Assert.Null(await NamedPipeVolumeStatusChannel.ExchangeAsync(client, _guid, Ct));
        await serving;
    }

    [Fact]
    public async Task The_client_rejects_an_oversized_answer()
    {
        var (client, server) = Duplex();
        var serving = Task.Run(async () =>
        {
            await VolumeStatusProtocol.ReadLineAsync(server, 64, Ct);
            await VolumeStatusProtocol.WriteLineAsync(server, new string('x', VolumeStatusProtocol.MaxResponseBytes + 10), Ct);
        }, Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await NamedPipeVolumeStatusChannel.ExchangeAsync(client, _guid, Ct));
        await serving;
    }

    [Theory]
    [InlineData("lock 0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70")]
    [InlineData("status ../../etc/passwd")]
    [InlineData("status 0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70 extra")]
    [InlineData("")]
    public async Task The_server_answers_a_malformed_request_with_an_error_and_asks_bitlocker_nothing(string request)
    {
        var volumes = new FakeStatusVolumes();
        var (client, server) = Duplex();

        var serving = new VolumeStatusPipeServer(volumes).HandleAsync(server, Ct).AsTask();
        await VolumeStatusProtocol.WriteLineAsync(client, request, Ct);
        var line = await VolumeStatusProtocol.ReadLineAsync(client, VolumeStatusProtocol.MaxResponseBytes, Ct);
        await serving;

        var reply = VolumeStatusProtocol.ParseReply(line!);
        Assert.Equal("bad_request", reply?.Error);
        Assert.Null(reply?.Volume);
        Assert.Equal(0, volumes.Asked);
    }

    [Fact]
    public async Task The_server_rejects_an_oversized_request_without_reading_it_all()
    {
        var volumes = new FakeStatusVolumes();
        var (client, server) = Duplex();

        var serving = new VolumeStatusPipeServer(volumes).HandleAsync(server, Ct).AsTask();
        await client.WriteAsync(Encoding.UTF8.GetBytes(new string('a', 5000) + "\n"), Ct);
        await client.FlushAsync(Ct);
        var line = await VolumeStatusProtocol.ReadLineAsync(client, VolumeStatusProtocol.MaxResponseBytes, Ct);
        await serving;

        Assert.Equal("bad_request", VolumeStatusProtocol.ParseReply(line!)?.Error);
        Assert.Equal(0, volumes.Asked);
    }

    [Fact]
    public async Task A_line_longer_than_the_cap_throws_and_a_missing_terminator_throws()
    {
        using var tooLong = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 10) + "\n"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await VolumeStatusProtocol.ReadLineAsync(tooLong, 5, Ct));

        using var unterminated = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await VolumeStatusProtocol.ReadLineAsync(unterminated, 50, Ct));

        using var empty = new MemoryStream();
        Assert.Null(await VolumeStatusProtocol.ReadLineAsync(empty, 50, Ct));
    }

    [Fact]
    public async Task A_carriage_return_before_the_line_feed_is_dropped()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("status x\r\n"));

        Assert.Equal("status x", await VolumeStatusProtocol.ReadLineAsync(stream, 50, Ct));
    }

    [Fact]
    public async Task A_missing_pipe_gives_no_status()
    {
        var channel = new NamedPipeVolumeStatusChannel("bazaar-volume-test-" + System.Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(200));

        Assert.Null(await channel.GetStatusAsync(_guid, Ct));
    }

    [Fact]
    public async Task The_channel_refuses_a_non_canonicalGuid_without_connecting()
    {
        var channel = new NamedPipeVolumeStatusChannel("bazaar-volume-test-" + System.Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(30));

        Assert.Null(await channel.GetStatusAsync("{" + _guid + "}", Ct));
    }

    [Fact]
    public async Task The_client_and_server_talk_over_a_real_named_pipe()
    {
        var name = "bazaar-volume-test-" + System.Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var server = new VolumeStatusPipeServer(new FakeStatusVolumes());
        var serving = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(Ct);
            await server.HandleAsync(pipe, Ct);
        }, Ct);

        var reply = await new NamedPipeVolumeStatusChannel(name, TimeSpan.FromSeconds(5)).GetStatusAsync(_guid, Ct);
        await serving;

        Assert.Equal(new VolumeStatusReply(_guid, true, true, 7, false), reply);
    }

    [Fact]
    public void The_pipe_acl_allows_system_administrators_and_the_service_account_only_and_denies_network()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The pipe ACL is a Windows security descriptor.");

        if (OperatingSystem.IsWindows())
            AssertPipeAcl();
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPipeAcl()
    {
        var service = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var security = VolumeStatusPipeServer.BuildSecurity(service);

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => ((SecurityIdentifier)r.IdentityReference, r.PipeAccessRights)).ToList();
        Assert.Contains((new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl), allowed);
        Assert.Contains((new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl), allowed);
        Assert.Contains((service, PipeAccessRights.ReadWrite), allowed);
        Assert.Equal(3, allowed.Count);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny
            && ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid));
    }

    private static async Task<VolumeStatusReply?> Roundtrip(FakeStatusVolumes volumes, string guid)
    {
        var (client, server) = Duplex();
        var serving = new VolumeStatusPipeServer(volumes).HandleAsync(server, Ct).AsTask();
        var reply = await NamedPipeVolumeStatusChannel.ExchangeAsync(client, guid, Ct);
        await serving;
        return reply;
    }

    /// <summary>Two connected in-memory duplex streams: what one writes, the other reads.</summary>
    private static (Stream Client, Stream Server) Duplex()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        return (
            new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()),
            new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeStatusVolumes : IBitLockerVolumes
    {
        public int Lock { get; set; }

        public bool Fail { get; set; }

        public int Asked { get; private set; }

        private ValueTask<int> Answer(int value)
        {
            Asked++;
            return Fail ? throw new BitLockerException("failed (test)") : ValueTask.FromResult(value);
        }

        public ValueTask<int> GetConversionStatusAsync(string volumeGuid, CancellationToken cancellationToken) => Answer(BitLockerConversion.FullyEncrypted);

        public ValueTask<int> GetProtectionStatusAsync(string volumeGuid, CancellationToken cancellationToken) => Answer(BitLockerValues.ProtectionOn);

        public ValueTask<int> GetEncryptionMethodAsync(string volumeGuid, CancellationToken cancellationToken) => Answer(BitLockerValues.XtsAes256);

        public ValueTask<int> GetLockStatusAsync(string volumeGuid, CancellationToken cancellationToken) => Answer(Lock);

        public ValueTask ProtectKeyWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask EncryptAsync(string volumeGuid, int encryptionMethod, int encryptionFlags, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UnlockWithExternalKeyAsync(string volumeGuid, ReadOnlyMemory<byte> externalKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<uint> LockAsync(string volumeGuid, bool forceDismount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
