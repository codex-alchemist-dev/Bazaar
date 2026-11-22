namespace Bazaar.ModulePermissions.Fs;

/// <summary>
/// The write-only stream a module gets from <see cref="ModuleFileSystem.OpenWrite"/> and <see cref="ModuleFileSystem.OpenAppend"/>.
/// Before any call that could make the file longer (a write, <see cref="SetLength"/>, a write after a seek past the end), the
/// new length is checked against the per-file limit and the growth is charged to the root; a refusal happens before anything
/// is written. The length is tracked here, which is sound because the file is open without sharing for writes.
/// </summary>
internal sealed class ModuleFsQuotaStream : Stream
{
    private readonly FileStream _inner;
    private readonly long _maxFileBytes;
    private readonly ModuleFsRootUsage _usage;
    private long _length;

    public ModuleFsQuotaStream(FileStream inner, long maxFileBytes, ModuleFsRootUsage usage)
    {
        _inner = inner;
        _maxFileBytes = maxFileBytes;
        _usage = usage;
        _length = inner.Length;
    }

    public override bool CanRead => false;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value > _maxFileBytes)
        {
            throw new ModuleFsRefusedException("The file would be larger than the limit.");
        }

        var old = _length;
        if (value > old)
        {
            _usage.Charge(value - old);
            _length = value;
        }

        try
        {
            _inner.SetLength(value);
        }
        catch
        {
            Resync();
            throw;
        }

        if (value < old)
        {
            _usage.Release(old - value);
            _length = value;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Reserve(buffer.Length);
        try
        {
            _inner.Write(buffer);
        }
        catch
        {
            Resync();
            throw;
        }
    }

    public override void WriteByte(byte value)
    {
        Reserve(1);
        try
        {
            _inner.WriteByte(value);
        }
        catch
        {
            Resync();
            throw;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reserve(buffer.Length);
        try
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Resync();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    // Checks the length a write of count bytes at the current position would reach, and charges the growth, before the write.
    private void Reserve(int count)
    {
        if (count == 0)
        {
            return;
        }

        var position = _inner.Position;
        if (count > _maxFileBytes || position > _maxFileBytes - count)
        {
            throw new ModuleFsRefusedException("The file would be larger than the limit.");
        }

        var end = position + count;
        if (end > _length)
        {
            _usage.Charge(end - _length);
            _length = end;
        }
    }

    // After a failed write or SetLength the file may be shorter than was reserved; take the real length and refund the difference.
    private void Resync()
    {
        try
        {
            var actual = _inner.Length;
            if (actual != _length)
            {
                _usage.Adjust(actual - _length);
                _length = actual;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // The handle is unusable; the reservation stays, which errs on the side of the quota.
        }
    }
}
