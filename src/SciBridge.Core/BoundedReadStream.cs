namespace SciBridge;

/// <summary>
/// Exposes exactly <paramref name="length"/> bytes of a network stream to
/// SSH.NET without buffering the whole upload in memory. The inner stream is
/// owned by the HTTP handler and is not disposed here.
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;

    public BoundedReadStream(Stream inner, long length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        _inner = inner;
        _length = length;
    }

    public bool IsFullyRead => _position >= _length;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set
        {
            // SSH.NET may reset a fresh stream to zero; do not allow rewinding
            // a network stream that has already been consumed.
            if (value == 0 && _position == 0) return;
            if (value == _position) return;
            throw new NotSupportedException("网络流不支持回退。");
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = (int)Math.Min(count, _length - _position);
        if (allowed <= 0) return 0;
        var read = _inner.Read(buffer, offset, allowed);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _position);
        if (allowed <= 0) return 0;
        var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target == _position) return _position;
        if (target == 0 && _position == 0) return 0;
        throw new NotSupportedException("网络流不支持回退。");
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The HTTP handler owns the network stream.
    }
}
