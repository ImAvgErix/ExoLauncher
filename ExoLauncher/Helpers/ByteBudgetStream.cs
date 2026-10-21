namespace ExoLauncher.Helpers;

/// <summary>Enforces one byte budget while reading or writing, even when the underlying length changes.</summary>
internal sealed class ByteBudgetStream(Stream inner, long budget, bool leaveOpen = false) : Stream
{
    private long _consumed;
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => inner.Length;
    public override long Position { get => _consumed; set => throw new NotSupportedException(); }
    private void Admit(int count)
    {
        if (count < 0 || count > budget - _consumed) throw new InvalidDataException("Cache exceeds its byte budget.");
        _consumed += count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = inner.Read(buffer[..(int)Math.Min(buffer.Length, budget - _consumed + 1)]);
        Admit(count); return count;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, budget - _consumed + 1)], cancellationToken).ConfigureAwait(false);
        Admit(count); return count;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { Admit(buffer.Length); inner.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Admit(buffer.Length); return inner.WriteAsync(buffer, cancellationToken); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
}
