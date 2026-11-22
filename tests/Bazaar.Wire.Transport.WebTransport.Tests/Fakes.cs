using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Bazaar.Wire.Transport.WebTransport.Tests;

/// <summary>One in-memory bidirectional WebTransport stream: the server's pipe and the client's stream.</summary>
internal sealed class FakeStreamPair
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();

    public FakeStreamPair()
    {
        Server = new DefaultConnectionContext("fake-stream")
        {
            Transport = new DuplexPipe(_clientToServer.Reader, _serverToClient.Writer),
        };
        Server.Features.Set<IStreamDirectionFeature>(new Direction());
        Client = new ClientStream(_serverToClient.Reader.AsStream(), _clientToServer.Writer.AsStream());
    }

    public DefaultConnectionContext Server { get; }

    public Stream Client { get; }

    private sealed class Direction : IStreamDirectionFeature
    {
        public bool CanRead => true;

        public bool CanWrite => true;
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input => input;

        public PipeWriter Output => output;
    }

    private sealed class ClientStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);
    }
}

internal sealed class FakeSession(ConnectionContext? stream) : IWebTransportSession
{
    public int? AbortedWith { get; private set; }

    public long SessionId => 1;

    public void Abort(int errorCode) => AbortedWith = errorCode;

    public async ValueTask<ConnectionContext?> AcceptStreamAsync(CancellationToken cancellationToken)
    {
        if (stream is not null) return stream;
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public ValueTask<ConnectionContext?> OpenUnidirectionalStreamAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeFeature(IWebTransportSession session, bool isWebTransport = true) : IHttpWebTransportFeature
{
    public bool IsWebTransportRequest => isWebTransport;

    public ValueTask<IWebTransportSession> AcceptAsync(CancellationToken cancellationToken) => ValueTask.FromResult(session);
}

internal static class Requests
{
    public static DefaultHttpContext Connect(IWebTransportSession session, bool isWebTransport = true)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpWebTransportFeature>(new FakeFeature(session, isWebTransport));
        return context;
    }
}
