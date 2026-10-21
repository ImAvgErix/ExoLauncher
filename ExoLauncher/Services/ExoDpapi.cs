using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ExoLauncher.Services;

/// <summary>
/// DPAPI CurrentUser wrap for the keys Exo saves on this PC. Any process
/// running as this Windows user can decrypt the blob. Additional entropy is
/// not a secret — it ships in the binary — and only stops a generic DPAPI dump
/// from decoding Exo's files without knowing the entropy. Unpackaged WinUI has
/// no app-private locker. Malware as this user can steal the keys.
/// </summary>
internal static class ExoDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;

    // Not a secret. Bound into the binary on purpose so the blob is not a
    // vanilla CurrentUser DPAPI payload. Saved keys were written with it;
    // changing it makes every saved key unreadable.
    private static readonly byte[] Entropy =
        "ExoLauncher.auth.bin.v1"u8.ToArray();
    private static readonly byte[] ZeroBlock = new byte[4096];

    internal static byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Crypt(plaintext, protect: true);
    }

    internal static byte[] Unprotect(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return Crypt(blob, protect: false);
    }

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var entropy = new DataBlob();
        var output = new DataBlob();
        try
        {
            input = ToBlob(data);
            entropy = ToBlob(Entropy);
            var ok = protect
                ? CryptProtectData(
                    ref input,
                    "ExoLauncher",
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output);
            if (!ok || output.PbData == IntPtr.Zero || output.CbData < 0)
                throw new CryptographicException("DPAPI could not protect the key.");

            var result = new byte[output.CbData];
            Marshal.Copy(output.PbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            FreeBlob(ref input);
            FreeBlob(ref entropy);
            if (output.PbData != IntPtr.Zero)
            {
                ZeroUnmanaged(output.PbData, output.CbData);
                LocalFree(output.PbData);
            }
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { CbData = data.Length, PbData = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, blob.PbData, data.Length);
            return blob;
        }
        catch
        {
            FreeBlob(ref blob);
            throw;
        }
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.PbData == IntPtr.Zero) return;
        ZeroUnmanaged(blob.PbData, blob.CbData);
        Marshal.FreeHGlobal(blob.PbData);
        blob.PbData = IntPtr.Zero;
        blob.CbData = 0;
    }

    internal static void ZeroUnmanaged(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length <= 0) return;
        for (var offset = 0; offset < length;)
        {
            var count = Math.Min(ZeroBlock.Length, length - offset);
            Marshal.Copy(ZeroBlock, 0, IntPtr.Add(pointer, offset), count);
            offset += count;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
