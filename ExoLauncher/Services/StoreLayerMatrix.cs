namespace ExoLauncher.Services;

/// <summary>
/// A store is not one integration. It is five independent backends that happen to
/// share one login: the session, the entitlement list, the catalog art, the
/// downloader, and the social/stats API. Exo reports each layer separately so the
/// UI never implies a layer that was never wired.
/// </summary>
internal static class StoreLayerMatrix
{
    /// <summary>Wired = Exo calls it today. Partial = only a narrower path works. None = not yet.</summary>
    public const string Wired = "wired";
    public const string Partial = "partial";
    public const string None = "none";

    /// <summary>
    /// A fully observed local capability context. Callers do the bounded disk /
    /// registry probes once, then this mapper remains deterministic and free of
    /// ambient filesystem, registry, process, and network reads.
    /// </summary>
    public sealed record Context(
        bool ClientPresent,
        bool BackendPresent,
        bool SessionPresent,
        bool WebApiKeyPresent,
        bool LocalDatabasePresent);

    public sealed record Layers(
        string Login,
        string Owned,
        string Covers,
        string Downloads,
        string Social,
        string Note);

    public static Layers For(string storeId, Context context)
    {
        return (storeId ?? string.Empty).ToLowerInvariant() switch
        {
            // Session lives in the Steam process. Ownership comes from that
            // account's tickets and librarycache; bytes move over Steam IPC.
            // Friend names come off the local cache. Live status is a Steam
            // Web API call that needs a key the user pasted, so social stays
            // partial even when that key is present — private profiles stay
            // unknown, and the capability is off by default.
            "steam" => new Layers(
                context.ClientPresent ? Partial : None,
                context.SessionPresent ? Wired : None,
                Wired,
                context.ClientPresent ? Wired : None,
                context.ClientPresent ? Partial : None,
                context.WebApiKeyPresent
                    ? "Steam owns the session, the depots, and live presence. Exo commands the client over IPC and reads the local friends cache. Live status and the current game come from Steam's Web API for public profiles, using a key you saved. Private profiles stay unknown. Covers come from the Steam library cache and Steam's own CDN, including hashed library_capsule."
                    : "Steam owns the session, the depots, and live presence. Exo commands the client over IPC and reads the local friends cache. A Steam Web API key in Settings turns on live status and the current game for public profiles. Covers come from the Steam library cache and Steam's own CDN, including hashed library_capsule."),

            // Legendary holds the OAuth token and speaks Epic's own endpoints.
            // That token reaches the friends list, their names, and when Epic
            // last saw them — but Epic serves live presence over its chat
            // service, not HTTP, so social stays partial.
            "epic" => new Layers(
                context.BackendPresent ? Wired : (context.SessionPresent ? Partial : None),
                context.BackendPresent && context.SessionPresent ? Wired : None,
                Wired,
                context.BackendPresent && context.SessionPresent ? Wired : None,
                context.BackendPresent && context.SessionPresent ? Partial : None,
                "Legendary keeps the Epic token and pulls official chunks. Epic's API gives Exo the friends list and last-seen; it does not give live presence."),

            // gogdl holds the token; Galaxy's local database fills in playtime
            // and, when the user configured integrations, last-known friends.
            "gog" => new Layers(
                context.BackendPresent ? Wired : (context.SessionPresent ? Partial : None),
                context.BackendPresent && context.SessionPresent ? Wired : None,
                Wired,
                context.BackendPresent && context.SessionPresent ? Wired : None,
                context.LocalDatabasePresent ? Partial : None,
                context.LocalDatabasePresent
                    ? "gogdl keeps the GOG token and pulls official installers. Friends and last-known presence come from Galaxy's local database when Galaxy was used. Live only while Galaxy is running. Private or missing tables stay unknown."
                    : "gogdl keeps the GOG token and pulls official installers. Friends need GOG Galaxy's local database — none on this PC."),

            // Vanguard requires Riot's own patcher. Exo drives the lockfile
            // patch API for install progress and launch; it never patches
            // around anti-cheat. Login stays partial because the session lives
            // in Riot Client, and owned stays partial because Exo still shows
            // a fixed catalog rather than the account's entitlements.
            "riot" => new Layers(
                context.ClientPresent ? Partial : None,
                context.ClientPresent ? Partial : None,
                Partial,
                context.ClientPresent && context.BackendPresent ? Wired : None,
                None,
                "Riot Client owns the session, Vanguard, and entitlements. Exo drives its lockfile patch API for install progress and launch; it never patches around anti-cheat. Covers use Epic store portraits for the fixed catalog; live client theme art needs Riot running."),

            // List and launch only until an official agent exists for these.
            "xbox" or "ea" or "ubisoft" or "battlenet" or "rockstar" => new Layers(
                None,
                context.ClientPresent ? Partial : None,
                Partial,
                None,
                None,
                "List and launch only. Covers follow a Steam title match or the installed icon. Installs stay in the official client until an agent exists."),

            // Nile holds the Amazon token the same way Legendary holds Epic's.
            // Without a Nile session Exo only lists proven fuel.json installs.
            "amazon" => new Layers(
                context.BackendPresent ? Wired : (context.SessionPresent ? Partial : None),
                context.BackendPresent && context.SessionPresent
                    ? Wired
                    : (context.ClientPresent ? Partial : None),
                Partial,
                context.BackendPresent && context.SessionPresent ? Wired : None,
                None,
                "Nile keeps the Amazon token and pulls official chunks. Without a Nile session Exo only lists and launches proven Amazon Games installs. Covers follow a Steam title match or the installed icon."),

            "itch" => new Layers(
                None,
                context.ClientPresent ? Partial : None,
                Partial,
                None,
                None,
                "Lists itch.app receipts on disk and launches those builds. butlerd downloads are not wired yet. Covers follow a Steam title match or the installed icon."),

            "minecraft" => new Layers(
                None,
                context.ClientPresent ? Partial : None,
                Partial,
                None,
                None,
                "Lists the official Java versions folder and Bedrock package, then hands off to Minecraft Launcher. Covers use the Microsoft Store poster, then a Steam title match or the installed icon."),

            "roblox" => new Layers(
                None,
                context.ClientPresent ? Partial : None,
                Partial,
                None,
                None,
                "Lists the installed Roblox Player and launches it. Experiences are not a local entitlement list. Covers use the Microsoft Store poster, then a Steam title match or the installed icon."),

            "paradox" => new Layers(
                None,
                context.ClientPresent ? Partial : None,
                Partial,
                None,
                None,
                "Lists proven Paradox Interactive installs and launches them through the official launcher. Covers follow a Steam title match or the installed icon."),

            "wargaming" => new Layers(
                None,
                context.ClientPresent ? Partial : None,
                Partial,
                None,
                None,
                "Lists Wargaming Game Center titles from wgc_gameinfo.xml and launches through wgc://. Covers follow a Steam title match or the installed icon."),

            "local" => new Layers(
                Wired,
                Wired,
                Partial,
                Wired,
                None,
                "Folders you added. Covers are a folder image, a Steam match, or the executable icon."),

            _ => new Layers(None, None, None, None, None, "Not wired yet."),
        };
    }
}
