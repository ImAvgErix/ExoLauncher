using System.Globalization;
using System.Text.Json;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Threads and forum posts as Discord's client keeps them: whether you joined one, its notification setting and mute (your
/// thread membership), and whether it's closed, locked, pinned or tagged. The thread menu reads it; the sidebar lists the
/// joined, open threads under their channel.
/// </summary>
internal sealed partial class DiscordCache
{
    internal const ulong ManageThreads = 1UL << 34;
    // A forum's pinned post (channel flag PINNED).
    internal const int PinnedPost = 1 << 1;
    // Thread member flags for its notifications: all messages, only @mentions, nothing.
    internal const int ThreadAll = 1 << 1, ThreadMentions = 1 << 2, ThreadNothing = 1 << 3;

    // Discord's thread member object for you (in READY's threads, THREAD_CREATE, a search, THREAD_LIST_SYNC or
    // THREAD_MEMBER_UPDATE): you're in the thread, with its flags and mute.
    private static void ReadMembership(Channel thread, JsonElement member)
    {
        thread.Joined = true;
        thread.MemberFlags = Int(member, "flags");
        thread.ThreadMuted = Bool(member, "muted");
        thread.ThreadMuteEnds = member.TryGetProperty("mute_config", out var mute) && mute.ValueKind == JsonValueKind.Object && Str(mute, "end_time") is { } end
            && DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    }

    private static void KeepMembership(Channel thread, Channel old)
    {
        thread.Joined = old.Joined;
        thread.MemberFlags = old.MemberFlags;
        thread.ThreadMuted = old.ThreadMuted;
        thread.ThreadMuteEnds = old.ThreadMuteEnds;
    }

    private static void Unjoin(Channel thread)
    {
        thread.Joined = false;
        thread.MemberFlags = 0;
        thread.ThreadMuted = false;
        thread.ThreadMuteEnds = null;
    }

    private static bool ThreadMutedNow(Channel c) =>
        c.Type is 10 or 11 or 12 && c.ThreadMuted && (c.ThreadMuteEnds is not { } end || end > DateTimeOffset.UtcNow);

    // A thread's own notification setting: 0 all messages, 1 only @mentions, 2 nothing; null as its channel.
    private static int? ThreadNotify(int flags) =>
        (flags & ThreadNothing) != 0 ? 2 : (flags & ThreadMentions) != 0 ? 1 : (flags & ThreadAll) != 0 ? 0 : null;

    private void OnThreadMembers(string type, JsonElement d, List<(string, object)> events)
    {
        var guildId = Str(d, "guild_id");
        if (guildId is null || !_guilds.TryGetValue(guildId, out var guild)) return;
        switch (type)
        {
            // The open threads of some channels (channel_ids) or of the whole server, and your membership of them; one
            // of those channels' threads left out is no longer open.
            case "THREAD_LIST_SYNC":
                var synced = new HashSet<string>();
                foreach (var t in Array(d, "threads"))
                {
                    var thread = ReadChannel(t, guildId);
                    if (thread.Type is not (10 or 11 or 12) || thread.ParentId is null) continue;
                    if (guild.Channels.TryGetValue(thread.Id, out var old)) { thread.LastMessageId ??= old.LastMessageId; KeepMembership(thread, old); }
                    guild.Channels[thread.Id] = thread;
                    synced.Add(thread.Id);
                }
                if (d.TryGetProperty("channel_ids", out var channels) && channels.ValueKind == JsonValueKind.Array)
                {
                    var parents = channels.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToHashSet();
                    foreach (var gone in guild.Channels.Values.Where(c => c.Type is 10 or 11 or 12 && c.ParentId is { } p && parents.Contains(p) && !synced.Contains(c.Id)))
                        gone.Archived = true;
                }
                foreach (var m in Array(d, "members"))
                    if (Str(m, "id") is { } id && guild.Channels.TryGetValue(id, out var joined)) ReadMembership(joined, m);
                break;
            // Your own membership changed: joined here or in another app, its notification setting or mute.
            case "THREAD_MEMBER_UPDATE":
                if (Str(d, "id") is { } mine && guild.Channels.TryGetValue(mine, out var yours)) ReadMembership(yours, d);
                break;
            // Who joined or left; you among them when you joined or left in another app.
            case "THREAD_MEMBERS_UPDATE":
                if (Me is null || Str(d, "id") is not { } threadId || !guild.Channels.TryGetValue(threadId, out var changed)) return;
                foreach (var added in Array(d, "added_members"))
                    if (Str(added, "user_id") == Me.Id) ReadMembership(changed, added);
                if (Array(d, "removed_member_ids").Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == Me.Id)) Unjoin(changed);
                break;
        }
        events.Add(("discord.changed", new { guildId }));
    }

    /// <summary>A thread as its menu shows it: joined, closed, locked, pinned, muted, its notifications, its tags and what you may change.</summary>
    internal object? ThreadState(string threadId)
    {
        lock (_gate)
        {
            if (Find(threadId) is not { Type: 10 or 11 or 12, GuildId: not null } thread) return null;
            var parent = thread.ParentId is { } p ? Find(p) : null;
            var forum = parent?.Type is 15 or 16;
            var muted = ThreadMutedNow(thread);
            return new
            {
                id = thread.Id,
                name = thread.Name ?? "",
                parentId = thread.ParentId,
                guildId = thread.GuildId,
                joined = thread.Joined,
                archived = thread.Archived,
                locked = thread.Locked,
                pinned = (thread.Flags & PinnedPost) != 0,
                muted,
                mutedUntil = muted ? thread.ThreadMuteEnds?.ToUnixTimeMilliseconds() : null,
                notify = ThreadNotify(thread.MemberFlags),
                unread = Unread(thread),
                mine = Me is not null && thread.OwnerId == Me.Id,
                manage = (Perms(thread) & ManageThreads) != 0,
                forum,
                tags = forum ? parent!.Tags.Select(t => new { id = t.Id, name = t.Name, emojiId = t.EmojiId, emojiName = t.EmojiName }).ToList() : null,
                applied = thread.AppliedTags.ToList(),
            };
        }
    }

    /// <summary>What you may change on a thread, for the bridge's checks; null when it isn't a thread Exo knows.</summary>
    internal (bool Mine, bool Manage, bool Forum, int Flags, int MemberFlags, HashSet<string> Tags)? ThreadRights(string threadId)
    {
        lock (_gate)
        {
            if (Find(threadId) is not { Type: 10 or 11 or 12, GuildId: not null } thread) return null;
            var parent = thread.ParentId is { } p ? Find(p) : null;
            return (Me is not null && thread.OwnerId == Me.Id, (Perms(thread) & ManageThreads) != 0, parent?.Type is 15 or 16,
                thread.Flags, thread.MemberFlags, parent?.Tags.Select(t => t.Id).ToHashSet() ?? []);
        }
    }

    /// <summary>Your membership once Discord took the change: joined or left, its flags, its mute (until: null for good).</summary>
    internal void SetThreadMember(string threadId, bool? joined = null, int? flags = null, bool? muted = null, DateTimeOffset? until = null)
    {
        lock (_gate)
        {
            if (Find(threadId) is not { Type: 10 or 11 or 12 } thread) return;
            if (joined == false) { Unjoin(thread); return; }
            if (joined == true) thread.Joined = true;
            if (flags is { } f) thread.MemberFlags = f;
            if (muted is { } m) { thread.ThreadMuted = m; thread.ThreadMuteEnds = m ? until : null; }
        }
    }

    /// <summary>The thread once Discord took the change: closed, locked, its flags (pinned) or its tags.</summary>
    internal void SetThread(string threadId, bool? archived = null, bool? locked = null, int? flags = null, List<string>? tags = null)
    {
        lock (_gate)
        {
            if (Find(threadId) is not { Type: 10 or 11 or 12 } thread) return;
            if (archived is { } a) thread.Archived = a;
            if (locked is { } l) thread.Locked = l;
            if (flags is { } f) thread.Flags = f;
            if (tags is not null) thread.AppliedTags = tags;
        }
    }
}
