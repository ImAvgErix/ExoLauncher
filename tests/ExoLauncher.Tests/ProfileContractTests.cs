using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The profile page is the user's to arrange. These tests hold the lines that
/// broke before: a save has to reach the titlebar, uploaded profile art has to
/// come from the host and stay local, the page may not print the same count
/// twice, and every arrangement has to survive a settings round-trip.
/// </summary>
public sealed class ProfileContractTests
{
    [Fact]
    public void EveryProfileWrite_TellsTheRestOfTheAppAboutIt()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        // The titlebar reads the profile once, so a silent save left a stale
        // avatar beside the settings gear.
        Assert.Contains("PostEvent(\"profile.updated\", mapped)", bridge, StringComparison.Ordinal);
        foreach (var write in new[]
                 {
                     "private object ProfileSet(JsonElement p, bool hasParams)",
                     "private object ProfileSetLook(JsonElement p, bool hasParams) => ProfileSaved(",
                     "return ProfileSaved(_social.Profile(RunningLibraryGame()));",
                 })
        {
            Assert.Contains(write, bridge, StringComparison.Ordinal);
        }

        // Resolved local art rides along so every profile surface paints the
        // host-owned copies instead of learning filesystem paths.
        Assert.Contains("avatarImageUrl = profile.AvatarImageUrl", bridge, StringComparison.Ordinal);
        Assert.Contains("bannerImageUrl = profile.BannerImageUrl", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadedPictures_StayHostOwnedAndCloudCopiesAreExplicit()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var hostTs = ReadRepoFile("ui", "src", "lib", "host.ts");
        var indexHtml = ReadRepoFile("ui", "index.html");

        // The UI names a slot. It never names a file, and the RPC has no field
        // for it to name one with.
        Assert.Contains("\"profile.pickImage\" => await ProfilePickImageAsync", bridge, StringComparison.Ordinal);
        Assert.Contains("FileOpenPicker", bridge, StringComparison.Ordinal);
        Assert.Contains("host.profilePickImage(kind)", room, StringComparison.Ordinal);
        Assert.Contains("host.profileClearImage(kind)", room, StringComparison.Ordinal);
        Assert.Contains("export type ProfileImageKind = 'avatar' | 'banner' | ProfileGalleryKind", hostTs, StringComparison.Ordinal);
        Assert.Contains("rawCall<ProfileImageResponse>('profile.pickImage', { kind })", hostTs, StringComparison.Ordinal);
        Assert.Contains("rawCall<ProfileImageResponse>('profile.clearImage', { kind })", hostTs, StringComparison.Ordinal);
        Assert.Contains("ProfileImageStore.Save(sourcePath, slot)", ReadRepoFile("ExoLauncher", "Services", "SocialService.cs"), StringComparison.Ordinal);
        Assert.Contains("onUploadAvatar={() => void uploadImage('avatar')}", room, StringComparison.Ordinal);
        Assert.Contains("onRemoveAvatar={() => void clearImage('avatar')}", room, StringComparison.Ordinal);
        Assert.Contains("onUploadBanner={() => void uploadImage('banner')}", room, StringComparison.Ordinal);
        Assert.Contains("onRemoveBanner={() => void clearImage('banner')}", room, StringComparison.Ordinal);
        Assert.Contains("host.onlineUploadMedia(kind)", room, StringComparison.Ordinal);
        Assert.Contains("host.onlineDeleteMedia(kind)", room, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload public copy", room, StringComparison.Ordinal);
        Assert.Contains("auto-saved to Exo", room, StringComparison.Ordinal);
        Assert.Contains("onAddGallery", room, StringComparison.Ordinal);
        Assert.Contains("onlineMediaCapable", room, StringComparison.Ordinal);
        Assert.Contains("PNG, JPEG, WebP, or GIF", room, StringComparison.Ordinal);

        // Pictures are served through the cover host that the CSP already
        // allows. No file://, no widened policy, no inlined megabytes.
        Assert.DoesNotContain("file://", room, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", room, StringComparison.Ordinal);
        Assert.DoesNotContain("blob:", room, StringComparison.Ordinal);
        Assert.DoesNotContain("sourcePath", room, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"file\"", room, StringComparison.Ordinal);
        Assert.Contains("https://covers.exo-launcher.local", indexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("blob:", indexHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshSettings_StartTheProfileCentered()
    {
        Assert.Equal("center", new AppSettings().ProfileLayout);
    }

    [Fact]
    public void TheRoom_UsesOneBannerHeroAndOneEditor()
    {
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var css = ReadRepoFile("ui", "src", "tokens.css");

        // One icon control. The words are not in a button.
        Assert.Contains("aria-label={mode === 'edit' ? 'Close editor' : 'Edit profile'}", room, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit showcase", room, StringComparison.Ordinal);
        Assert.DoesNotContain(">Customize<", room, StringComparison.Ordinal);
        Assert.DoesNotContain(" title=", room, StringComparison.Ordinal);

        // Identity, appearance, and showcase share the one panel.
        Assert.Contains(">Identity<", room, StringComparison.Ordinal);
        Assert.Contains(">Appearance<", room, StringComparison.Ordinal);
        Assert.Contains(">Showcase<", room, StringComparison.Ordinal);
        Assert.DoesNotContain("mode === 'identity'", room, StringComparison.Ordinal);
        Assert.DoesNotContain("mode === 'customize'", room, StringComparison.Ordinal);

        // Avatar enlarges from a real button; initials stay inert. The shared
        // hero reads the host-resolved banner and paints it as decorative art.
        Assert.Contains("aria-label=\"Enlarge profile picture\"", room, StringComparison.Ordinal);
        Assert.Contains("exo-profile-lightbox-scrim", room, StringComparison.Ordinal);
        Assert.Contains("className=\"exo-profile-lightbox-close\"", room, StringComparison.Ordinal);
        Assert.Contains("tabIndex={-1}", room, StringComparison.Ordinal);
        Assert.Contains("closeRef.current?.focus", room, StringComparison.Ordinal);
        Assert.Contains("button:not([disabled]):not([tabindex=\"-1\"])", room, StringComparison.Ordinal);
        Assert.Contains("if (event.key === 'Escape')", room, StringComparison.Ordinal);
        Assert.Contains("returnFocusRef.current?.focus", room, StringComparison.Ordinal);
        Assert.Contains("event.key !== 'Tab'", room, StringComparison.Ordinal);
        Assert.DoesNotContain("<label className=\"exo-profile-field\">", room, StringComparison.Ordinal);
        Assert.Contains("htmlFor=\"exo-profile-name\"", room, StringComparison.Ordinal);
        Assert.Contains("const bannerImage = profile?.bannerImageUrl ?? null", room, StringComparison.Ordinal);
        var hero = Between(room, "<header", "</header>");
        Assert.Contains("exo-profile-hero-fallback", hero, StringComparison.Ordinal);
        Assert.Contains("exo-profile-hero-image", hero, StringComparison.Ordinal);
        Assert.Contains("src={bannerImage ?? undefined}", hero, StringComparison.Ordinal);
        Assert.Contains("alt=\"\"", hero, StringComparison.Ordinal);
        Assert.Contains("decoding=\"async\"", hero, StringComparison.Ordinal);
        Assert.Contains("exo-profile-hero-veil", hero, StringComparison.Ordinal);
        Assert.Contains("exo-profile-hero-content", hero, StringComparison.Ordinal);
        Assert.Contains("exo-profile-actions", hero, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload avatar", hero, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload banner", hero, StringComparison.Ordinal);
        Assert.DoesNotContain("uploadImage('", hero, StringComparison.Ordinal);
        Assert.DoesNotContain("<CoverArt", hero, StringComparison.Ordinal);

        var editor = Between(room, "function EditorPanel(", "function ArtPicker(");
        Assert.Contains("exo-profile-image-controls", editor, StringComparison.Ordinal);
        Assert.Contains("exo-profile-image-control is-avatar", editor, StringComparison.Ordinal);
        Assert.Contains("exo-profile-image-control is-banner", editor, StringComparison.Ordinal);
        Assert.Contains("onUploadAvatar", editor, StringComparison.Ordinal);
        Assert.Contains("onRemoveAvatar", editor, StringComparison.Ordinal);
        Assert.Contains("onUploadBanner", editor, StringComparison.Ordinal);
        Assert.Contains("onRemoveBanner", editor, StringComparison.Ordinal);
        Assert.Contains("bannerImage: string | null", editor, StringComparison.Ordinal);
        Assert.Contains("Upload avatar", editor, StringComparison.Ordinal);
        Assert.Contains("Remove avatar", editor, StringComparison.Ordinal);
        Assert.Contains("Upload banner", editor, StringComparison.Ordinal);
        Assert.Contains("Remove banner", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("<CoverArt game={banner", room, StringComparison.Ordinal);
        Assert.Contains("const PROFILE_LAYOUTS", room, StringComparison.Ordinal);
        Assert.Contains("['left', 'Left']", room, StringComparison.Ordinal);
        Assert.Contains("const BANNER_HEIGHTS", room, StringComparison.Ordinal);
        Assert.DoesNotContain("showLevel", room, StringComparison.Ordinal);
        Assert.Contains("canEnlarge ? (", room, StringComparison.Ordinal);
        Assert.Contains("'exo-profile min-h-0 flex-1'", room, StringComparison.Ordinal);
        Assert.Contains("`is-${profileLayout}`", room, StringComparison.Ordinal);
        Assert.Contains("`is-${bannerHeight}`", room, StringComparison.Ordinal);
        Assert.Contains("'--profile-accent': accent", room, StringComparison.Ordinal);
        Assert.DoesNotContain("levelFromMinutes", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-level", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-xp", room, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-level", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-xp", css, StringComparison.Ordinal);
        Assert.DoesNotContain("<AccountPanel", room, StringComparison.Ordinal);
        Assert.DoesNotContain("`@${handle}`", room, StringComparison.Ordinal);
        Assert.DoesNotContain("No handle yet", room, StringComparison.Ordinal);
        Assert.DoesNotContain("Handle visibility", room, StringComparison.Ordinal);
        var pickerLabel = Between(css, ".exo-picker-label {", "}");
        Assert.Contains("white-space: normal", pickerLabel, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", pickerLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("text-overflow: ellipsis", pickerLabel, StringComparison.Ordinal);
        var statline = Between(css, ".exo-profile-statline {", ".exo-profile-statline > div");
        Assert.Contains("background: transparent", statline, StringComparison.Ordinal);

        // The hero owns every visual layer and the content remains inside its
        // bottom edge. Uploaded banners are never routed through cover art.
        Assert.Contains(".exo-profile-hero", css, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-hero-fallback", css, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-hero-image", css, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-hero-veil", css, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-hero-content", css, StringComparison.Ordinal);
        Assert.DoesNotContain("backdrop-filter", Between(css, ".exo-profile-lightbox {", ".exo-profile-lightbox-stage.is-art"), StringComparison.Ordinal);

        // The root owns the viewport and prose keeps a readable measure.
        Assert.Contains("max-width: none", Between(css, ".exo-profile-body {", ".exo-profile-body .exo-profile-note"), StringComparison.Ordinal);
        Assert.DoesNotContain("max-width: 768px", css, StringComparison.Ordinal);
        Assert.Contains("max-width: 65ch", Between(css, ".exo-profile-bio {", ".exo-profile-count"), StringComparison.Ordinal);
        Assert.Contains("exo-profile-view", room, StringComparison.Ordinal);
        Assert.Contains("exo-showcase-feature", room, StringComparison.Ordinal);
        Assert.Contains("TrophyCabinet", room, StringComparison.Ordinal);
        Assert.Contains("'has-trophies'", room, StringComparison.Ordinal);
        Assert.Contains("exo-profile-trophy-stage", room, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(220px, 250px) minmax(0, 1fr) minmax(280px, 330px)", css, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", Between(css, ".exo-profile-stage {", ".exo-profile-stage::-webkit-scrollbar"), StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-y: auto", Between(css, ".exo-profile-stage {", ".exo-profile-stage::-webkit-scrollbar"), StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr))", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(116px, 1fr))", css, StringComparison.Ordinal);

        // The showcase is the fixed main stage. Stored ordering still controls
        // the compact detail rail, and showcase reordering is not offered.
        Assert.Contains("const railSections = visibleSections.filter((key) => key !== 'showcase')", room, StringComparison.Ordinal);
        Assert.Contains("{railSections.map(renderSection)}", room, StringComparison.Ordinal);
        Assert.Contains("{showShowcase ? renderSection('showcase') : null}", room, StringComparison.Ordinal);
        Assert.Contains("if (key === 'showcase') return", room, StringComparison.Ordinal);
        Assert.Contains("Main stage", room, StringComparison.Ordinal);
        var viewCss = Between(css, ".exo-profile-view {", ".exo-profile-statline {");
        Assert.Contains("grid-template-columns: minmax(236px, 272px) minmax(0, 1fr)", viewCss, StringComparison.Ordinal);
        Assert.Contains("align-items: start", viewCss, StringComparison.Ordinal);
        Assert.Contains("height: 100%", viewCss, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", viewCss, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-rail", viewCss, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-block.is-showcase", viewCss, StringComparison.Ordinal);
        Assert.Contains("height: fit-content", Between(viewCss, ".exo-profile-rail {", ".exo-profile-block {"), StringComparison.Ordinal);
        Assert.Contains("max-height: 100%", Between(viewCss, ".exo-profile-rail {", ".exo-profile-block {"), StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1200px)", viewCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(220px, 236px) minmax(0, 1fr)", viewCss, StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: minmax(0, 1.35fr) minmax(0, 0.8fr) minmax(0, 0.95fr)",
            Between(css, ".exo-profile-statline {", ".exo-profile-statline > div"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("grid-column: span 2", viewCss, StringComparison.Ordinal);

        // The fallback is an accent-led composition, not another generic
        // black dashboard gradient. Uploaded art gets one quiet readability veil.
        var heroCss = Between(css, ".exo-profile-hero {", ".exo-profile-head {");
        Assert.Contains("var(--profile-accent)", heroCss, StringComparison.Ordinal);
        Assert.Contains("radial-gradient", heroCss, StringComparison.Ordinal);
        Assert.Contains("linear-gradient", heroCss, StringComparison.Ordinal);

        // The editor previews the choices the host already persists, keeps its
        // primary actions reachable, and does not grow a second set of modes.
        Assert.Contains("exo-profile-form-head", editor, StringComparison.Ordinal);
        Assert.Contains("label=\"Alignment\"", editor, StringComparison.Ordinal);
        Assert.Contains("label=\"Banner height\"", editor, StringComparison.Ordinal);
        Assert.Contains("Make this page yours", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("label=\"Style\"", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentitySave_PreservesGameArtAndBannerUsesTheSavedHeroAsFallback()
    {
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var save = Between(room, "async function saveIdentity()", "const hiddenSections");
        var hero = Between(room, "<header", "</header>");

        // Identity fields are unrelated to art choices. Omitting these optional
        // patch fields preserves the host's saved game IDs instead of clearing them.
        Assert.DoesNotContain("avatarGameId:", save, StringComparison.Ordinal);
        Assert.DoesNotContain("bannerGameId:", save, StringComparison.Ordinal);

        Assert.Contains("const bannerGame = profile?.bannerGameId", room, StringComparison.Ordinal);
        Assert.Contains("real.find((game) => game.id === profile.bannerGameId)", room, StringComparison.Ordinal);
        Assert.Contains("{effectiveBannerImage ? (", hero, StringComparison.Ordinal);
        Assert.Contains(") : bannerGame ? (", hero, StringComparison.Ordinal);
        Assert.Contains("<HeroWash game={bannerGame} />", hero, StringComparison.Ordinal);
        Assert.Contains("className=\"exo-profile-hero-game-art\"", hero, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-hero-game-art", css, StringComparison.Ordinal);

        Assert.True(
            hero.IndexOf("src={bannerImage ?? undefined}", StringComparison.Ordinal) <
            hero.IndexOf("<HeroWash game={bannerGame} />", StringComparison.Ordinal),
            "Uploaded banner art must outrank the selected library hero.");
        Assert.DoesNotContain("Upload banner", hero, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalProfile_OnlyPrintsPresenceForAnObservedRunningGame()
    {
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var presence = Between(room, "const running =", "// The library read is the fresher");
        var avatar = Between(room, "const avatarInner = (", "return (");

        Assert.Contains("const playing = running", presence, StringComparison.Ordinal);
        Assert.DoesNotContain("profile?.playingId", presence, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-status", avatar, StringComparison.Ordinal);
        Assert.Contains("Playing {playing.title}", room, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilePictures_DoNotCarryPresenceDots()
    {
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var css = ReadRepoFile("ui", "src", "tokens.css");

        Assert.DoesNotContain("exo-status", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-status", friends, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-status", css, StringComparison.Ordinal);
        Assert.Contains("Playing {playing.title}", room, StringComparison.Ordinal);
        Assert.Contains("PRESENCE_LABEL", friends, StringComparison.Ordinal);
        Assert.Contains("const presenceMeta = presence?.available", friends, StringComparison.Ordinal);
        Assert.Contains("const meta = presenceMeta ??", friends, StringComparison.Ordinal);
        Assert.Contains("Presence unavailable", friends, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRoom_UsesObservedActivityAndConnectedStoresWithoutDashboardCards()
    {
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var css = ReadRepoFile("ui", "src", "tokens.css");

        // Stats are one quiet ruled line, not a field of interchangeable cards
        // or repeated fact pills.
        Assert.DoesNotContain("exo-profile-stats", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-profile-stat-value", room, StringComparison.Ordinal);
        Assert.DoesNotContain("<Stat ", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-fact", room, StringComparison.Ordinal);
        Assert.Contains("exo-profile-statline", room, StringComparison.Ordinal);

        // Activity is derived only from observed library timestamps. It never
        // invents a feed or falls back to account/server fiction.
        Assert.Contains("const recentGames = useMemo", room, StringComparison.Ordinal);
        Assert.Contains("game.lastPlayedUtc", room, StringComparison.Ordinal);
        Assert.Contains(">Activity<", room, StringComparison.Ordinal);
        Assert.Contains("exo-profile-activity", room, StringComparison.Ordinal);
        Assert.Contains("formatRelativeLastPlayed(game.lastPlayedUtc)", room, StringComparison.Ordinal);

        // Store identities belong to Settings; the profile keeps the stage for
        // authored identity, activity, about, showcase, and gallery content.
        Assert.DoesNotContain("{ key: 'stores', label: 'Connected stores'", room, StringComparison.Ordinal);
        Assert.DoesNotContain("const connectedAccounts =", room, StringComparison.Ordinal);
        Assert.DoesNotContain(">Connected stores<", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-profile-store-list", room, StringComparison.Ordinal);
        Assert.DoesNotContain("host.storesAuth", room, StringComparison.Ordinal);
        Assert.DoesNotContain("host.showStore", room, StringComparison.Ordinal);

        var profileCss = Between(css, "/* ── Exo profile ── */", "/* Library / friends / settings chrome. */");
        Assert.DoesNotContain(".exo-fact", profileCss, StringComparison.Ordinal);
    }

    [Fact]
    public void Showcase_UsesOnlyObservedHoursAndStoreInOneQuietLine()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");

        // The host still records the complete snapshot, while the compact card
        // uses only observed hours and store instead of a dashboard of stats.
        Assert.Contains("showcaseEntries = ShowcaseEntries(profile.Showcase)", bridge, StringComparison.Ordinal);
        Assert.Contains("GetLatestSnapshot(game)", bridge, StringComparison.Ordinal);
        Assert.Contains("achievementsUnlocked = unlocked", bridge, StringComparison.Ordinal);
        Assert.Contains("entry?.playtimeMinutes ?? game.playtimeMinutes ?? null", room, StringComparison.Ordinal);
        Assert.Contains("storeLabel(entry?.store ?? game.store)", room, StringComparison.Ordinal);
        Assert.Contains("exo-showcase-meta", room, StringComparison.Ordinal);
        Assert.Contains("formatPlaytime(minutes)", room, StringComparison.Ordinal);
        Assert.Contains("achievementsTotal", room, StringComparison.Ordinal);
        Assert.DoesNotContain("Achievement signals", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-showcase-rank", room, StringComparison.Ordinal);
        Assert.DoesNotContain("'Unlocks'", room, StringComparison.Ordinal);
        Assert.DoesNotContain("'Last played'", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-showcase-stats", room, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-showcase-bar", room, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortWindow_CompactsTheViewAndOnlyTheEditorShowsScrollBehavior()
    {
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var profileCss = Between(css, "/* ── Exo profile ── */", "/* Library / friends / settings chrome. */");
        var compactHero = profileCss.IndexOf(".exo-profile.is-view .exo-profile-hero,", StringComparison.Ordinal);
        var compactStart = compactHero < 0
            ? -1
            : profileCss.LastIndexOf("@media (max-height: 760px) {", compactHero, StringComparison.Ordinal);
        var compactEnd = compactHero < 0
            ? -1
            : profileCss.IndexOf("@media (hover: hover) and (pointer: fine) {", compactHero, StringComparison.Ordinal);
        Assert.True(compactStart >= 0 && compactEnd > compactStart, "The short-window profile rules must remain scoped and discoverable.");
        var compact = profileCss[compactStart..compactEnd];

        Assert.Contains("mode === 'view' && 'is-view'", room, StringComparison.Ordinal);
        Assert.Contains("mode === 'edit' && 'is-edit'", room, StringComparison.Ordinal);
        Assert.Contains(".exo-profile.is-view .exo-profile-hero", compact, StringComparison.Ordinal);
        Assert.Contains(".exo-profile.is-view .exo-profile-hero-content", compact, StringComparison.Ordinal);
        Assert.Contains(".exo-profile.is-edit .exo-profile-hero", compact, StringComparison.Ordinal);
        Assert.Contains(".exo-profile.is-view .exo-profile-block", compact, StringComparison.Ordinal);
        Assert.Contains(".exo-profile.is-view .exo-showcase", compact, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr))", compact, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 52px minmax(0, 1fr)", compact, StringComparison.Ordinal);
        Assert.Contains("height: 176px", compact, StringComparison.Ordinal);
        Assert.Contains("-webkit-line-clamp: 3", compact, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-showcase-item:nth-child", compact, StringComparison.Ordinal);
        Assert.DoesNotContain(".exo-profile-editor", compact, StringComparison.Ordinal);

        // At both supported window targets the pane, root, body, rail, and view
        // clip. Only the editor form and cover picker own vertical scrolling.
        var rootCss = Between(profileCss, ".exo-profile {", ".exo-pane:has(> .exo-profile)");
        Assert.Contains("height: 100%", rootCss, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", rootCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: auto minmax(0, 1fr)", rootCss, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", Between(profileCss, ".exo-pane:has(> .exo-profile) {", ".exo-pane:has(> .exo-profile)::-webkit-scrollbar"), StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", Between(profileCss, ".exo-profile-body {", ".exo-profile-body .exo-profile-note"), StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", Between(profileCss, ".exo-profile-view {", ".exo-profile-statline {"), StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", Between(profileCss, ".exo-profile-rail {", ".exo-profile-block {"), StringComparison.Ordinal);
        var formCss = Between(profileCss, ".exo-profile-form {", ".exo-profile-form::-webkit-scrollbar");
        Assert.Contains("height: 100%", formCss, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", formCss, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none", formCss, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-form-head", css, StringComparison.Ordinal);
        var formHead = Between(css, ".exo-profile-form-head {", ".exo-profile-editor-block {");
        Assert.Contains("position: sticky", formHead, StringComparison.Ordinal);
        Assert.Contains("background: #000", formHead, StringComparison.Ordinal);
        Assert.DoesNotContain("rgba(0, 0, 0, 0.96)", formHead, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-image-controls", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", Between(css, ".exo-profile-image-controls {", ".exo-profile-image-control {"), StringComparison.Ordinal);
    }

    [Fact]
    public void Pickers_ScrollAndAreValidatedHostSide()
    {
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var store = ReadRepoFile("ExoLauncher", "Services", "ProfileImageStore.cs");

        // The old strip cut every library off after the first few titles.
        Assert.DoesNotContain("exo-profile-strip", css, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-profile-strip", room, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", Between(css, ".exo-picker {", ".exo-picker-item"), StringComparison.Ordinal);

        // The bytes decide the format, and the size has to be sane.
        Assert.Contains("0x89 && head[1] == 0x50", store, StringComparison.Ordinal);
        Assert.Contains("head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF", store, StringComparison.Ordinal);
        Assert.Contains("(byte)'G'", store, StringComparison.Ordinal);
        Assert.Contains("MaxBytes", store, StringComparison.Ordinal);
        Assert.Contains("ReadImageSize", store, StringComparison.Ordinal);
    }

    [Fact]
    public void Showcase_UsesCompleteCoverCardsAndSupportsReordering()
    {
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var showcase = Between(css, ".exo-showcase {", "@media (max-height: 760px) {");

        Assert.Contains("aspect-ratio: 2 / 3", showcase, StringComparison.Ordinal);
        Assert.Contains(".exo-showcase-feature", showcase, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(116px, 1fr))", showcase, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1200px)", showcase, StringComparison.Ordinal);
        Assert.Contains(".exo-profile-trophies", showcase, StringComparison.Ordinal);
        Assert.DoesNotContain("achievementCache", room, StringComparison.Ordinal);
        Assert.Contains("host.refreshAchievements", room, StringComparison.Ordinal);
        Assert.Contains("exo-showcase-meta", room, StringComparison.Ordinal);
        Assert.DoesNotContain("max-width: 840px", showcase, StringComparison.Ordinal);
        Assert.Contains("showcaseStyle", room, StringComparison.Ordinal);
        Assert.Contains("exo-showcase-row", room, StringComparison.Ordinal);
        Assert.Contains("draggable", room, StringComparison.Ordinal);
        Assert.Contains("exo-profile-drag-handle", room, StringComparison.Ordinal);
        Assert.Contains("exo-profile-visibility-btn", room, StringComparison.Ordinal);
        Assert.DoesNotContain("height: 64px", showcase, StringComparison.Ordinal);
        Assert.DoesNotContain("object-position", showcase, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileInteractions_AreShortPointerGatedAndReduceMotion()
    {
        var css = ReadRepoFile("ui", "src", "tokens.css");
        var profileCss = Between(css, "/* ── Exo profile ── */", "/* Library / friends / settings chrome. */");
        var hover = Between(profileCss, "@media (hover: hover) and (pointer: fine) {", "@media (prefers-reduced-motion: reduce) {");
        var reduced = profileCss[(profileCss.IndexOf("@media (prefers-reduced-motion: reduce) {", StringComparison.Ordinal))..];

        Assert.DoesNotContain("transition: all", profileCss, StringComparison.Ordinal);
        Assert.Contains("transition: transform 180ms var(--ease-out)", profileCss, StringComparison.Ordinal);
        Assert.Contains("transform 140ms var(--ease-out)", profileCss, StringComparison.Ordinal);
        Assert.Contains(".exo-showcase-item:hover .exo-showcase-art", hover, StringComparison.Ordinal);
        Assert.Contains("transform: translateY(-3px)", hover, StringComparison.Ordinal);
        Assert.Contains("transform: scale(1.018)", hover, StringComparison.Ordinal);
        Assert.Contains("transform: none !important", reduced, StringComparison.Ordinal);
        Assert.Contains("transition-property: color, border-color, background-color, opacity", reduced, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileLightbox_IsInstantForKeyboardAndMovesOnlyItsPointerOpenedStage()
    {
        var room = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var lightboxStart = room.IndexOf("function AvatarLightbox(", StringComparison.Ordinal);
        Assert.True(lightboxStart >= 0);
        var lightbox = room[lightboxStart..];

        Assert.Contains("setLightboxInstant(event.detail === 0)", room, StringComparison.Ordinal);
        Assert.Contains("onClose(true)", lightbox, StringComparison.Ordinal);
        Assert.Contains("if (reduce || instant)", lightbox, StringComparison.Ordinal);
        Assert.Contains("className={cn('exo-profile-lightbox-stage'", lightbox, StringComparison.Ordinal);
        Assert.Contains("transform: 'scale(0.96)'", lightbox, StringComparison.Ordinal);
        Assert.Contains("duration: 0.16", lightbox, StringComparison.Ordinal);
        Assert.DoesNotContain("initial={{ opacity: 0 }}", lightbox, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrangementChoices_AreCheckedAgainstFixedKeySets()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");

        Assert.Contains("SectionKeys = [\"facts\", \"about\", \"showcase\", \"stores\"]", social, StringComparison.Ordinal);
        Assert.Contains("LayoutKeys = [\"left\", \"center\"]", social, StringComparison.Ordinal);
        Assert.Contains("keys.Contains(key) ? key : keys[0]", social, StringComparison.Ordinal);
        Assert.Contains(".Where(SectionKeys.Contains)", social, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="SettingsService.Current"/> hands out a detached clone, so a
    /// field missing from the clone is a field the user loses on the next read.
    /// </summary>
    [Fact]
    public void SettingsSnapshot_CarriesEveryProfileChoice()
    {
        var service = new SettingsService(new AppSettings
        {
            ProfileAvatarImage = "profile-avatar-abc.png",
            ProfileBannerImage = "profile-banner-def.jpg",
            ProfileLayout = "center",
            ProfileBannerHeight = "tall",
            ProfileShowcaseStyle = "rows",
            ProfileSections = ["stores", "showcase"],
            ProfileHiddenSections = ["facts"],
        });

        var snapshot = service.Current;

        Assert.Equal("profile-avatar-abc.png", snapshot.ProfileAvatarImage);
        Assert.Equal("profile-banner-def.jpg", snapshot.ProfileBannerImage);
        Assert.Equal("center", snapshot.ProfileLayout);
        Assert.Equal("tall", snapshot.ProfileBannerHeight);
        Assert.Equal("rows", snapshot.ProfileShowcaseStyle);
        Assert.Equal(new[] { "stores", "showcase" }, snapshot.ProfileSections);
        Assert.Equal(new[] { "facts" }, snapshot.ProfileHiddenSections);

        // And the snapshot must not be a handle back into the service.
        snapshot.ProfileSections.Clear();
        snapshot.ProfileHiddenSections.Clear();
        Assert.Equal(2, service.Current.ProfileSections.Count);
        Assert.Single(service.Current.ProfileHiddenSections);
    }

    [Fact]
    public async Task ProfileChoices_SurviveAReload()
    {
        await InIsolatedDataDirectory(async () =>
        {
            var service = new SettingsService();
            service.UpdateProfile(settings =>
            {
                settings.ProfileLayout = "center";
                settings.ProfileShowcaseStyle = "rows";
                settings.ProfileSections = ["showcase", "facts", "about", "stores"];
                settings.ProfileHiddenSections = ["stores"];
                settings.ProfileAvatarImage = "profile-avatar-abc.png";
                settings.ProfileBannerImage = "profile-banner-def.jpg";
            });

            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(PathHelper.SettingsPath));
            var root = persisted.RootElement;
            Assert.Equal("center", root.GetProperty("profileLayout").GetString());
            Assert.Equal("showcase", root.GetProperty("profileSections")[0].GetString());
            Assert.Equal("profile-avatar-abc.png", root.GetProperty("profileAvatarImage").GetString());
            Assert.Equal("profile-banner-def.jpg", root.GetProperty("profileBannerImage").GetString());
            Assert.False(root.TryGetProperty("profileShowLevel", out _));

            var reloaded = new SettingsService();
            reloaded.Load();
            Assert.Equal("rows", reloaded.Current.ProfileShowcaseStyle);
            Assert.Equal(new[] { "stores" }, reloaded.Current.ProfileHiddenSections);
            Assert.Equal("profile-banner-def.jpg", reloaded.Current.ProfileBannerImage);
        });
    }

    [Fact]
    public async Task LegacyProfileLevelSetting_IsIgnoredAndNotReemitted()
    {
        await InIsolatedDataDirectory(async () =>
        {
            await File.WriteAllTextAsync(
                PathHelper.SettingsPath,
                """{"profileLayout":"left","profileShowLevel":false}""");

            var service = new SettingsService();
            service.Load();
            Assert.Equal("left", service.Current.ProfileLayout);

            service.UpdateProfile(settings => settings.ProfileBio = "kept");
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(PathHelper.SettingsPath));
            Assert.False(persisted.RootElement.TryGetProperty("profileShowLevel", out _));
        });
    }

    private static async Task InIsolatedDataDirectory(Func<Task> test)
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExoLauncherProfileContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            await test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"missing '{start}'");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"missing '{end}' after '{start}'");
        return text[from..to];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
