using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class AppUpdateServiceTests
{
    [Theory]
    [InlineData("https://github.com/ImAvgErix/ExoLauncher/releases/download/v1/ExoLauncher.exe")]
    [InlineData("https://objects.githubusercontent.com/object")]
    [InlineData("https://release-assets.githubusercontent.com/object")]
    public void UpdateDownloadHostsRequireExactHttpsOrigins(string value)
    {
        Assert.True(AppUpdateService.IsAllowedDownloadUri(new Uri(value)));
    }

    [Theory]
    [InlineData("http://github.com/ImAvgErix/ExoLauncher/releases/download/v1/ExoLauncher.exe")]
    [InlineData("https://evilgithub.com/ExoLauncher.exe")]
    [InlineData("https://github.com.evil.example/ExoLauncher.exe")]
    [InlineData("https://raw.githubusercontent.com/ImAvgErix/ExoLauncher/main/ExoLauncher.exe")]
    public void UpdateDownloadHostsRejectLookalikesAndUnexpectedOrigins(string value)
    {
        Assert.False(AppUpdateService.IsAllowedDownloadUri(new Uri(value)));
    }

    [Theory]
    [InlineData("ExoLauncher.exe", true)]
    [InlineData("ExoLauncher-Setup.exe", true)]
    [InlineData("ExoLauncher.zip", false)]
    [InlineData("setup.exe", false)]
    [InlineData("ExoLauncher-malware.exe", false)]
    public void UpdateAssetNamesFollowTheInstallerContract(string name, bool expected)
    {
        Assert.Equal(expected, AppUpdateService.IsUpdateAssetName(name));
    }

    [Fact]
    public void StartupCleanupPrunesStaleAndExcessInstallersWithoutTouchingUnrelatedFiles()
    {
        var root = CreateSandbox();
        try
        {
            var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
            var stale = CreateArtifact(root, ".exe", now.AddDays(-30));
            var excess = CreateArtifact(root, ".exe", now.AddDays(-3));
            var retainedOlder = CreateArtifact(root, ".exe", now.AddDays(-2));
            var retainedNewest = CreateArtifact(root, ".exe", now.AddDays(-1));
            var unrelated = Path.Combine(root, "keep-me.exe");
            File.WriteAllText(unrelated, "not updater-owned");

            _ = new AppUpdateService(
                new HttpClient(new UnexpectedRequestHandler()),
                root,
                new FixedTimeProvider(now));

            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(excess));
            Assert.True(File.Exists(retainedOlder));
            Assert.True(File.Exists(retainedNewest));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupCleanupRemovesInterruptedPartialButSkipsAnActiveDownload()
    {
        var root = CreateSandbox();
        try
        {
            var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
            var abandoned = CreateArtifact(root, ".partial", now.AddMinutes(-5));
            var active = CreateArtifact(root, ".partial", now.AddMinutes(-1));

            using (File.Open(active, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = new AppUpdateService(
                    new HttpClient(new UnexpectedRequestHandler()),
                    root,
                    new FixedTimeProvider(now));

                Assert.False(File.Exists(abandoned));
                Assert.True(File.Exists(active));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedPreLaunchValidationRemovesTheDownloadedArtifactImmediately()
    {
        var root = CreateSandbox();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("this is not a Windows installer");
            using var client = new HttpClient(new ReleaseHandler(bytes));
            var service = new AppUpdateService(client, root, TimeProvider.System);

            var result = await service.InstallAsync("1.0.0");

            Assert.False(result.Installed);
            Assert.Contains("not an Exo Launcher installer", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringDownloadRemovesThePartialArtifactImmediately()
    {
        var root = CreateSandbox();
        try
        {
            using var cts = new CancellationTokenSource();
            using var client = new HttpClient(new CancellingReleaseHandler(cts));
            var service = new AppUpdateService(client, root, TimeProvider.System);

            var result = await service.InstallAsync("1.0.0", ct: cts.Token);

            Assert.False(result.Installed);
            Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentInstallAttemptsAreSerialized()
    {
        var root = CreateSandbox();
        try
        {
            using var handler = new BlockingFirstCheckHandler();
            using var client = new HttpClient(handler);
            var service = new AppUpdateService(client, root, TimeProvider.System);

            var first = service.InstallAsync("1.0.0");
            await handler.FirstCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = service.InstallAsync("1.0.0");

            var prematureSecondCheck = await Task.WhenAny(
                handler.SecondCheckStarted.Task,
                Task.Delay(TimeSpan.FromMilliseconds(150)));
            Assert.NotSame(handler.SecondCheckStarted.Task, prematureSecondCheck);

            handler.ReleaseFirstCheck.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(handler.SecondCheckStarted.Task.IsCompletedSuccessfully);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateSandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-updater-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateArtifact(string root, string extension, DateTimeOffset modified)
    {
        var path = Path.Combine(root, $"ExoLauncher-Setup-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "artifact");
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
    }

    private class ReleaseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected byte[] AssetBytes { get; } = bytes;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
                return Task.FromResult(CreateReleaseResponse(AssetBytes));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(AssetBytes),
                RequestMessage = request,
            });
        }

        protected static HttpResponseMessage CreateReleaseResponse(byte[] bytes)
        {
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var json = $$"""
                {
                  "tag_name": "v9.9.9",
                  "assets": [{
                    "name": "ExoLauncher-Setup.exe",
                    "browser_download_url": "https://github.com/ImAvgErix/ExoLauncher/releases/download/v9.9.9/ExoLauncher-Setup.exe",
                    "size": {{bytes.Length}},
                    "digest": "sha256:{{digest}}"
                  }]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CancellingReleaseHandler(CancellationTokenSource cancellation)
        : ReleaseHandler([1, 2, 3, 4])
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
                return Task.FromResult(CreateReleaseResponse(AssetBytes));

            var content = new StreamContent(new CancelAfterFirstReadStream(AssetBytes, cancellation));
            content.Headers.ContentLength = AssetBytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }

    private sealed class CancelAfterFirstReadStream(
        byte[] bytes,
        CancellationTokenSource cancellation) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_served)
            {
                _served = true;
                bytes.AsSpan().CopyTo(buffer.Span);
                Position = bytes.Length;
                return ValueTask.FromResult(bytes.Length);
            }

            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellation.Token);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingFirstCheckHandler : ReleaseHandler
    {
        private int _checkCount;
        public TaskCompletionSource FirstCheckStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCheck { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCheckStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingFirstCheckHandler() : base(Encoding.UTF8.GetBytes("not an installer")) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
            {
                var count = Interlocked.Increment(ref _checkCount);
                if (count == 1)
                {
                    FirstCheckStarted.TrySetResult();
                    await ReleaseFirstCheck.Task.WaitAsync(cancellationToken);
                }
                else if (count == 2)
                {
                    SecondCheckStarted.TrySetResult();
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
