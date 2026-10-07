namespace ArturRios.Cerberus.WebApi.Middleware;

internal sealed class BoundedRequestStream(Stream inner, long maximum) : Stream
{
    private long _read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allowed(count)));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Allowed(buffer.Length)]));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, Allowed(count)), cancellationToken));

    private int Allowed(int requested) => maximum - _read >= requested ? requested : (int)Math.Min(requested, maximum - _read + 1);
    private int Count(int count)
    {
        _read += count;
        if (_read > maximum) throw new BadHttpRequestException("Request limit exceeded.", StatusCodes.Status413PayloadTooLarge);
        return count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
