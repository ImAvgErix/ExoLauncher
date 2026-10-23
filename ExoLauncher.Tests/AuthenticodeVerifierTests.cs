using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the signature check Exo runs on NVIDIA's driver, its runtimes and upscaler files could fail:
/// - a properly signed file is refused once revocation is checked (the check fails closed on a network quirk);
/// - the wrong publisher, or a file with no signature, or one changed after signing, passes;
/// - a missing file throws instead of failing.
/// </summary>
public sealed class AuthenticodeVerifierTests
{
    // .NET's own runtime library: Authenticode-signed by Microsoft, on every machine that runs these tests.
    private static readonly string Signed = typeof(object).Assembly.Location;

    [Fact]
    public void ASignedFilePassesWithItsPublisherOnly()
    {
        Assert.True(AuthenticodeVerifier.WinTrustValid(Signed));
        Assert.True(AuthenticodeVerifier.HasPublisher(Signed, SignerName()));
        Assert.False(AuthenticodeVerifier.HasPublisher(Signed, "NVIDIA Corporation"));
    }

    // The certificate's own name (".NET" today, once "Microsoft Corporation"), so the test follows Microsoft's signing.
    private static string SignerName()
    {
#pragma warning disable SYSLIB0057
        using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
            System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(Signed));
#pragma warning restore SYSLIB0057
        return certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
    }

    [Fact]
    public void AnUnsignedOrChangedFileFails()
    {
        var unsigned = Path.Combine(Path.GetTempPath(), $"exo-unsigned-{Guid.NewGuid():N}.dll");
        var changed = Path.Combine(Path.GetTempPath(), $"exo-changed-{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0, 1, 2, 3]);
            Assert.False(AuthenticodeVerifier.WinTrustValid(unsigned));
            // The signed library with one byte changed in its code: its signature no longer matches.
            var bytes = File.ReadAllBytes(Signed);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(changed, bytes);
            Assert.False(AuthenticodeVerifier.WinTrustValid(changed));
        }
        finally
        {
            File.Delete(unsigned);
            File.Delete(changed);
        }
    }

    [Fact]
    public void AMissingFileFails() =>
        Assert.False(AuthenticodeVerifier.HasPublisher(Path.Combine(Path.GetTempPath(), "exo-missing.dll"), "Microsoft Corporation"));
}
