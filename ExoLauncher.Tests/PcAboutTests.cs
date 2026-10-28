using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways PC › Overview's About card could go wrong silently:
/// - memory is misread from SMBIOS: empty slots counted as fitted (or not counted as slots), an extended or KB-sized
///   module added wrongly, two kinds of module called one type, a kit whose firmware reports the JEDEC speed not shown
///   as rated for its part number's speed;
/// - the cores are misread: an empty socket added, a 3.0 word count (over 254 cores) ignored, a short structure throwing;
/// - a PC built from parts is titled by its firmware's placeholder ("System Product Name") or its board's code
///   ("MS-7C91"), a maker's model loses its maker or repeats it, Lenovo's machine type shows instead of its name;
/// - Windows 11 is called Windows 10 (the registry still says so), or Windows 10 is renamed;
/// - the BIOS date is read in the wrong order;
/// - a drive's kind is wrong (an NVMe or SATA SSD called a hard drive) or its model takes a bus's word ("ATA");
/// - Resizable BAR is called off on an integrated GPU or a card that can't have it.
/// </summary>
public sealed class PcAboutTests
{
    // ---- SMBIOS ----

    private static byte[] Type17(ushort size, byte type = 0x1A, ushort speed = 3600, ushort configured = 3600, byte partIndex = 0, uint extSize = 0)
    {
        var s = new byte[0x5C];
        s[0] = 17; s[1] = 0x5C;
        BitConverter.GetBytes(size).CopyTo(s, 0x0C);
        s[0x0E] = 0x09;
        s[0x12] = type;
        BitConverter.GetBytes(speed).CopyTo(s, 0x15);
        s[0x1A] = partIndex;
        BitConverter.GetBytes(extSize).CopyTo(s, 0x1C);
        BitConverter.GetBytes(configured).CopyTo(s, 0x20);
        return s;
    }

    private static byte[] Type4(byte cores, bool populated = true, ushort cores2 = 0)
    {
        var s = new byte[0x30];
        s[0] = 4; s[1] = 0x30;
        s[0x18] = (byte)(populated ? 0x41 : 0x00);
        s[0x23] = cores;
        BitConverter.GetBytes(cores2).CopyTo(s, 0x2A);
        return s;
    }

    private static byte[] Strings(params string[] strings) =>
        strings.Length == 0 ? [0, 0] : [.. strings.SelectMany(text => Encoding.ASCII.GetBytes(text).Append((byte)0)), 0];

    private static byte[] Table(params byte[][] parts)
    {
        var body = parts.SelectMany(part => part).ToArray();
        return [0, 3, 3, 0, .. BitConverter.GetBytes((uint)body.Length), .. body];
    }

    [Fact]
    public void CountsEverySlotAndAddsUpTheFittedModules()
    {
        var raw = Table(
            Type17(0), Strings(),
            Type17(8192), Strings(),
            Type17(0), Strings(),
            Type17(8192), Strings(),
            [127, 4, 0, 0], Strings());
        Assert.Equal(new PcAbout.Memory(16, "DDR4", 3600, 3600, 2, 4), PcAbout.ParseMemory(raw));
    }

    [Fact]
    public void ReadsTheExtendedSizeAndNamesNoTypeForMixedModules()
    {
        var raw = Table(Type17(0x7FFF, 0x22, 6000, 6000, extSize: 32768), Strings(), Type17(0x8000 | 512 * 1024 / 1024), Strings());
        var memory = PcAbout.ParseMemory(raw)!;
        Assert.Equal(32, memory.Gb);
        Assert.Null(memory.Type); // DDR5 beside DDR4: no one type
    }

    [Fact]
    public void AKitAtItsJedecSpeedIsRatedByItsPartNumber()
    {
        var raw = Table(Type17(8192, speed: 2133, configured: 2133, partIndex: 1), Strings("CMK16GX4M2D3600C18"));
        var memory = PcAbout.ParseMemory(raw)!;
        Assert.Equal(2133, memory.Speed);
        Assert.Equal(3600, memory.Rated);
    }

    [Fact]
    public void NoFittedModuleIsNoMemory()
    {
        Assert.Null(PcAbout.ParseMemory(Table(Type17(0), Strings())));
        Assert.Null(PcAbout.ParseMemory([1, 2, 3]));
    }

    [Fact]
    public void AddsThePopulatedSocketsCores()
    {
        Assert.Equal(6, PcAbout.ParseCores(Table(Type4(6), Strings(), Type4(16, populated: false), Strings())));
        Assert.Equal(288, PcAbout.ParseCores(Table(Type4(0xFF, cores2: 288), Strings())));
        Assert.Null(PcAbout.ParseCores(Table([4, 0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], Strings())));
    }

    [Theory]
    [InlineData(0x1A, "DDR4")]
    [InlineData(0x22, "DDR5")]
    [InlineData(0x23, "LPDDR5")]
    [InlineData(0x02, null)]
    public void NamesTheMemoryType(byte code, string? name) => Assert.Equal(name, PcAbout.MemoryType(code));

    // ---- Names ----

    [Theory]
    [InlineData("ASUS", "System Product Name", "System Version", "ROG STRIX B550-F GAMING WIFI II", null)]
    [InlineData("Micro-Star International Co., Ltd.", "MS-7C91", "1.0", "MAG B550 TOMAHAWK (MS-7C91)", null)]
    [InlineData("Gigabyte Technology Co., Ltd.", "B550 AORUS ELITE", "-CF", "B550 AORUS ELITE", null)]
    [InlineData("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", null, "B650M Pro RS", null)]
    [InlineData("Dell Inc.", "XPS 15 9520", null, "0RH1JY", "Dell XPS 15 9520")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG Zephyrus G14 GA402RJ", "1.0", "GA402RJ", "ASUS ROG Zephyrus G14 GA402RJ")]
    [InlineData("LENOVO", "82JQ", "Legion 5 15ACH6H", "LNVNB161216", "Lenovo Legion 5 15ACH6H")]
    [InlineData("Razer", "Razer Blade 16", null, "DA-RZ09", "Razer Blade 16")]
    public void TitlesAMakersModelAndNotAPcBuiltFromParts(string? maker, string? product, string? version, string? board, string? model) =>
        Assert.Equal(model, PcAbout.Model(maker, product, version, board));

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG STRIX B550-F GAMING WIFI II", "ASUS ROG STRIX B550-F GAMING WIFI II")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI MAG B550 TOMAHAWK", "MSI MAG B550 TOMAHAWK")]
    [InlineData("Foo Technology Co., Ltd.", "X1", "Foo X1")]
    [InlineData(null, "X1", "X1")]
    [InlineData("ASUS", "Default string", null)]
    public void NamesTheMotherboardWithItsMakerOnce(string? maker, string? product, string? name) => Assert.Equal(name, PcAbout.Named(maker, product));

    [Theory]
    [InlineData("Windows 10 Pro", 26300, "Windows 11 Pro")]
    [InlineData("Windows 10 Home", 22000, "Windows 11 Home")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData(null, 26300, null)]
    public void CallsWindows11ByItsName(string? product, int build, string? name) => Assert.Equal(name, PcAbout.WindowsName(product, build));

    [Theory]
    [InlineData("01/04/2026", "2026-01-04")]
    [InlineData("12/31/2019", "2019-12-31")]
    [InlineData("2026-01-04", null)]
    [InlineData(null, null)]
    public void ReadsTheBiosDateMonthFirst(string? text, string? day) => Assert.Equal(day, PcAbout.BiosDate(text));

    // ---- Drives ----

    [Theory]
    [InlineData(0x11, null, "NVMe SSD")]
    [InlineData(0x0B, false, "SSD")]
    [InlineData(0x0B, true, "Hard drive")]
    [InlineData(0x07, false, "USB drive")]
    [InlineData(0x0B, null, "Drive")]
    public void NamesTheDiskKind(int bus, bool? seeks, string kind) => Assert.Equal(kind, PcAbout.Kind(bus, seeks));

    private static byte[] Descriptor(string? vendor, string? product, int bus)
    {
        var d = new byte[256];
        var at = 40;
        void Put(int field, string? text)
        {
            if (text is null) return;
            BitConverter.GetBytes(at).CopyTo(d, field);
            Encoding.ASCII.GetBytes(text).CopyTo(d, at);
            at += text.Length + 1;
        }
        Put(12, vendor);
        Put(16, product);
        BitConverter.GetBytes(bus).CopyTo(d, 28);
        return d;
    }

    [Theory]
    [InlineData(null, "Samsung SSD 990 PRO with Heatsink 2TB", "Samsung SSD 990 PRO with Heatsink 2TB")]
    [InlineData("NVMe", "WD_BLACK SN850X 2000GB", "WD_BLACK SN850X 2000GB")]
    [InlineData("ATA     ", "WDC WD20EZRZ-00Z5HB0", "WDC WD20EZRZ-00Z5HB0")]
    [InlineData("Samsung", "Portable SSD T5", "Samsung Portable SSD T5")]
    [InlineData("Samsung", "Samsung SSD 870 EVO 1TB", "Samsung SSD 870 EVO 1TB")]
    public void TakesTheDisksModelWithoutABussWord(string? vendor, string product, string model)
    {
        var (name, bus) = PcAbout.ParseDevice(Descriptor(vendor, product, 0x11));
        Assert.Equal(model, name);
        Assert.Equal(0x11, bus);
    }

    [Fact]
    public void AShortOrBrokenDescriptorHasNoModel()
    {
        Assert.Equal(((string?)null, 0), PcAbout.ParseDevice(null));
        Assert.Equal(((string?)null, 0), PcAbout.ParseDevice(new byte[20]));
        var broken = Descriptor(null, null, 0x0B);
        BitConverter.GetBytes(9999).CopyTo(broken, 16);
        Assert.Null(PcAbout.ParseDevice(broken).Model);
    }

    // ---- Resizable BAR ----

    [Fact]
    public void ResizableBarSpeaksOnlyForACardThatCanHaveIt()
    {
        Assert.Null(PcAbout.ResizableBar(new PcHealth.GpuBar("AMD Radeon(TM) Graphics", true, false)));
        Assert.True(PcAbout.ResizableBar(new PcHealth.GpuBar("NVIDIA GeForce RTX 3070", false, true)));
        Assert.False(PcAbout.ResizableBar(new PcHealth.GpuBar("NVIDIA GeForce RTX 3070", false, false)));
        Assert.Null(PcAbout.ResizableBar(new PcHealth.GpuBar("NVIDIA GeForce GTX 1080", false, false)));
        Assert.Null(PcAbout.ResizableBar(new PcHealth.GpuBar("NVIDIA GeForce RTX 3070", false, null)));
        Assert.Null(PcAbout.ResizableBar(null));
    }
}
