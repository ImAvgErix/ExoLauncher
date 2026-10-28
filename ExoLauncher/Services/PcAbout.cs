using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

/// <summary>
/// PC › Overview's About card: what this PC is, from Windows' own records, without WMI, administrator rights or the
/// network. The model (or, on a PC built from parts, the name Windows gives it), the processor's cores and the memory's
/// type, size and slots from SMBIOS (the table <see cref="PcHealth"/> reads), the motherboard, BIOS and Windows from the
/// registry, each fixed drive with its disk's model and kind as the storage driver describes it (asked with no access),
/// Secure Boot, the TPM and Memory integrity, and the connection in use. What can't change while Exo runs is read once;
/// drives and the connection each time. A part that can't be read is null, never a guess.
/// </summary>
internal static partial class PcAbout
{
    /// <summary>A fixed drive: its letter, its disk's model and kind, and its size and free space in GB (as Explorer counts them).</summary>
    internal sealed record Drive(string Letter, string? Model, string Kind, double TotalGb, double FreeGb);

    /// <summary>The fitted memory: total, type (DDR4, DDR5…), the speed it runs at and the kit's rating (MT/s), slots used and slots.</summary>
    internal sealed record Memory(int Gb, string? Type, int? Speed, int? Rated, int Used, int Slots);

    internal sealed record About(
        string? Name, string? Processor, int? Cores, int Threads,
        string? Graphics, double? VideoMemoryGb, string DirectX, bool? ResizableBar,
        int? MemoryGb, string? MemoryType, int? MemorySpeed, int? MemoryRated, int? MemorySlotsUsed, int? MemorySlots,
        string? Motherboard, string? Bios, string? BiosDate, string? Windows, string? WindowsVersion, string? Build,
        bool? SecureBoot, string? Tpm, bool? MemoryIntegrity,
        IReadOnlyList<Drive> Drives, string? Network = null, double? NetworkMbps = null);

    private static readonly Lazy<About> Once = new(ReadOnce, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static About Read()
    {
        var network = Connection();
        return Once.Value with { Drives = Drives(), Network = network?.Kind, NetworkMbps = network?.Mbps };
    }

    private static About ReadOnce()
    {
        var specs = SystemSpecs.Read();
        var raw = PcHealth.SmbiosRaw();
        var memory = ParseMemory(raw);
        string? model = null, board = null, bios = null, biosDate = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var boardProduct = Text(key, "BaseBoardProduct");
            board = Named(Text(key, "BaseBoardManufacturer"), boardProduct);
            model = Model(Text(key, "SystemManufacturer"), Text(key, "SystemProductName"), Text(key, "SystemVersion"), boardProduct);
            bios = Text(key, "BIOSVersion");
            biosDate = BiosDate(Text(key, "BIOSReleaseDate"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left out */ }
        string? windows = null, version = null, build = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var number = int.TryParse(Text(key, "CurrentBuildNumber"), out var n) ? n : Environment.OSVersion.Version.Build;
            windows = WindowsName(Text(key, "ProductName"), number);
            version = Text(key, "DisplayVersion");
            build = key?.GetValue("UBR") is int ubr ? $"{number}.{ubr}" : number.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left out */ }
        bool? secureBoot = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            secureBoot = key?.GetValue("UEFISecureBootEnabled") is int on ? on == 1 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* left out */ }
        return new About(
            model ?? HostName(), specs.Cpu, ParseCores(raw), Environment.ProcessorCount,
            specs.Gpu, specs.VramGb, SystemSpecs.Direct3D12(0xc200) ? "12 Ultimate" : specs.DirectX == 12 ? "12" : "11", ResizableBar(PcHealth.MainGpu()),
            memory?.Gb ?? (specs.RamGb > 0 ? specs.RamGb : null), memory?.Type, memory?.Speed, memory?.Rated, memory?.Used, memory?.Slots,
            board, bios, biosDate, windows, version, build, secureBoot, Tpm(), ControllerLab.MemoryIntegrityOn(), []);
    }

    private static string? Text(RegistryKey? key, string name) => (key?.GetValue(name) as string)?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>The PC's own name, as Windows' About page shows it (case kept, unlike the NetBIOS name).</summary>
    private static string? HostName()
    {
        try { return Dns.GetHostName() is { Length: > 0 } name ? name : null; }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    // ---- SMBIOS ----

    /// <summary>
    /// The memory SMBIOS lists (type 17): every slot, and of the fitted ones the total, the type, the speed the slowest
    /// runs at and the kit's rating (the larger of the firmware's and the part number's, as <see cref="PcHealth.SlowRam"/>).
    /// Null when the table lists no fitted module.
    /// </summary>
    internal static Memory? ParseMemory(ReadOnlySpan<byte> raw)
    {
        int slots = 0, mb = 0;
        var types = new List<string?>();
        PcHealth.Walk(raw, (type, s, _) =>
        {
            if (type != 17 || s.Length < 0x13) return;
            slots++;
            // Size: 0 an empty slot, 0xFFFF unknown, 0x7FFF says to read the extended size (MB) at 0x1C; bit 15 counts in KB.
            var size = PcHealth.U16(s, 0x0C);
            if (size is 0 or 0xFFFF) return;
            mb += size == 0x7FFF ? (int)(PcHealth.U32(s, 0x1C) & 0x7FFFFFFF) : (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size;
            types.Add(MemoryType(s[0x12]));
        });
        if (types.Count == 0) return null;
        var dimms = PcHealth.ParseDimms(raw).Where(d => d.Configured > 0).ToList();
        int? speed = dimms.Count > 0 ? dimms.Min(d => d.Configured) : null;
        int? rated = dimms.Count > 0 ? dimms.Min(d => Math.Max(d.Speed, PcHealth.RatedFromPart(d.Part) ?? 0)) : null;
        return new Memory((int)Math.Round(mb / 1024.0), types.Distinct().Count() == 1 ? types[0] : null, speed, rated > speed ? rated : speed, types.Count, slots);
    }

    /// <summary>SMBIOS's memory type code as people name it.</summary>
    internal static string? MemoryType(byte code) => code switch
    {
        0x12 => "DDR", 0x13 => "DDR2", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3",
        0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => null,
    };

    /// <summary>The processor's cores (SMBIOS type 4, populated sockets added up; 0xFF says to read the 3.0 word at 0x2A), or null.</summary>
    internal static int? ParseCores(ReadOnlySpan<byte> raw)
    {
        var cores = 0;
        PcHealth.Walk(raw, (type, s, _) =>
        {
            if (type != 4 || s.Length < 0x24 || (s[0x18] & 0x40) == 0) return;
            cores += s[0x23] == 0xFF ? PcHealth.U16(s, 0x2A) : s[0x23];
        });
        return cores > 0 ? cores : null;
    }

    // ---- Names ----

    // What board makers' firmware leaves in the system fields of a PC built from parts.
    private static readonly string[] Placeholders = ["System Product Name", "System Version", "System manufacturer", "To be filled by O.E.M.",
        "Default string", "Not Applicable", "None", "O.E.M.", "OEM", "Type1ProductConfigId", "All Series", "Standard PC", "Not Specified", "Undefined"];

    internal static bool Real([NotNullWhen(true)] string? value) =>
        value?.Trim() is { Length: > 0 } text && text.Any(char.IsLetterOrDigit) && !Placeholders.Any(p => text.Equals(p, StringComparison.OrdinalIgnoreCase));

    private static readonly (string Prefix, string Name)[] Makers =
    [
        ("ASUS", "ASUS"), ("Micro-Star", "MSI"), ("MSI", "MSI"), ("Gigabyte", "Gigabyte"), ("ASRock", "ASRock"), ("Biostar", "Biostar"),
        ("Dell", "Dell"), ("Alienware", "Alienware"), ("Lenovo", "Lenovo"), ("Hewlett", "HP"), ("HP", "HP"), ("Acer", "Acer"),
        ("Razer", "Razer"), ("Microsoft", "Microsoft"), ("Samsung", "Samsung"), ("Framework", "Framework"), ("Valve", "Valve"),
        ("Intel", "Intel"), ("NZXT", "NZXT"), ("EVGA", "EVGA"), ("Supermicro", "Supermicro"), ("LG Electronics", "LG"),
    ];

    /// <summary>A maker as people say it ("ASUSTeK COMPUTER INC." is ASUS, "Micro-Star International Co., Ltd." MSI), or null.</summary>
    internal static string? Maker(string? maker)
    {
        if (!Real(maker)) return null;
        var text = maker.Trim();
        foreach (var (prefix, name) in Makers)
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name;
        return CorporateTail().Replace(text, "").Trim() is { Length: > 0 } plain ? plain : text;
    }

    /// <summary>A maker and a product as one name, the maker once ("ASUS ROG STRIX B550-F GAMING WIFI II"), or null without a product.</summary>
    internal static string? Named(string? maker, string? product)
    {
        if (!Real(product)) return null;
        var name = product.Trim();
        return Maker(maker) is { } brand && !name.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? $"{brand} {name}" : name;
    }

    /// <summary>
    /// The PC's model as its maker names it ("ASUS ROG Zephyrus G14", "Lenovo Legion 5 15ACH6H"), or null on a PC built
    /// from parts: placeholders, or a product that is only the motherboard's (MSI's "MS-7C91", Gigabyte's board name).
    /// Lenovo keeps the machine type in the product ("82JQ") and the name in the version.
    /// </summary>
    internal static string? Model(string? maker, string? product, string? version, string? board)
    {
        var name = Maker(maker) == "Lenovo" && Real(version) ? version : product;
        if (!Real(name)) return null;
        if (Real(board) && board.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return Named(maker, name);
    }

    /// <summary>Windows' name: the registry still says "Windows 10" on Windows 11 (build 22000 and newer).</summary>
    internal static string? WindowsName(string? product, int build) =>
        string.IsNullOrWhiteSpace(product) ? null
        : build >= 22000 ? product.Trim().Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : product.Trim();

    /// <summary>The BIOS's release date ("01/04/2026" in the registry) as yyyy-MM-dd, or null.</summary>
    internal static string? BiosDate(string? text) =>
        DateTime.TryParseExact(text, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>Resizable BAR: on when the CPU sees all of the card's memory, off on a card that has it and doesn't; null on an integrated GPU, an older card or a Windows that can't say.</summary>
    internal static bool? ResizableBar(PcHealth.GpuBar? gpu) =>
        gpu is not { Uma: false } ? null : gpu.UploadHeap == true ? true : PcHealth.ReBarOff(gpu) ? false : null;

    // ---- Drives ----

    private static List<Drive> Drives()
    {
        var drives = new List<Drive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var letter = drive.Name.TrimEnd('\\');
                var (model, kind) = Disk(letter);
                drives.Add(new Drive(letter, model, kind, drive.TotalSize / 1073741824.0, drive.AvailableFreeSpace / 1073741824.0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ready */ }
        }
        return drives;
    }

    /// <summary>The model and kind of the disk a volume starts on, asked of the storage driver with no access (no administrator rights needed).</summary>
    private static (string? Model, string Kind) Disk(string letter)
    {
        using var volume = Open(@"\\.\" + letter);
        var extents = new byte[8 + 24 * 4]; // VOLUME_DISK_EXTENTS: a count, then DISK_EXTENTs of 24 bytes
        if (volume.IsInvalid || !DeviceIoControl(volume, 0x00560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, null, 0, extents, extents.Length, out _, IntPtr.Zero)
            || BitConverter.ToInt32(extents, 0) < 1) return (null, Kind(0, null));
        using var disk = Open(@"\\.\PhysicalDrive" + BitConverter.ToUInt32(extents, 8));
        if (disk.IsInvalid) return (null, Kind(0, null));
        var (model, bus) = ParseDevice(Query(disk, 0 /* StorageDeviceProperty */, 1024));
        // DEVICE_SEEK_PENALTY_DESCRIPTOR: a spinning disk pays to seek.
        bool? seeks = Query(disk, 7 /* StorageDeviceSeekPenaltyProperty */, 12) is { } penalty ? penalty[8] != 0 : null;
        return (model, Kind(bus, seeks));
    }

    private static byte[]? Query(SafeFileHandle disk, int property, int size)
    {
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY: the property, PropertyStandardQuery (0), a spare byte
        BitConverter.GetBytes(property).CopyTo(query, 0);
        var answer = new byte[size];
        return DeviceIoControl(disk, 0x002D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero) && returned >= 12 ? answer : null;
    }

    /// <summary>
    /// A STORAGE_DEVICE_DESCRIPTOR's model (its vendor before its product, unless the vendor is a bus's word or already in
    /// the product) and bus type (at 28).
    /// </summary>
    internal static (string? Model, int Bus) ParseDevice(byte[]? d)
    {
        if (d is null || d.Length < 36) return (null, 0);
        string? Field(int at)
        {
            var offset = BitConverter.ToInt32(d, at);
            if (offset <= 0 || offset >= d.Length) return null;
            var end = Array.IndexOf(d, (byte)0, offset);
            var text = Encoding.ASCII.GetString(d, offset, (end < 0 ? d.Length : end) - offset).Trim();
            return text.Length > 0 ? text : null;
        }
        var (vendor, product) = (Field(12), Field(16));
        var model = vendor is null or "ATA" or "NVMe" or "SCSI" || product?.Contains(vendor, StringComparison.OrdinalIgnoreCase) == true ? product : product is null ? null : $"{vendor} {product}";
        return (model, BitConverter.ToInt32(d, 28));
    }

    /// <summary>A disk's kind from its bus (STORAGE_BUS_TYPE) and, on SATA and the rest, whether it pays to seek.</summary>
    internal static string Kind(int bus, bool? seeks) => bus switch
    {
        0x11 => "NVMe SSD",
        0x07 => "USB drive",
        0x0C or 0x0D => "Memory card",
        0x08 => "RAID",
        0x0E or 0x0F => "Virtual disk",
        0x10 => "Storage Spaces",
        _ => seeks switch { true => "Hard drive", false => "SSD", null => "Drive" },
    };

    // ---- Security and connection ----

    /// <summary>The TPM's version ("2.0", "1.2"), "None" when the PC has none, null when Windows can't say.</summary>
    private static string? Tpm()
    {
        try
        {
            var result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info);
            if (result == 0x8028400F) return "None"; // TBS_E_TPM_NOT_FOUND
            return result != 0 ? null : info.TpmVersion switch { 1 => "1.2", 2 => "2.0", _ => null };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>The connection Windows routes through: an Ethernet or Wi-Fi adapter that's up with a gateway (Ethernet first, no virtual switches), its kind and link speed.</summary>
    private static (string Kind, double Mbps)? Connection()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Speed > 0
                    && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211
                    && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Select(n => ((string, double)?)(n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet", n.Speed / 1_000_000.0))
                .FirstOrDefault();
        }
        catch (NetworkInformationException) { return null; }
    }

    // ", Inc.", " Co., Ltd.", " Corporation", " Technology Co., Ltd." and the like.
    [GeneratedRegex(@"[,.]?\s+(?:Co\.?,?\s*)?(?:Ltd|Inc|Corporation|Corp|LLC|GmbH|Technology|Computer|International)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex CorporateTail();

    /// <summary>No access asked: these IOCTLs only describe the device.</summary>
    private static SafeFileHandle Open(string path) => CreateFile(path, 0, 0x3 /* share read, write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
