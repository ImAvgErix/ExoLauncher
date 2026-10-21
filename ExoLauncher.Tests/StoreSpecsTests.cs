using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a game's requirements could read wrong on its Store page (all strings are Steam's own, from real games):
/// - a page shape the parser does not know (label inside the &lt;strong&gt;, one paragraph per column, "OS *:")
///   drops rows, so the box shows less than Steam does;
/// - a card or processor written loosely ("Nvidia 2060 Super", "GTX 970 / 1060", "AMD r5 3600", "1080Ti")
///   is missed, or a size ("6GB", "1080p") is taken for a model;
/// - this PC is judged against the other maker's part when the game names one from its own maker;
/// - a laptop card counts as the desktop card of its name;
/// - "SSD recommended" reads as SSD required, or a storage size only in the notes is lost;
/// - the oldest Windows accepted is not the one compared ("Windows 7/8/10" asks for 7);
/// - a part that cannot be read is judged anyway (a verdict out of nothing), or loses Steam's words.
/// </summary>
public sealed class StoreSpecsTests
{
    private static readonly StoreService.ThisPc Desktop = new("AMD Ryzen 5 5600X", "NVIDIA GeForce RTX 3070", 16, 11, 412.6);

    private const string CyberpunkMin = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li>Requires a 64-bit processor and operating system<br></li><li><strong>OS:</strong> 64-bit Windows 10<br></li><li><strong>Processor:</strong> Core i7-6700 or Ryzen 5 1600<br></li><li><strong>Memory:</strong> 12 GB RAM<br></li><li><strong>Graphics:</strong> GeForce GTX 1060 6GB or Radeon RX 580 8GB or Arc A380<br></li><li><strong>DirectX:</strong> Version 12<br></li><li><strong>Storage:</strong> 70 GB available space<br></li><li><strong>Additional Notes:</strong> SSD required. Attention: In this game you will encounter a variety of visual effects that may provide seizures or loss of consciousness in a minority of people.</li></ul>";
    private const string CyberpunkRec = "<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li>Requires a 64-bit processor and operating system<br></li><li><strong>OS:</strong> 64-bit Windows 10<br></li><li><strong>Processor:</strong> Core i7-12700 or Ryzen 7 7800X3D<br></li><li><strong>Memory:</strong> 16 GB RAM<br></li><li><strong>Graphics:</strong> GeForce RTX 2060 SUPER or Radeon RX 5700 XT or Arc A770<br></li><li><strong>DirectX:</strong> Version 12<br></li><li><strong>Storage:</strong> 70 GB available space<br></li><li><strong>Additional Notes:</strong> SSD required.</li></ul>";

    private const string PalworldMin = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li>Requires a 64-bit processor and operating system<br></li><li><strong>OS:</strong> Windows 10 (64-Bit)<br></li><li><strong>Processor:</strong> Intel Core i5-9400F<br></li><li><strong>Memory:</strong> 16 GB RAM<br></li><li><strong>Graphics:</strong> GeForce GTX 1660<br></li><li><strong>DirectX:</strong> Version 11<br></li><li><strong>Network:</strong> Broadband Internet connection<br></li><li><strong>Storage:</strong> 40 GB available space<br></li><li><strong>Additional Notes:</strong> SSD Required - Performance may vary depending on the amount of active Pals.</li></ul>";
    private const string PalworldRec = "<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li>Requires a 64-bit processor and operating system<br></li><li><strong>OS:</strong> Windows 11 (64-Bit)<br></li><li><strong>Processor:</strong> Intel Core i5-12400 / AMD Ryzen 5 5600X<br></li><li><strong>Memory:</strong> 32 GB RAM<br></li><li><strong>Graphics:</strong> GeForce RTX 3060 Ti / Radeon RX 6700 XT<br></li><li><strong>DirectX:</strong> Version 11<br></li><li><strong>Network:</strong> Broadband Internet connection<br></li><li><strong>Storage:</strong> 40 GB available space<br></li><li><strong>Additional Notes:</strong> SSD Required - Performance may vary depending on the amount of active Pals.</li></ul>";

    private static StoreService.SpecRow Row(StoreService.SpecFit fit, string label) => Assert.Single(fit.Rows, r => r.Label == label);

    [Fact]
    public void CyberpunkReadsShortAndIsJudgedPartByPart()
    {
        var fit = StoreService.FitSpecs(StoreService.ParseSpecs(CyberpunkMin, CyberpunkRec), Desktop);

        Assert.Equal(["OS", "Processor", "Graphics", "Memory", "Storage", "DirectX"], fit.Rows.Select(r => r.Label));
        Assert.Equal(("Windows 10", "Windows 10"), (Row(fit, "OS").Minimum, Row(fit, "OS").Recommended));
        Assert.Equal(("i7-6700 · Ryzen 5 1600", "i7-12700 · Ryzen 7 7800X3D"), (Row(fit, "Processor").Minimum, Row(fit, "Processor").Recommended));
        Assert.Equal(("GTX 1060 · RX 580 · Arc A380", "RTX 2060 Super · RX 5700 XT · Arc A770"), (Row(fit, "Graphics").Minimum, Row(fit, "Graphics").Recommended));
        Assert.Equal(("12 GB", "16 GB"), (Row(fit, "Memory").Minimum, Row(fit, "Memory").Recommended));
        Assert.Equal("70 GB SSD", Row(fit, "Storage").Minimum);
        Assert.Equal("DirectX 12", Row(fit, "DirectX").Minimum);

        Assert.Equal(("RTX 3070", "recommended"), (Row(fit, "Graphics").Mine, Row(fit, "Graphics").Meets));
        // A Ryzen 5 5600X against the Ryzen the game names (a 7800X3D), not the Intel one.
        Assert.Equal(("Ryzen 5 5600X", "minimum"), (Row(fit, "Processor").Mine, Row(fit, "Processor").Meets));
        Assert.Equal(("16 GB", "recommended"), (Row(fit, "Memory").Mine, Row(fit, "Memory").Meets));
        Assert.Equal(("412 GB free", "recommended"), (Row(fit, "Storage").Mine, Row(fit, "Storage").Meets));
        Assert.Null(Row(fit, "DirectX").Meets);
        Assert.Equal("minimum", fit.Verdict);
    }

    [Fact]
    public void AnOlderCardIsBelowMinimumAndSoIsThePc()
    {
        var pc = new StoreService.ThisPc("Intel Core i5-8400", "NVIDIA GeForce GTX 960", 8, 10, 30);
        var fit = StoreService.FitSpecs(StoreService.ParseSpecs(PalworldMin, PalworldRec), pc);

        Assert.Equal("below", Row(fit, "Graphics").Meets);
        Assert.Equal("below", Row(fit, "Memory").Meets);
        Assert.Equal("minimum", Row(fit, "OS").Meets); // Windows 10 against 10, then 11
        Assert.Equal(("40 GB SSD", "below"), (Row(fit, "Storage").Minimum, Row(fit, "Storage").Meets));
        Assert.Equal("Broadband", Row(fit, "Network").Minimum);
        Assert.Equal("below", fit.Verdict);
    }

    // "DirectX: Version 12" and "Network: Broadband Internet connection" are judged too: the card's DirectX and whether Windows is online.
    [Fact]
    public void DirectXAndTheNetworkAreJudgedAgainstThisPc()
    {
        const string min = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> Windows 10<br></li><li><strong>DirectX:</strong> Version 11<br></li><li><strong>Network:</strong> Broadband Internet connection<br></li></ul>";
        const string rec = "<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> Windows 10<br></li><li><strong>DirectX:</strong> Version 12<br></li><li><strong>Network:</strong> Broadband Internet connection<br></li></ul>";
        var specs = StoreService.ParseSpecs(min, rec);

        var modern = StoreService.FitSpecs(specs, Desktop with { DirectX = 12, Online = true });
        Assert.Equal(("DirectX 12", "recommended"), (Row(modern, "DirectX").Mine, Row(modern, "DirectX").Meets));
        Assert.Equal(("Online", "recommended"), (Row(modern, "Network").Mine, Row(modern, "Network").Meets));

        var older = StoreService.FitSpecs(specs, Desktop with { DirectX = 11, Online = false });
        Assert.Equal("minimum", Row(older, "DirectX").Meets);
        Assert.Equal(("Offline", "below"), (Row(older, "Network").Mine, Row(older, "Network").Meets));
    }

    // Counter-Strike 2 asks for "Video card must be 1 GB or more": no card to compare, so the card's memory is.
    [Fact]
    public void ACardAskedForOnlyByItsMemoryIsJudgedByThePcsVideoMemory()
    {
        const string min = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> Windows 10<br></li><li><strong>Processor:</strong> 4 hardware CPU threads - Intel Core i5 750 or higher<br></li><li><strong>Memory:</strong> 8 GB RAM<br></li><li><strong>Graphics:</strong> Video card must be 1 GB or more and should be a DirectX 11-compatible with support for Shader Model 5.0<br></li></ul>";
        var specs = StoreService.ParseSpecs(min, null);
        Assert.Equal(("1 GB VRAM", "minimum"), (Row(StoreService.FitSpecs(specs, Desktop with { VramGb = 8 }), "Graphics").Minimum, Row(StoreService.FitSpecs(specs, Desktop with { VramGb = 8 }), "Graphics").Meets));
        Assert.Equal("below", Row(StoreService.FitSpecs(specs, Desktop with { VramGb = 0.5 }), "Graphics").Meets);
        Assert.Null(Row(StoreService.FitSpecs(specs, Desktop with { VramGb = null }), "Graphics").Meets);
    }

    [Fact]
    public void ACardFromTheOtherMakerIsComparedWithThatMakersCard()
    {
        var radeon = Desktop with { Gpu = "AMD Radeon RX 6700 XT" };
        var fit = StoreService.FitSpecs(StoreService.ParseSpecs(CyberpunkMin, CyberpunkRec), radeon);
        Assert.Equal(("RX 6700 XT", "recommended"), (Row(fit, "Graphics").Mine, Row(fit, "Graphics").Meets));
    }

    [Fact]
    public void ALaptopCardCountsForLessThanTheDesktopCard()
    {
        var laptop = Desktop with { Gpu = "NVIDIA GeForce RTX 3060 Laptop GPU", Cpu = "12th Gen Intel Core i7-12700H" };
        var fit = StoreService.FitSpecs(StoreService.ParseSpecs(CyberpunkMin, CyberpunkRec), laptop);
        Assert.Equal(("RTX 3060", "minimum"), (Row(fit, "Graphics").Mine, Row(fit, "Graphics").Meets));
        Assert.Equal("i7-12700H", Row(fit, "Processor").Mine);
    }

    [Fact]
    public void TerrariaWithLabelsInsideTheBoldReadsEveryRow()
    {
        const string min = "<h2 class=\"bb_tag\" ><strong>REQUIRED</strong></h2><ul class=\"bb_ul\"><li><strong>OS: Windows Xp, Vista, 7, 8/8.1, 10</strong> <br>\t\t\t</li><li><strong>Processor: 2.0 Ghz</strong> <br>\t\t\t</li><li><strong>Memory: 2.5GB</strong><br>\t\t\t</li><li><strong>Hard Disk Space: 200MB </strong> \t<br>\t\t\t</li><li><strong>Video Card: 128mb Video Memory, capable of Shader Model 2.0+</strong> <br>\t\t\t</li><li><strong>DirectX®: 9.0c or Greater</strong> \t<br>\t\t\t</li></ul>";
        const string rec = "<h2 class=\"bb_tag\" ><strong>RECOMMENDED</strong></h2><ul class=\"bb_ul\"><li><strong>OS: Windows 7, 8/8.1, 10</strong> <br>\t\t\t</li><li><strong>Processor: Dual Core 3.0 Ghz</strong> <br>\t\t\t</li><li><strong>Memory: 4GB</strong><br>\t\t\t</li><li><strong>Hard Disk Space: 200MB </strong> \t<br>\t\t\t</li><li><strong>Video Card: 256mb Video Memory, capable of Shader Model 2.0+</strong> <br>\t\t\t</li><li><strong>DirectX®: 9.0c or Greater</strong> \t<br>\t\t\t</li></ul>";
        var fit = StoreService.FitSpecs(StoreService.ParseSpecs(min, rec), Desktop);

        Assert.Equal(["OS", "Processor", "Graphics", "Memory", "Storage", "DirectX"], fit.Rows.Select(r => r.Label));
        Assert.Equal(("Windows XP", "Windows 7"), (Row(fit, "OS").Minimum, Row(fit, "OS").Recommended));
        Assert.Equal(("2.5 GB", "4 GB"), (Row(fit, "Memory").Minimum, Row(fit, "Memory").Recommended));
        Assert.Equal("200 MB", Row(fit, "Storage").Minimum);
        Assert.Equal(("128 MB VRAM", "256 MB VRAM"), (Row(fit, "Graphics").Minimum, Row(fit, "Graphics").Recommended));
        Assert.Equal("DirectX 9.0c", Row(fit, "DirectX").Minimum);
        // Steam's words where there is no model to read, and no judgment.
        Assert.Equal(("2.0 Ghz", null), (Row(fit, "Processor").Minimum, Row(fit, "Processor").Meets));
        Assert.Null(Row(fit, "Graphics").Meets);
        Assert.Null(fit.Verdict);
    }

    [Fact]
    public void HalfLifesTwoParagraphsSplitIntoBothColumns()
    {
        const string min = "\n\t\t\t<p><strong>Minimum:</strong> 500 mhz processor, 96mb ram, 16mb video card, Windows XP, Mouse, Keyboard, Internet Connection<br /></p>\n\t\t\t<p><strong>Recommended:</strong> 800 mhz processor, 128mb ram, 32mb+ video card, Windows XP, Mouse, Keyboard, Internet Connection<br /></p>\n\t\t\t";
        var specs = StoreService.ParseSpecs(min, null);

        Assert.Equal(["Processor", "Memory", "Graphics", "OS", "Network"], specs.Select(s => s.Label));
        var fit = StoreService.FitSpecs(specs, Desktop);
        Assert.Equal(("96 MB", "128 MB"), (Row(fit, "Memory").Minimum, Row(fit, "Memory").Recommended));
        Assert.Equal(("16 MB VRAM", "32 MB VRAM"), (Row(fit, "Graphics").Minimum, Row(fit, "Graphics").Recommended));
        Assert.Equal("recommended", Row(fit, "Memory").Meets);
    }

    [Fact]
    public void LooselyWrittenCardsAreRead()
    {
        // Dota 2, DOOM Eternal, Call of Duty, Baldur's Gate 3, GTA V, Civilization VI, Hades II.
        Assert.Equal("GeForce 8600 · HD 2600", Graphics("NVIDIA GeForce 8600/9600GT, ATI/AMD Radeon HD2600/3600"));
        Assert.Equal("GTX 1050 Ti · R9 280", Graphics("NVIDIA GeForce GTX 1050Ti (4GB), GTX 1060 (3GB), GTX 1650 (4GB) or AMD Radeon R9 280(3GB), AMD Radeon R9 290 (4GB), RX 470 (4GB)"));
        Assert.Equal("RX 470 · GTX 970 · Arc A580", Graphics("AMD Radeon™ RX 470, NVIDIA® GeForce® GTX 970 / 1060 or Intel® Arc™ A580"));
        Assert.Equal("RTX 2060 Super · RX 5700 XT · Arc A580", Graphics("Nvidia 2060 Super / RX 5700 XT / Intel Arc A580 (8GB+ of VRAM)"));
        Assert.Equal("GeForce 9800 · HD 4870", Graphics("NVIDIA 9800 GT 1GB / AMD HD 4870 1GB (DX 10, 10.1, 11)"));
        Assert.Equal("HD 5570 · GTX 450 · Intel HD 530", Graphics("1 GB &amp; AMD 5570 or nVidia 450 or Intel Integrated Graphics 530"));
        Assert.Equal("GTX 950 · R7 360 · Intel HD 630", Graphics("GeForce GTX 950, Radeon R7 360, or Intel HD Graphics 630"));
        Assert.Equal("RTX 2060 Super · RX 5700 XT · Arc A750", Graphics("NVIDIA GeForce RTX 2060 (Super) / AMD RX 5700-XT / Intel Arc A750"));
        Assert.Equal("GTX 1080 Ti · RX 5700 XT", Graphics("NVIDIA GeForce 1080Ti  or AMD Radeon RX 5700 XT"));
    }

    [Fact]
    public void LooselyWrittenProcessorsAreRead()
    {
        Assert.Equal("i5-4690 · FX-8350", Processor("Intel I5 4690 / AMD FX 8350 / Snapdragon X Elite"));
        Assert.Equal("i7-8700K · Ryzen 5 3600", Processor("Intel i7 8700K / AMD r5 3600"));
        Assert.Equal("i5-4430 · FX-6300", Processor("Intel Core i5-4430 / AMD FX-6300"));
        Assert.Equal("Ryzen 5 1600 · i5-7600K", Processor("AMD Ryzen 5-1600 / Intel Core i5-7600K"));
        Assert.Equal("Core 2 Quad Q6600 · Phenom", Processor("Intel Core 2 Quad CPU Q6600 @ 2.40GHz (4 CPUs) / AMD Phenom 9850 Quad-Core Processor (4 CPUs) @ 2.5GHz"));
        Assert.Equal("i5-750", Processor("4 hardware CPU threads - Intel® Core™ i5 750 or higher"));
        Assert.Equal("Ryzen 5 5600X · i5-12400", Processor("Ryzen 5 5600X﻿ or ﻿i5-12400 ﻿or equivalent performance, 6 physical cores minimum"));
        Assert.Equal("Quad core 4Ghz+ from 2020", Processor("Quad core 4Ghz+ from 2020 or newer"));
    }

    [Fact]
    public void StorageAndSsdComeFromTheNotesToo()
    {
        // Call of Duty: no storage line, the size in the notes.
        const string cod = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> Windows® 10 64 Bit (latest update)<br></li><li><strong>Memory:</strong> 8 GB RAM<br></li><li><strong>Additional Notes:</strong> SSD with 161 GB available space at launch</li></ul>";
        Assert.Equal("161 GB SSD", Row(StoreService.FitSpecs(StoreService.ParseSpecs(cod, null), Desktop), "Storage").Minimum);

        // The Witcher 3: "60 SSD GB available space".
        const string witcher = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> 64-bit Windows 11<br></li><li><strong>Storage:</strong> 60 SSD GB available space<br></li></ul>";
        Assert.Equal("60 GB SSD", Row(StoreService.FitSpecs(StoreService.ParseSpecs(witcher, null), Desktop), "Storage").Minimum);

        // Hogwarts Legacy: preferred at Minimum, required at Recommended.
        const string min = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>Storage:</strong> 85 GB available space<br></li><li><strong>Additional Notes:</strong> SSD (Preferred), HDD (Supported), 720p/30 fps, Low Quality Settings</li></ul>";
        const string rec = "<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li><strong>Storage:</strong> 85 GB available space<br></li><li><strong>Additional Notes:</strong> SSD, 1080p/60 fps, High Quality Settings</li></ul>";
        var storage = Row(StoreService.FitSpecs(StoreService.ParseSpecs(min, rec), Desktop), "Storage");
        Assert.Equal(("85 GB", "85 GB SSD"), (storage.Minimum, storage.Recommended));
    }

    [Fact]
    public void TheOldestWindowsAcceptedIsTheOneCompared()
    {
        const string civ = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>OS *:</strong> Windows 7x64 / Windows 8.1x64 / Windows 10x64<br></li></ul>";
        var fit = StoreService.FitSpecs(StoreService.ParseSpecs(civ, null), Desktop with { Windows = 10 });
        Assert.Equal(("Windows 7", "Windows 10", "minimum"), (Row(fit, "OS").Minimum, Row(fit, "OS").Mine, Row(fit, "OS").Meets));

        const string monster = "<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> Windows®10 (64-bit Required)/Windows®11 (64-bit Required)<br></li></ul>";
        Assert.Equal("Windows 10", Row(StoreService.FitSpecs(StoreService.ParseSpecs(monster, null), Desktop), "OS").Minimum);
    }

    [Fact]
    public void WithoutThisPcNothingIsJudged()
    {
        var fit = StoreService.FitSpecs(StoreService.ParseSpecs(CyberpunkMin, CyberpunkRec), null);
        Assert.All(fit.Rows, r => Assert.Null(r.Meets));
        Assert.Null(fit.Verdict);
    }

    private static string? Graphics(string text) => Short("Graphics", text);

    private static string? Processor(string text) => Short("Processor", text);

    private static string? Short(string label, string text) =>
        Row(StoreService.FitSpecs(StoreService.ParseSpecs($"<ul><li><strong>{label}:</strong> {text}</li></ul>", null), Desktop), label).Minimum;
}
