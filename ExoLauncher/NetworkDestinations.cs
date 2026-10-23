namespace ExoLauncher;

/// <summary>
/// Every host Exo's native code sends a request to, each mapped to the row of
/// PRIVACY.md's "Other network requests" table that tells users about it.
/// ExoLauncher.Tests/NetworkDestinationsTests checks three things: every row
/// named here exists in the table, every table row is named here, and every
/// https:// host written in the host's source is either listed here or
/// explicitly classed there as not contacted by native code.
///
/// A new request: list its host here with its PRIVACY.md row (add the row
/// first if none fits), and build its client with <see cref="ExoHttp.Create"/>.
///
/// Out of scope: pages hosted in WebView2 (Spotify's, YouTube Music's and
/// Apple Music's web players, store and Google/Apple sign-in pages) and the
/// pictures Exo's own page loads; those requests are the page's, under the
/// service's own privacy policy, and PRIVACY.md describes them per service.
/// Loopback (the Riot Client's local API, Spotify's debugging port) never
/// leaves the PC and is not listed.
/// </summary>
internal static class NetworkDestinations
{
    /// <param name="Host">An exact host, or "*.example.com" for example.com and any subdomain.</param>
    /// <param name="PrivacyRow">The Destination cell of the PRIVACY.md row, verbatim.</param>
    internal sealed record Destination(string Host, string PrivacyRow);

    internal const string ExoReleases = "GitHub (ExoLauncher releases)";
    internal const string ToolReleases = "GitHub (Legendary, gogdl and Nile releases, Exo's own build of DeepFilterNet from Exo's release, and NVIDIA's TitaNet voice model from sherpa-onnx's release)";
    internal const string StoreTools = "Epic, GOG, Amazon (through Legendary, gogdl, Nile)";
    internal const string EpicServices = "Epic account and library services";
    internal const string LegendaryApi = "Legendary's optional content lists (api.legendary.gl)";
    internal const string Gog = "GOG (gameplay.gog.com, api.gog.com, gamesdb.gog.com)";
    internal const string Steam = "Steam";
    internal const string SteamWebApi = "Steam Web API";
    internal const string ItchApi = "itch.io API";
    internal const string TrackerGg = "tracker.gg";
    internal const string PcGamingWiki = "PCGamingWiki";
    internal const string OpenXbl = "OpenXBL (api.xbl.io)";
    internal const string CoverArt = "Cover art: Steam CDN and Steam store search, Epic store catalog, GOG, Microsoft Store catalog, and the image servers a store's own data points to";
    internal const string SteamGridDb = "SteamGridDB";
    internal const string Igdb = "IGDB (Twitch)";
    internal const string Upscalers = "Vendor upscaler files (NVIDIA DLSS, AMD FidelityFX SDK and Intel XeSS repositories on GitHub, raw.githubusercontent.com at pinned commits and at each newer release's commit, found through api.github.com; other DLSS, FSR 3.1 and XeSS builds from DLSS Swapper's archive, dlss-swapper-downloads.beeradmoore.com)";
    internal const string UpscalerList = "DLSS Swapper's release list (beeradmoore.github.io/dlss-swapper/manifest.json)";
    internal const string StoreRoom = "Store room: Steam store and Steam Web API (store search, ranked lists, game pages), CheapShark (prices from official stores only), Epic free games";
    internal const string StoreTags = "Store room: Steam tag names (IStoreService/GetTagList)";
    internal const string MusicArtwork = "Music artwork (Spotify, YouTube Music and Apple Music image servers)";
    internal const string Shaders = "Exo Shaders (SCSKiller's servers api.scskiller.com and api.scskiller.io; GitHub: Oodle and zlib-ng codec libraries, and some games' public archive keys)";
    internal const string AmdDrivers = "AMD (newest chipset and graphics driver versions and their downloads: `www.amd.com`, `drivers.amd.com`)";
    internal const string IntelDrivers = "Intel (newest chipset and graphics driver versions and their downloads: `www.intel.com`, `downloadmirror.intel.com`)";
    internal const string NvidiaDrivers = "NVIDIA (driver lookup and download, and RTX Noise Removal's runtime: `www.nvidia.com`, `gfwsl.geforce.com`, `api-forums.nvidia.com`, `us.download.nvidia.com`, `international.download.nvidia.com`)";
    internal const string DiscordClient = "Exo's own Discord client (discord.com, gateway.discord.gg, remote-auth-gateway.discord.gg, cdn.discordapp.com, Discord's media proxy on discordapp.net, Discord's voice servers on discord.media, Discord's file uploads for apps on storage.googleapis.com)";
    internal const string StripeCards = "Stripe's card form in Exo's Discord Shop (js.stripe.com, api.stripe.com, m.stripe.network, hooks.stripe.com), only while you add a card or your bank confirms a payment";
    internal const string DiscordGifs = "GIF hosts Discord points to (klipy.com, tenor.com, giphy.com)";
    internal const string LucidUpdates = "Lucid's version feed (raw.githubusercontent.com/tekkusai/Lucid)";
    internal const string SpotifyShareLinks = "Spotify share links (spotify.link, spotify.app.link)";
    internal const string Installers = "Microsoft (Visual C++ and other runtimes), Spotify";

    internal static readonly Destination[] Hosts =
    [
        // AppUpdateService: release lookup, then the installer through GitHub's download redirect.
        new("api.github.com", ExoReleases),
        new("github.com", ExoReleases),
        new("objects.githubusercontent.com", ExoReleases),
        new("release-assets.githubusercontent.com", ExoReleases),

        // VerifiedGitHubReleaseDownloader: pinned tool assets (Legendary, gogdl, Nile, DeepFilterNet).
        new("api.github.com", ToolReleases),
        new("github.com", ToolReleases),
        new("objects.githubusercontent.com", ToolReleases),
        new("release-assets.githubusercontent.com", ToolReleases),

        // EpicPlaytime: the playtime endpoint.
        new("library-service.live.use1a.on.epicgames.com", EpicServices),
        // StoreGifts: a one-time exchange code that signs the hidden gifts page in to Epic's store.
        new("account-public-service-prod.ol.epicgames.com", EpicServices),

        // WebHostBridge.Properties: an installed Epic game's optional content (selective download) list.
        new("api.legendary.gl", LegendaryApi),

        // GogGameplayAchievementProvider, GogOwnedLibraryService.
        new("gameplay.gog.com", Gog),
        new("api.gog.com", Gog),
        new("gamesdb.gog.com", Gog),

        // SteamWebApi, SteamWebApiAchievementParser (only with a pasted key).
        new("api.steampowered.com", SteamWebApi),
        // SteamStoreAppTypes: an owned app's type, for Web API ids appinfo lacks.
        new("store.steampowered.com", SteamWebApi),

        // ItchOwnedApi (only with a pasted key).
        new("api.itch.io", ItchApi),

        // TrackerGgPlaytime.
        new("api.tracker.gg", TrackerGg),

        // HdrGames: whether a game outputs HDR (only with HDR for HDR games, or a game page on an HDR screen).
        new("www.pcgamingwiki.com", PcGamingWiki),

        // XboxPlaytime (only with a pasted OpenXBL key).
        new("api.xbl.io", OpenXbl),

        // CoverArtService, EpicCatalogArt, MicrosoftStoreArt, SteamOwnershipCatalog.
        new("*.steamstatic.com", CoverArt),
        new("steamcdn-a.akamaihd.net", CoverArt),
        new("store.steampowered.com", CoverArt),
        new("steamcommunity.com", CoverArt),
        new("api.steampowered.com", CoverArt),
        new("*.epicgames.com", CoverArt),
        new("*.unrealengine.com", CoverArt),
        new("api.gog.com", CoverArt),
        new("*.gog-statics.com", CoverArt),
        new("displaycatalog.mp.microsoft.com", CoverArt),
        new("store-images.s-microsoft.com", CoverArt),
        new("images-eds-ssl.xboxlive.com", CoverArt),
        new("*.riotgames.com", CoverArt),
        new("*.riotcdn.net", CoverArt), // Riot theme manifests and art
        new("*.playvalorant.com", CoverArt),
        new("*.leagueoflegends.com", CoverArt),
        new("*.ubisoft.com", CoverArt),
        new("*.ubi.com", CoverArt),
        new("*.ea.com", CoverArt),
        new("*.origin.com", CoverArt),
        new("*.blizzard.com", CoverArt),
        new("*.battle.net", CoverArt),
        new("images.igdb.com", CoverArt),
        new("*.steamgriddb.com", CoverArt),

        // SteamGridDbArt (only with a pasted key).
        new("www.steamgriddb.com", SteamGridDb),

        // IgdbTwitchAuth, CoverArtService's IGDB lookup (developer builds).
        new("id.twitch.tv", Igdb),
        new("api.igdb.com", Igdb),

        // DlssSwapService.Releases, and DlssSwapService.VendorWatch asking each vendor's repository for its latest release.
        new("raw.githubusercontent.com", Upscalers),
        new("api.github.com", Upscalers),
        new("dlss-swapper-downloads.beeradmoore.com", Upscalers),
        new("www.nvidia.com", NvidiaDrivers),
        new("gfwsl.geforce.com", NvidiaDrivers),
        new("us.download.nvidia.com", NvidiaDrivers),
        new("api-forums.nvidia.com", NvidiaDrivers),
        new("international.download.nvidia.com", NvidiaDrivers),

        // ChipsetUpdate: the newest AMD chipset package (only on a Ryzen with AMD's chipset driver), and its download.
        // GpuDriverUpdate: the newest AMD Software for an AMD GPU, and its download.
        new("www.amd.com", AmdDrivers),
        new("drivers.amd.com", AmdDrivers),

        // ChipsetUpdate: the newest Intel Chipset Device Software (only on an Intel processor with it installed), and its download.
        // GpuDriverUpdate: the newest Intel graphics driver for an Intel GPU, and its download.
        new("www.intel.com", IntelDrivers),
        new("downloadmirror.intel.com", IntelDrivers),

        // DlssSwapService.Catalog.
        new("beeradmoore.github.io", UpscalerList),

        // StoreService.
        new("store.steampowered.com", StoreRoom),
        new("api.steampowered.com", StoreRoom),
        new("www.cheapshark.com", StoreRoom),
        new("store-site-backend-static-ipv4.ak.epicgames.com", StoreRoom),
        new("api.steampowered.com", StoreTags),

        // MediaSessionService: the playing song's cover from a web player, for the mini player.
        new("*.googleusercontent.com", MusicArtwork),
        new("*.ggpht.com", MusicArtwork),
        new("*.ytimg.com", MusicArtwork),
        new("*.scdn.co", MusicArtwork),
        new("*.spotifycdn.com", MusicArtwork),
        new("open.spotify.com", MusicArtwork), // a Spotify song's cover from its public oEmbed, when the app names none
        new("*.mzstatic.com", MusicArtwork),
        // SpotifyLinks: a short link's redirect, read from its Location header (the page it names is never fetched).
        new("spotify.link", SpotifyShareLinks),
        new("spotify.app.link", SpotifyShareLinks),
        new("spotify-alternate.app.link", SpotifyShareLinks),

        // DiscordClient: Exo's own Discord client, only while signed in.
        new("discord.com", DiscordClient),
        new("gateway.discord.gg", DiscordClient),
        new("*.discord.gg", DiscordClient),
        new("remote-auth-gateway.discord.gg", DiscordClient),
        new("cdn.discordapp.com", DiscordClient),
        new("*.discordapp.net", DiscordClient), // Copy Image and Save Image from the media proxies (media., images-ext-1.)
        new("*.discord.media", DiscordClient),
        new("discord-attachments-uploads-prd.storage.googleapis.com", DiscordClient),

        // StripeCards: the Shop's card form (its own page, pay.exo-launcher.local); card numbers go only to Stripe.
        new("js.stripe.com", StripeCards),
        new("api.stripe.com", StripeCards),
        new("m.stripe.network", StripeCards),
        new("hooks.stripe.com", StripeCards),

        // LucidCompanion.AvailableUpdateAsync: the version Lucid's own updater reads.
        new("raw.githubusercontent.com", LucidUpdates),

        // DependencyService, DesktopClientHost: installers you chose, and their download redirects.
        new("aka.ms", Installers),
        new("*.microsoft.com", Installers),
        new("download.scdn.co", Installers),
    ];

    /// <summary>
    /// Rows whose requests Exo's own code does not make: the store tools and
    /// Exo Shaders are separate programs, and the Steam row says nothing is sent.
    /// </summary>
    internal static readonly string[] RowsWithoutNativeHosts = [StoreTools, Steam, Shaders, DiscordGifs];

    internal static bool Covers(string pattern, string host) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    internal static bool IsListed(string host) => Hosts.Any(destination => Covers(destination.Host, host));
}
