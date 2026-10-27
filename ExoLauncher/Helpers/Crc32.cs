namespace ExoLauncher.Helpers;

/// <summary>CRC-32 (IEEE 802.3, reflected, 0xEDB88320): what PNG chunks and a DualSense Edge's profiles are checked with.</summary>
internal static class Crc32
{
    internal static uint Of(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
