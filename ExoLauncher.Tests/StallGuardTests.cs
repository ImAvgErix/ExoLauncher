using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a driver download could hang or mislead:
/// - a server that goes quiet mid-file holds the update at "downloading" forever (HttpClient's timeout ends at the headers);
/// - the user's own cancel is mistaken for a stall, or a stall for a cancel;
/// - a read that does arrive is slowed or cut by the guard.
/// </summary>
public sealed class StallGuardTests
{
    private sealed class Silent : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AServerThatGoesQuietIsATimeoutNotAHang()
    {
        var buffer = new byte[16];
        await Assert.ThrowsAsync<TimeoutException>(() => StallGuard.ReadAsync(new Silent(), buffer, CancellationToken.None, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task TheCallersOwnCancelStaysACancel()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var buffer = new byte[16];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StallGuard.ReadAsync(new Silent(), buffer, cancel.Token, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ARealReadPassesThroughWhole()
    {
        using var data = new MemoryStream([1, 2, 3, 4, 5]);
        var buffer = new byte[16];
        Assert.Equal(5, await StallGuard.ReadAsync(data, buffer, CancellationToken.None, TimeSpan.FromSeconds(30)));
        Assert.Equal(0, await StallGuard.ReadAsync(data, buffer, CancellationToken.None, TimeSpan.FromSeconds(30)));
    }
}
