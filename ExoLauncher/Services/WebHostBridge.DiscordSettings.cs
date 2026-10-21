using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Models;
using ExoLauncher.Services.DiscordClient;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Services;

/// <summary>
/// Discord settings in the Discord room: the account's own settings (Discord's PreloadedUserSettings,
/// <see cref="DiscordUserSettings"/>), the account itself (name, profile, avatar, data-use consent,
/// logged-in devices, authorized apps, connections) and the settings Exo keeps for its client (input
/// profile, Push to Talk, sensitivity, echo cancellation, attenuation, keybinds, notifications).
/// The page never gets the sign-in, the full email or the full phone number.
/// </summary>
public sealed partial class WebHostBridge
{
    private const int MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>Reads the account's settings, or saves { changes } (key: value, Discord's own field names) and returns them as saved.</summary>
    private static async Task<object> DiscordUserSettingsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected.", values = (object?)null };
        Dictionary<string, JsonElement>? changes = null;
        if (hasParams && p.TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Object)
            changes = c.EnumerateObject().Take(20).ToDictionary(x => x.Name, x => x.Value.Clone());
        var (values, error) = await session.UserSettingsAsync(changes, ct).ConfigureAwait(false);
        return new { ok = values is not null, message = error, values = (object?)values };
    }

    /// <summary>My Account, Profiles, Data & Privacy, Devices and Connected Apps: one action each.</summary>
    private static async Task<object> DiscordAccountAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected." };
        var rest = session.Rest;
        switch (ReadString(p, hasParams, "action"))
        {
            case "me": return await DiscordMeAsync(session, ct).ConfigureAwait(false);
            case "name":
                var name = ReadString(p, hasParams, "globalName")?.Trim();
                if (name is { Length: > 32 }) return new { ok = false, message = "Display names can be up to 32 characters." };
                return Done(await rest.UpdateMeAsync(new JsonObject { ["global_name"] = string.IsNullOrEmpty(name) ? null : name }, ct).ConfigureAwait(false));
            case "profile": return await DiscordProfileEditAsync(rest, p, hasParams, ct).ConfigureAwait(false);
            // The banner picture is Nitro's; Discord refuses it without, and the banner color shows instead.
            case "avatar" or "banner":
                var field = ReadString(p, hasParams, "action")!;
                if (ReadBool(p, hasParams, "remove") == true) return Done(await rest.UpdateMeAsync(new JsonObject { [field] = null }, ct).ConfigureAwait(false));
                if (ImageData(ReadString(p, hasParams, "uploadId")) is not { } data) return new { ok = false, message = "Pick a PNG, JPEG, GIF or WebP image up to 10 MB." };
                return Done(await rest.UpdateMeAsync(new JsonObject { [field] = data }, ct).ConfigureAwait(false));
            case "consent": return Body(await rest.ConsentAsync(ct).ConfigureAwait(false), body => new
            {
                usageStatistics = Consented(body, "usage_statistics"),
                personalization = Consented(body, "personalization"),
            });
            case "setConsent":
                var grant = ConsentTypes(p, hasParams, "grant");
                var revoke = ConsentTypes(p, hasParams, "revoke");
                if (grant.Count + revoke.Count == 0) return new { ok = false, message = (string?)null };
                return Done(await rest.SetConsentAsync(grant, revoke, ct).ConfigureAwait(false));
            case "sessions": return Body(await rest.SessionsAsync(ct).ConfigureAwait(false), body => Sessions(body, session.AuthSessionIdHash));
            case "logoutSessions":
                var hashes = hasParams && p.TryGetProperty("idHashes", out var h) && h.ValueKind == JsonValueKind.Array
                    ? h.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(x => x.Length is > 0 and <= 128 && x != session.AuthSessionIdHash).Take(100).ToList()
                    : [];
                if (hashes.Count == 0) return new { ok = false, message = (string?)null };
                return Done(await rest.LogoutSessionsAsync(hashes, ct).ConfigureAwait(false));
            case "apps": return Body(await rest.AuthorizedAppsAsync(ct).ConfigureAwait(false), AuthorizedApps);
            case "deauthorize" when ReadString(p, hasParams, "id") is { } tokenId:
                return Done(await rest.DeauthorizeAsync(tokenId, ct).ConfigureAwait(false));
            case "connections": return Body(await rest.ConnectionsAsync(ct).ConfigureAwait(false), Connections);
            default: return new { ok = false, message = (string?)null };
        }
    }

    /// <summary>
    /// Discord's Shop: the catalog with what the account owns (Discord's own shapes), one product (a profile
    /// effect's layers), or equipping an owned decoration, nameplate or profile effect (skuId null takes it off).
    /// </summary>
    private static async Task<object> DiscordShopAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Discord isn't connected." };
        var rest = session.Rest;
        switch (ReadString(p, hasParams, "action"))
        {
            case "catalog":
                var categories = rest.ShopCategoriesAsync(ct);
                var owned = await rest.CollectiblesPurchasesAsync(ct).ConfigureAwait(false);
                var catalog = await categories.ConfigureAwait(false);
                return new { ok = catalog.Error is null, message = catalog.Error, catalog = catalog.Body, owned = owned.Body };
            case "product" when ReadString(p, hasParams, "skuId") is { } sku:
                var product = await rest.CollectiblesProductAsync(sku, ct).ConfigureAwait(false);
                return new { ok = product.Error is null, message = product.Error, product = product.Body };
            case "stripeKey":
                return new { ok = true, message = (string?)null, key = await DiscordRest.StripeKeyAsync(ct).ConfigureAwait(false) };
            // Add a card: Discord checks the address, then a setup intent for Stripe's own card form (in Exo's pay frame).
            case "card":
                if (DiscordRest.BillingAddress.From(name => ReadString(p, hasParams, name)) is not { } address)
                    return new { ok = false, message = (string?)"Fill in your name, address, city, postal code and country." };
                var checkedAddress = await rest.ValidateBillingAddressAsync(address, ct).ConfigureAwait(false);
                var addressToken = checkedAddress.Body is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("token", out var tk) ? tk.GetString() : null;
                if (addressToken is null) return new { ok = false, message = checkedAddress.Error ?? "Discord didn't accept that address." };
                var intent = await rest.CardSetupIntentAsync(ct).ConfigureAwait(false);
                var secret = intent.Body is { ValueKind: JsonValueKind.Object } si && si.TryGetProperty("client_secret", out var cs) ? cs.GetString() : null;
                if (secret is null) return new { ok = false, message = intent.Error ?? "Discord couldn't start adding a card." };
                return new { ok = true, message = (string?)null, key = await DiscordRest.StripeKeyAsync(ct).ConfigureAwait(false), clientSecret = secret, addressToken };
            // The card Stripe confirmed goes onto the account; the list comes back with it.
            case "cardSave" when ReadString(p, hasParams, "paymentMethod") is { Length: > 3 and < 100 } method && method.StartsWith("pm_", StringComparison.Ordinal)
                && method.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && ReadString(p, hasParams, "addressToken") is { Length: > 0 and < 4096 } token:
                if (DiscordRest.BillingAddress.From(name => ReadString(p, hasParams, name)) is not { } saveAddress)
                    return new { ok = false, message = (string?)"Fill in your name, address, city, postal code and country." };
                var added = await rest.AddCardAsync(method, saveAddress, token, ct).ConfigureAwait(false);
                Helpers.AppLog.Info($"Discord client: Shop card {(added.Error is null ? "added" : "not added")}.");
                if (added.Error is not null) return new { ok = false, message = added.Error };
                var after = await rest.PaymentSourcesAsync(ct).ConfigureAwait(false);
                return new { ok = true, message = (string?)null, payment = after.Body is { } now ? DiscordRest.PaymentChoices(now) : [] };
            // A purchase the bank must confirm: the payment intent's secret for Stripe's confirm in the pay frame.
            case "paymentSecret" when ReadString(p, hasParams, "paymentId") is { } paymentId:
                var pi = await rest.PaymentIntentSecretAsync(paymentId, ct).ConfigureAwait(false);
                var piSecret = pi.Body is { ValueKind: JsonValueKind.Object } pb && pb.TryGetProperty("stripe_payment_intent_client_secret", out var ps) ? ps.GetString() : null;
                return piSecret is null ? new { ok = false, message = pi.Error ?? "Discord didn't send the payment to confirm.", key = (string?)null, clientSecret = (string?)null }
                    : new { ok = true, message = (string?)null, key = (string?)await DiscordRest.StripeKeyAsync(ct).ConfigureAwait(false), clientSecret = (string?)piSecret };
            case "payment":
                var sources = await rest.PaymentSourcesAsync(ct).ConfigureAwait(false);
                return new { ok = sources.Error is null, message = sources.Error, payment = sources.Body is { } list ? DiscordRest.PaymentChoices(list) : [] };
            case "preview" when ReadString(p, hasParams, "skuId") is { } previewSku && ReadString(p, hasParams, "paymentSourceId") is { } previewSource:
                var preview = await rest.PurchasePreviewAsync(previewSku, previewSource, ct).ConfigureAwait(false);
                if (preview.Error is not null || preview.Body is not { ValueKind: JsonValueKind.Object } quote) return new { ok = false, message = preview.Error };
                return new
                {
                    ok = true,
                    message = (string?)null,
                    price = new
                    {
                        amount = Number(quote, "amount"),
                        subtotal = Number(quote, "subtotal"),
                        tax = Number(quote, "tax"),
                        currency = Text(quote, "currency"),
                        taxInclusive = quote.TryGetProperty("tax_inclusive", out var inclusive) && inclusive.ValueKind == JsonValueKind.True,
                    },
                };
            case "buy" when ReadString(p, hasParams, "skuId") is { } buySku && ReadString(p, hasParams, "paymentSourceId") is { } buySource
                && ReadString(p, hasParams, "currency") is { Length: 3 } currency && currency.All(char.IsAsciiLetterLower)
                && hasParams && p.TryGetProperty("amount", out var amountEl) && amountEl.TryGetInt64(out var amount) && amount is > 0 and <= 100_000:
                return await DiscordBuyAsync(rest, buySku, buySource, amount, currency, ct).ConfigureAwait(false);
            case "equip" when ReadString(p, hasParams, "kind") is "decoration" or "nameplate" or "effect":
                var kind = ReadString(p, hasParams, "kind")!;
                var skuId = ReadString(p, hasParams, "skuId");
                if (kind != "effect") return Done(await rest.EquipAsync(kind, skuId, ct).ConfigureAwait(false));
                return await EquipEffectAsync(session, skuId, ct).ConfigureAwait(false);
            default: return new { ok = false, message = (string?)null };
        }
    }

    /// <summary>A display name style's font (Discord's own file), as base64 for the page to register.</summary>
    private static async Task<object> DiscordFontAsync(JsonElement p, bool hasParams)
    {
        var bytes = await DiscordFonts.BytesAsync(ReadString(p, hasParams, "family")).ConfigureAwait(false);
        return new { ok = bytes is not null, data = bytes is null ? null : Convert.ToBase64String(bytes) };
    }

    // A profile effect, as Discord's Profiles saves it: the profile's collectibles with the effect swapped (the frame
    // stays), then read back, since Discord answers OK to a list it didn't take.
    private static async Task<object> EquipEffectAsync(DiscordSession session, string? skuId, CancellationToken ct)
    {
        if (session.Cache.Me?.Id is not { } me) return new { ok = false, message = (string?)"Discord isn't connected." };
        var before = await session.Rest.ProfileAsync(me, null, ct).ConfigureAwait(false);
        if (before.Body is not { } profile) return new { ok = false, message = before.Error };
        var keep = DiscordRest.ProfileCollectibles(profile).Where(c => c.Type != DiscordRest.ProfileEffectType).Select(c => c.Sku).ToList();
        if (skuId is not null) keep.Add(skuId);
        var saved = await session.Rest.ProfileCollectiblesAsync(keep, ct).ConfigureAwait(false);
        if (saved.Error is not null) return Done(saved);
        var after = await session.Rest.ProfileAsync(me, null, ct).ConfigureAwait(false);
        var now = after.Body is { } p ? DiscordRest.ProfileCollectibles(p).FirstOrDefault(c => c.Type == DiscordRest.ProfileEffectType).Sku : null;
        return now == skuId ? Done(saved) : new { ok = false, message = (string?)"Discord didn't change your profile effect. Try again in a moment." };
    }

    // Only with a valid card saved on the account (checked again here, not trusting the page), at the previewed price.
    private static async Task<object> DiscordBuyAsync(DiscordRest rest, string skuId, string sourceId, long amount, string currency, CancellationToken ct)
    {
        var sources = await rest.PaymentSourcesAsync(ct).ConfigureAwait(false);
        if (sources.Body is not { } list || !DiscordRest.PaymentChoices(list).Any(c => c.Id == sourceId && c.Usable))
            return new { ok = false, confirm = false, message = sources.Error ?? "Pick a card saved on your Discord account.", paymentId = (string?)null };
        var bought = await rest.PurchaseAsync(skuId, sourceId, amount, currency, ct).ConfigureAwait(false);
        Helpers.AppLog.Info($"Discord client: Shop purchase {(bought.Error is null ? "completed" : "not completed")}.");
        if (bought.Error is null) return new { ok = true, confirm = false, message = (string?)null, paymentId = (string?)null };
        return DiscordRest.NeedsConfirmation(bought.Body)
            ? new { ok = false, confirm = true, message = (string?)"Your bank needs to confirm this payment.", paymentId = DiscordRest.PaymentId(bought.Body) }
            : new { ok = false, confirm = false, message = bought.Error, paymentId = (string?)null };
    }

    private static object Done(DiscordRest.Reply reply) => new { ok = reply.Error is null, message = reply.Error };

    private static object Body(DiscordRest.Reply reply, Func<JsonElement, object> map) =>
        reply.Error is null && reply.Body is { } body
            ? new { ok = true, message = (string?)null, data = (object?)map(body) }
            : new { ok = false, message = reply.Error, data = (object?)null };

    // The account as My Account and Profiles show it; the email and phone number masked as Discord masks them.
    private static async Task<object> DiscordMeAsync(DiscordSession session, CancellationToken ct)
    {
        var me = await session.Rest.MeAsync(ct).ConfigureAwait(false);
        if (me.Error is not null || me.Body is not { ValueKind: JsonValueKind.Object } user) return new { ok = false, message = me.Error };
        var id = Text(user, "id") ?? "";
        var profile = await session.Rest.ProfileAsync(id, null, ct).ConfigureAwait(false);
        var about = profile.Body is { ValueKind: JsonValueKind.Object } pb && pb.TryGetProperty("user_profile", out var up) && up.ValueKind == JsonValueKind.Object ? up : (JsonElement?)null;
        var avatar = Text(user, "avatar");
        var banner = Text(user, "banner");
        return new
        {
            ok = true,
            message = (string?)null,
            me = new
            {
                id,
                username = Text(user, "username"),
                globalName = Text(user, "global_name"),
                avatar = avatar is null ? null : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.{(avatar.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "webp")}?size=256",
                banner = banner is null ? null : $"https://cdn.discordapp.com/banners/{id}/{banner}.{(banner.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "webp")}?size=600",
                accentColor = Number(about, "accent_color") ?? Number(user, "accent_color"),
                bio = about is { } a ? Text(a, "bio") ?? "" : Text(user, "bio") ?? "",
                pronouns = about is { } b ? Text(b, "pronouns") ?? "" : "",
                email = MaskEmail(Text(user, "email")),
                phone = MaskPhone(Text(user, "phone")),
                verified = user.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                mfa = user.TryGetProperty("mfa_enabled", out var m) && m.ValueKind == JsonValueKind.True,
                nsfwAllowed = !user.TryGetProperty("nsfw_allowed", out var n) || n.ValueKind != JsonValueKind.False,
                premium = Number(user, "premium_type") is > 0,
            },
        };
    }

    private static async Task<object> DiscordProfileEditAsync(DiscordRest rest, JsonElement p, bool hasParams, CancellationToken ct)
    {
        var body = new JsonObject();
        if (ReadString(p, hasParams, "bio") is { } bio)
        {
            if (bio.Length > 190) return new { ok = false, message = "About Me can be up to 190 characters." };
            body["bio"] = bio;
        }
        if (ReadString(p, hasParams, "pronouns") is { } pronouns)
        {
            if (pronouns.Length > 40) return new { ok = false, message = "Pronouns can be up to 40 characters." };
            body["pronouns"] = pronouns;
        }
        if (hasParams && p.TryGetProperty("accentColor", out var color))
        {
            if (color.ValueKind == JsonValueKind.Null) body["accent_color"] = null;
            else if (color.TryGetInt32(out var rgb) && rgb is >= 0 and <= 0xFFFFFF) body["accent_color"] = rgb;
            else return new { ok = false, message = (string?)null };
        }
        if (body.Count == 0) return new { ok = false, message = (string?)null };
        return Done(await rest.UpdateProfileAsync(body, ct).ConfigureAwait(false));
    }

    // An uploaded image (discord.upload) as the data: URI Discord takes for an avatar or banner.
    private static string? ImageData(string? uploadId)
    {
        (string Name, string Type, MemoryStream Data, long Started) upload;
        lock (Uploads) if (uploadId is null || !Uploads.Remove(uploadId, out upload)) return null;
        using var data = upload.Data;
        if (upload.Type is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp") || data.Length is 0 or > MaxImageBytes) return null;
        return $"data:{upload.Type};base64,{Convert.ToBase64String(data.ToArray())}";
    }

    private static List<string> ConsentTypes(JsonElement p, bool hasParams, string name) =>
        hasParams && p.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(x => x is "usage_statistics" or "personalization").Distinct()]
            : [];

    private static bool Consented(JsonElement body, string type) =>
        body.TryGetProperty(type, out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("consented", out var on) && on.ValueKind == JsonValueKind.True;

    private static object Sessions(JsonElement body, string? current) =>
        body.TryGetProperty("user_sessions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(s =>
            {
                var info = s.TryGetProperty("client_info", out var ci) && ci.ValueKind == JsonValueKind.Object ? ci : (JsonElement?)null;
                var hash = Text(s, "id_hash");
                return new
                {
                    idHash = hash,
                    os = info is { } i ? Text(i, "os") : null,
                    platform = info is { } j ? Text(j, "platform") : null,
                    location = info is { } k ? Text(k, "location") ?? Text(k, "ip") : null,
                    lastUsed = Text(s, "approx_last_used_time"),
                    current = hash is not null && hash == current,
                };
            }).ToArray()
            : [];

    private static object AuthorizedApps(JsonElement body) =>
        body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().Select(t =>
            {
                var app = t.TryGetProperty("application", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                var appId = app.ValueKind == JsonValueKind.Object ? Text(app, "id") : null;
                var icon = app.ValueKind == JsonValueKind.Object ? Text(app, "icon") : null;
                return new
                {
                    id = Text(t, "id"),
                    name = app.ValueKind == JsonValueKind.Object ? Text(app, "name") : null,
                    description = app.ValueKind == JsonValueKind.Object ? Text(app, "description") : null,
                    icon = appId is null || icon is null ? null : $"https://cdn.discordapp.com/app-icons/{appId}/{icon}.webp?size=64",
                    scopes = t.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array ? sc.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray() : [],
                };
            }).Where(a => a.id is not null).ToArray()
            : [];

    private static object Connections(JsonElement body) =>
        body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().Select(c => new
            {
                type = Text(c, "type"),
                name = Text(c, "name"),
                verified = c.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                revoked = c.TryGetProperty("revoked", out var r) && r.ValueKind == JsonValueKind.True,
                visible = Number(c, "visibility") == 1,
            }).ToArray()
            : [];

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Number(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

    /// <summary>"j*******@gmail.com": the first letter and the domain, as Discord shows an email before Reveal.</summary>
    internal static string? MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        var at = email.LastIndexOf('@');
        return at <= 0 ? new string('*', email.Length) : email[0] + new string('*', Math.Max(1, at - 1)) + email[at..];
    }

    /// <summary>"*******1234": the last four digits.</summary>
    internal static string? MaskPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return null;
        var digits = new string([.. phone.Where(char.IsAsciiDigit)]);
        return digits.Length <= 4 ? new string('*', digits.Length) : new string('*', digits.Length - 4) + digits[^4..];
    }

    /// <summary>Exo's own settings for its Discord client: read them, or save the ones given.</summary>
    private static object DiscordClientSettings(JsonElement p, bool hasParams)
    {
        if (ReadBool(p, hasParams, "streamerQuiet") is { } quiet) DiscordSession.StreamerQuiet = quiet;
        if (ReadBool(p, hasParams, "streamerSilent") is { } silent) DiscordSounds.StreamerSilent = silent;
        var sounds = hasParams && p.TryGetProperty("disabledSounds", out var ds) && ds.ValueKind == JsonValueKind.Array
            ? ds.EnumerateArray().Select(x => x.GetString()).Where(DiscordSounds.IsSound).OfType<string>().Distinct().ToList()
            : null;
        var keybinds = hasParams && p.TryGetProperty("keybinds", out var k) && k.ValueKind == JsonValueKind.Array ? Keybinds(k) : null;
        var profile = ReadString(p, hasParams, "inputProfile") is { } ip && ip is "exo" or "custom" ? ip : null;
        var badgeBefore = App.Services.Settings.Current.DiscordUnreadBadge;
        App.Services.Settings.UpdateProfile(s =>
        {
            ApplyClientSettings(s, p, hasParams, profile, keybinds);
            if (sounds is not null) s.DiscordDisabledSounds = sounds;
        });
        var settings = App.Services.Settings.Current;
        if (keybinds is not null) DiscordSession.Current?.KeybindsChanged();
        if (settings.DiscordUnreadBadge != badgeBefore) App.MainAppWindow?.RefreshTaskbarBadge();
        if (VoiceProcessing.From(settings).NoiseSuppression || settings.DiscordOthersNoiseSuppression) _ = NoiseSuppressorDownload.EnsureAsync(CancellationToken.None);
        return new
        {
            inputProfile = VoiceProcessing.From(settings).Profile,
            noiseSuppression = settings.DiscordNoiseSuppression,
            othersNoiseSuppression = settings.DiscordOthersNoiseSuppression,
            echoCancellation = settings.DiscordEchoCancellation,
            autoGain = settings.DiscordAutoGain,
            voiceFocus = settings.DiscordVoiceFocus,
            pushToTalk = settings.DiscordPushToTalk,
            pttDelay = settings.DiscordPttDelay,
            autoSensitivity = settings.DiscordAutoSensitivity,
            sensitivity = settings.DiscordSensitivity,
            attenuation = settings.DiscordAttenuation,
            attenuateSelf = settings.DiscordAttenuateSelf,
            attenuateOthers = settings.DiscordAttenuateOthers,
            keybinds = settings.DiscordKeybinds.Select(b => new { action = b.Action, keys = b.Keys.ToArray() }).ToArray(),
            desktopNotifications = settings.DiscordDesktopNotifications,
            unreadBadge = settings.DiscordUnreadBadge,
            taskbarFlash = settings.DiscordTaskbarFlash,
            disabledSounds = settings.DiscordDisabledSounds.ToArray(),
            disableAllSounds = settings.DiscordDisableAllSounds,
            selectedChannelSound = settings.DiscordSelectedChannelSound,
            soundVolume = settings.DiscordSoundVolume,
        };
    }

    private static void ApplyClientSettings(AppSettings s, JsonElement p, bool hasParams, string? profile, List<DiscordKeybind>? keybinds)
    {
        if (profile is not null) s.DiscordInputProfile = profile;
        if (ReadBool(p, hasParams, "noiseSuppression") is { } ns) s.DiscordNoiseSuppression = ns;
        if (ReadBool(p, hasParams, "othersNoiseSuppression") is { } forOthers) s.DiscordOthersNoiseSuppression = forOthers;
        if (ReadBool(p, hasParams, "echoCancellation") is { } ec) s.DiscordEchoCancellation = ec;
        if (ReadBool(p, hasParams, "autoGain") is { } ag) s.DiscordAutoGain = ag;
        if (ReadBool(p, hasParams, "voiceFocus") is { } vf) s.DiscordVoiceFocus = vf;
        if (ReadBool(p, hasParams, "pushToTalk") is { } ptt) s.DiscordPushToTalk = ptt;
        if (ReadDouble(p, hasParams, "pttDelay") is { } delay) s.DiscordPttDelay = (int)Math.Clamp(delay, 0, 2000);
        if (ReadBool(p, hasParams, "autoSensitivity") is { } auto) s.DiscordAutoSensitivity = auto;
        if (ReadDouble(p, hasParams, "sensitivity") is { } db) s.DiscordSensitivity = (int)Math.Clamp(db, -100, 0);
        if (ReadDouble(p, hasParams, "attenuation") is { } att) s.DiscordAttenuation = (int)Math.Clamp(att, 0, 100);
        if (ReadBool(p, hasParams, "attenuateSelf") is { } self) s.DiscordAttenuateSelf = self;
        if (ReadBool(p, hasParams, "attenuateOthers") is { } others) s.DiscordAttenuateOthers = others;
        if (keybinds is not null) s.DiscordKeybinds = keybinds;
        if (ReadBool(p, hasParams, "desktopNotifications") is { } dn) s.DiscordDesktopNotifications = dn;
        if (ReadBool(p, hasParams, "unreadBadge") is { } ub) s.DiscordUnreadBadge = ub;
        if (ReadBool(p, hasParams, "taskbarFlash") is { } tf) s.DiscordTaskbarFlash = tf;
        if (ReadBool(p, hasParams, "disableAllSounds") is { } all) s.DiscordDisableAllSounds = all;
        if (ReadBool(p, hasParams, "selectedChannelSound") is { } selected) s.DiscordSelectedChannelSound = selected;
        if (ReadDouble(p, hasParams, "soundVolume") is { } volume) s.DiscordSoundVolume = (int)Math.Clamp(volume, 0, 100);
    }

    private static List<DiscordKeybind> Keybinds(JsonElement list) =>
        [.. list.EnumerateArray().Take(50).Select(b => new DiscordKeybind
        {
            Action = b.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
            Keys = b.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array
                ? [.. k.EnumerateArray().Where(x => x.TryGetInt32(out var key) && key is > 0 and < 255).Select(x => x.GetInt32()).Distinct().Take(4)]
                : [],
        }).Where(b => DiscordClient.Voice.Keybinds.IsAction(b.Action) && b.Keys.Count > 0)];

    /// <summary>Plays one of Discord's sounds: Preview Sound in the Sounds lists, or one the room asks for (Camera On and Off).</summary>
    private static object DiscordSound(JsonElement p, bool hasParams)
    {
        if (ReadString(p, hasParams, "name") is { } name && DiscordSounds.IsSound(name)) DiscordSounds.Play(name, preview: ReadBool(p, hasParams, "preview") == true);
        return new { ok = true };
    }

    /// <summary>Voice Tune: record (the five parts' seconds), cancel it, keep, forget, or fix what Windows does (unmute, no ducking).</summary>
    private static async Task<object> DiscordVoiceTuneAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        switch (ReadString(p, hasParams, "action"))
        {
            case "record":
                var parts = hasParams && p.TryGetProperty("parts", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(x => x.TryGetInt32(out var n) ? n : 0).ToArray() : [];
                return await VoiceTune.RecordAsync(parts, ct).ConfigureAwait(true);
            case "cancel": return VoiceTune.Cancel();
            case "state": return VoiceTune.State();
            case "keep": return VoiceTune.Keep();
            case "forget": return VoiceTune.Forget();
            case "unmute": return VoiceTune.Unmute();
            case "noDucking": return VoiceTune.SetNoDucking();
            default: return new { ok = false, message = "Unknown action." };
        }
    }

    /// <summary>The Input Sensitivity meter: { on } starts or renews it, off stops it.</summary>
    private static object DiscordMicTest(JsonElement p, bool hasParams)
    {
        if (ReadBool(p, hasParams, "on") == true) MicTest.Start();
        else MicTest.Stop();
        return new { ok = true };
    }
}
