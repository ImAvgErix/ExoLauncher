using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>Reading AMD's and Intel's driver pages: which page is this PC's, and the driver it offers; and Intel's chipset package.</summary>
internal static partial class GpuDriverUpdate
{
    /// <summary>AMD's driver selector: its product list (every card and processor, with its driver page) rides in the page.</summary>
    internal const string AmdSelector = "https://www.amd.com/en/support/download/drivers.html";
    private const string AmdPages = "https://www.amd.com/en/support/downloads/drivers.html/";

    /// <summary>Intel's driver pages, newest family first: Arc and Core Ultra, 11th–14th Gen (and their Atom, Pentium and Celeron), 7th–10th Gen.</summary>
    internal static readonly string[] IntelPages =
    [
        "https://www.intel.com/content/www/us/en/download/785597/intel-arc-graphics-windows.html",
        "https://www.intel.com/content/www/us/en/download/864990/intel-11th-14th-gen-processor-graphics-windows.html",
        "https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html",
    ];

    /// <summary>The selector, then the card's (or processor's) page: two requests, each through <paramref name="get"/>.</summary>
    internal static async Task<Latest?> AmdLatestAsync(string card, string? cpu, Func<string, Task<string>> get)
    {
        var page = AmdProductPage(AmdProducts(await get(AmdSelector).ConfigureAwait(false)), card, cpu);
        return page is null ? null : AmdDriver(await get(page).ConfigureAwait(false));
    }

    /// <summary>
    /// Intel's page for this PC, read through <paramref name="get"/> one at a time until one fits: every Arc (a card, or
    /// built into a Core Ultra) is on Intel's Arc driver, whose list names its cards with their memory ("Arc™ A770 Graphics
    /// (8GB)") and the older pages list none; any other GPU is on the first page that names the card or the processor.
    /// </summary>
    internal static async Task<Latest?> IntelLatestAsync(string card, string? cpu, Func<string, Task<string>> get)
    {
        if (card.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return IntelDriver(await get(IntelPages[0]).ConfigureAwait(false), IntelPages[0]);
        foreach (var page in IntelPages)
        {
            var html = await get(page).ConfigureAwait(false);
            if (IntelCovers(html, card, cpu)) return IntelDriver(html, page);
        }
        return null;
    }

    /// <summary>Every product AMD's selector lists, its title and its driver page, from the JSON the page carries in an attribute.</summary>
    internal static IReadOnlyList<(string Title, string Url)> AmdProducts(string html)
    {
        var at = html.IndexOf("data-component=\"coveo-browse-documentation\"", StringComparison.Ordinal);
        var json = at < 0 ? Match.Empty : DataJson().Match(html, at);
        if (!json.Success) return [];
        using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(json.Groups[1].Value));
        if (!doc.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) return [];
        var list = new List<(string, string)>();
        foreach (var product in products.EnumerateArray())
            if (product.ValueKind == JsonValueKind.Object
                && product.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                && product.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                list.Add((title.GetString()!, url.GetString()!));
        return list;
    }

    /// <summary>
    /// The card's driver page among AMD's products: the graphics product named as the card is, else (an integrated GPU,
    /// which Windows names "AMD Radeon(TM) Graphics" or "AMD Radeon 780M Graphics") its processor's page. Only AMD's own pages.
    /// </summary>
    internal static string? AmdProductPage(IReadOnlyList<(string Title, string Url)> products, string card, string? cpu)
    {
        string? Find(string kind, string? name) => Named(name) is not { } plain ? null
            : products.FirstOrDefault(product => product.Url.StartsWith(AmdPages + kind + "/", StringComparison.OrdinalIgnoreCase) && Plain(product.Title) == plain).Url;
        return Find("graphics", card) ?? Find("processors", cpu is null ? null : AmdCpuTail().Replace(cpu, ""));
    }

    /// <summary>
    /// The newest AMD Software: Adrenalin Edition a card's or processor's page links in its Windows 11 section, or null.
    /// AMD labels each Recommended (WHQL) or Optional. A PRO card's page also lists AMD Software: PRO Edition, which is left out.
    /// </summary>
    internal static Latest? AmdDriver(string html)
    {
        if (AmdSection(html, "Windows 11") is not { } section) return null;
        Latest? newest = null;
        foreach (Match article in AmdArticle().Matches(section))
        {
            var body = article.Groups[1].Value;
            if (!AmdTitle().Match(body).Groups[1].Value.Contains("Adrenalin", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match field in AmdField().Matches(body)) fields.TryAdd(field.Groups[1].Value.Trim(), WebUtility.HtmlDecode(field.Groups[2].Value).Trim());
            var revision = fields.GetValueOrDefault("Revision Number") ?? "";
            var version = AmdVersion().Match(revision);
            var link = AmdLink().Match(body);
            if (!version.Success || !link.Success) continue;
            var date = DateTime.TryParseExact(fields.GetValueOrDefault("Release Date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateTime?)null;
            var found = new Latest("amd", version.Groups[1].Value, link.Groups[1].Value, Bytes(fields.GetValueOrDefault("File Size")), Day(date),
                revision.Contains("Optional", StringComparison.OrdinalIgnoreCase) ? "optional" : "recommended");
            // The newest wins; between two of one version, AMD's recommended one.
            if (newest is null || Version.Parse(found.Version) > Version.Parse(newest.Version)
                || (Version.Parse(found.Version) == Version.Parse(newest.Version) && found.Kind == "recommended"))
                newest = found;
        }
        return newest;
    }

    /// <summary>The page's part under the accordion header naming the OS ("Windows 11 - 64-Bit Edition"), or null.</summary>
    private static string? AmdSection(string html, string os)
    {
        foreach (var part in html.Split("class=\"accordion-item\"", StringSplitOptions.None).Skip(1))
            if (AccordionHeader().Match(part) is { Success: true } header && header.Groups[1].Value.Contains(os, StringComparison.OrdinalIgnoreCase)) return part;
        return null;
    }

    /// <summary>Whether an Intel driver page lists the card ("Intel® Arc™ B580 Graphics") or the processor ("Intel® Core™ i7-12700K Processor (25M Cache, …)").</summary>
    internal static bool IntelCovers(string html, string card, string? cpu)
    {
        var products = IntelProducts(html);
        if (Named(card) is { } plain && products.Any(product => Plain(product) == plain)) return true;
        if (IntelCpuModel(cpu) is not { } model) return false;
        var exact = new Regex($@"(?<![\w-]){Regex.Escape(model)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return products.Any(product => exact.IsMatch(product));
    }

    /// <summary>The products an Intel driver page says it is for.</summary>
    internal static IReadOnlyList<string> IntelProducts(string html) =>
        IntelProduct().Matches(html).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value).Trim()).Where(name => name.Length > 0).Distinct().ToList();

    /// <summary>
    /// The processor's model as Intel's product lists write it ("i7-12700K", "155H", "N100"): the last part of its name with a
    /// number in it, before " CPU @ 2.90GHz". Null without one.
    /// </summary>
    internal static string? IntelCpuModel(string? cpu)
    {
        if (cpu is null) return null;
        var name = IntelCpuTail().Replace(Marks().Replace(cpu, " "), "");
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(part => part.Length >= 3 && part.Any(char.IsAsciiDigit) && !Ordinal().IsMatch(part));
    }

    /// <summary>
    /// The driver an Intel page offers: the version it installs, its gfx_win installer (whose name carries the version's last
    /// two parts: gfx_win_101.9150.exe is 32.0.101.9150), the size and SHA-512 listed beside it, and the page's date.
    /// </summary>
    internal static Latest? IntelDriver(string html, string page)
    {
        var link = IntelLink().Match(html);
        var version = IntelVersion().Match(html);
        if (!link.Success || !version.Success || !version.Groups[1].Value.EndsWith("." + link.Groups[1].Value, StringComparison.Ordinal)) return null;
        var details = html[link.Index..];
        var size = IntelSize().Match(details);
        var sha = IntelSha().Match(details);
        var date = IntelDate().Match(html);
        var day = date.Success && DateTime.TryParseExact(date.Groups[1].Value, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTime?)null;
        return new Latest("intel", version.Groups[1].Value, link.Value, Bytes(size.Success ? size.Groups[1].Value : null), Day(day), "intel",
            sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null, page);
    }

    /// <summary>
    /// The package Intel's Chipset Device Software page offers (<see cref="ChipsetUpdate"/>): the version the page is for
    /// (its DownloadVersion), its SetupChipset.exe on Intel's download server (not the release notes or a zip of the INF
    /// files), and the SHA-256 listed in that download's own details. Null when the page names no installer or version.
    /// </summary>
    internal static ChipsetUpdate.Package? IntelChipset(string html)
    {
        var version = IntelDownloadVersion().Match(html);
        var link = IntelChipsetLink().Match(html);
        if (!version.Success || !link.Success || !Version.TryParse(version.Groups[1].Value, out _)) return null;
        var next = html.IndexOf("downloadmirror.intel.com", link.Index + link.Length, StringComparison.OrdinalIgnoreCase);
        var sha = IntelSha256().Match(html[link.Index..(next < 0 ? html.Length : next)]);
        return new(version.Groups[1].Value, link.Value, sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null);
    }

    /// <summary>A date the page shows, as the row reads it: the day, at local midnight.</summary>
    private static string? Day(DateTime? day) => day?.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture);

    /// <summary>"887 MB", "862.6 MB", "1.2 GB" as bytes, as the pages count them (1 MB = 1,000,000); 0 when it can't tell.</summary>
    internal static long Bytes(string? size)
    {
        var match = size is null ? Match.Empty : SizeText().Match(size);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0;
        return (long)(value * match.Groups[2].Value.ToUpperInvariant() switch { "GB" => 1e9, "MB" => 1e6, _ => 1e3 });
    }

    /// <summary>A product name to compare: no maker, marks, "Graphics" or "Series", lower case ("AMD Radeon™ RX 9070 XT" and "AMD Radeon RX 9070 XT" are "radeon rx 9070 xt").</summary>
    internal static string Plain(string name) => Spaces().Replace(Noise().Replace(name, " "), " ").Trim().ToLowerInvariant();

    /// <summary>
    /// The name to look for, or null when it names no model: "AMD Radeon(TM) Graphics" ("radeon") or "Intel(R) UHD Graphics"
    /// ("uhd") would match a page for some other chip.
    /// </summary>
    private static string? Named(string? name) =>
        name is null || Plain(name) is not { Length: > 0 } plain || (!plain.Any(char.IsAsciiDigit) && !plain.Contains(' ')) ? null : plain;

    [GeneratedRegex(@"\b(?:RX|PRO|VII)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiscreteRadeon();

    [GeneratedRegex(@"ResultCode\s*=\s*(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AmdResultCode();

    [GeneratedRegex("data-json=\"([^\"]*)\"")]
    private static partial Regex DataJson();

    // " w/ Radeon 780M Graphics", " with Radeon Graphics", " 8-Core Processor", " Processor".
    [GeneratedRegex(@"\s+(?:w/|with)\s.*$|\s+\d+-Core\s+Processor.*$|\s+Processor\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex AmdCpuTail();

    [GeneratedRegex(@"<button\s+class=""accordion-button[^""]*""[^>]*>(.*?)</button>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AccordionHeader();

    [GeneratedRegex(@"<article[^>]*driver-download-details[^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AmdArticle();

    [GeneratedRegex(@"<h4[^>]*>\s*(.*?)\s*</h4>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AmdTitle();

    [GeneratedRegex(@"<strong>\s*([^<]+?)\s*</strong>\s*<p>\s*([^<]*?)\s*</p>", RegexOptions.IgnoreCase)]
    private static partial Regex AmdField();

    [GeneratedRegex(@"Adrenalin\s+(\d+\.\d+\.\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdVersion();

    [GeneratedRegex(@"href=""(https://drivers\.amd\.com/drivers/[^""]+?\.exe)""", RegexOptions.IgnoreCase)]
    private static partial Regex AmdLink();

    [GeneratedRegex(@"other-valid-products-panel__product\b[^>]*>\s*<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex IntelProduct();

    [GeneratedRegex(@"https://downloadmirror\.intel\.com/\d+/gfx_win_(\d+\.\d+)\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex IntelLink();

    [GeneratedRegex(@"Graphics Driver (\d+\.\d+\.\d+\.\d+)")]
    private static partial Regex IntelVersion();

    [GeneratedRegex(@"Size:\s*([\d.]+\s*[KMG]B)", RegexOptions.IgnoreCase)]
    private static partial Regex IntelSize();

    [GeneratedRegex(@"SHA512:\s*([0-9a-fA-F]{128})\b")]
    private static partial Regex IntelSha();

    [GeneratedRegex(@"Date</label>\s*<span[^>]*>\s*(\d{1,2}/\d{1,2}/\d{4})\s*</span>")]
    private static partial Regex IntelDate();

    [GeneratedRegex(@"<meta\s+name=""DownloadVersion""\s+content=""(\d+(?:\.\d+){1,3})""", RegexOptions.IgnoreCase)]
    private static partial Regex IntelDownloadVersion();

    [GeneratedRegex(@"https://downloadmirror\.intel\.com/\d+/SetupChipset\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex IntelChipsetLink();

    [GeneratedRegex(@"SHA256:\s*([0-9a-fA-F]{64})\b")]
    private static partial Regex IntelSha256();

    // " CPU @ 2.90GHz", " @ 1.10GHz", " CPU".
    [GeneratedRegex(@"\s+(?:CPU\b.*|@.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCpuTail();

    [GeneratedRegex(@"^\d+(?:st|nd|rd|th)$", RegexOptions.IgnoreCase)]
    private static partial Regex Ordinal();

    [GeneratedRegex(@"\((?:R|TM)\)|[®™]")]
    private static partial Regex Marks();

    [GeneratedRegex(@"([\d.]+)\s*([KMG]B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeText();

    // Marks (and AMD's page's U+FFFD and zero-width spaces), makers, and the words a name may or may not carry.
    [GeneratedRegex(@"\((?:R|TM)\)|[®™�​]|\b(?:AMD|ATI|Intel|Series|Graphics)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
