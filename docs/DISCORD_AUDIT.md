# Exo's Discord client against Discord

An audit of Exo's own Discord client (`ui/src/components/discord/`, `ExoLauncher/Services/DiscordClient/`) against Discord's desktop and web client, the fixes made on the `discord-audit` branch, and what is left.

## How this was done

- **Discord's side comes from Discord's code.** On October 20, 2026, the `discord.com/app` page and every script its webpack runtime can load were downloaded: the 12 MB entry bundle `web.d41b6bfa9f8fcb8b.js`, the 353 other chunks the page loads first, and the 5,833 chunks the webpack runtime can load later. Discord's strings are hashed (`intl.string(t.t.cvSt1J)`), so the en-US message chunks (86 of them, 30,157 keys) were decoded into a key-to-English table, and every code site was read with the English back in place. Context menus were resolved from their `navId` through each item's hook module to its item ids and labels.
- **Citations.** A Discord citation names the bundle file (`web.d41b6bfa9f8fcb8b.js`, or a lazy chunk by its hash), plus a webpack module id (`mod 573163`, `m355682`), a menu item id (`id:"block"`), a byte offset (`@5617252`) or a dehashed string («Add Friend»). An Exo citation is `path:line`.
- **Exo's side was read, not grepped.** Each row was checked against the component and the host handler behind it. Five read-only passes covered the areas below, and each fix on this branch was checked against Discord's code again before it was written.
- **Line numbers** in the area tables are as of commit `8166098`, where the audit was taken. Fixes after it move some lines; a row a fix changed says so and names the commit.
- **Nothing was sent to Discord.** No sign-in, no requests, no tokens.
- **What was verified, and how.**
  - UI gates all pass on the final tree: `npx tsc -b`, `npm run -s lint`, `npm run -s lint:css`, `npm run -s contracts:write` then `contracts`, `npm run -s dead`, `npm run -s css:prune`.
  - The changed menus and dialogs were driven in Chromium against Exo's dev UI and mock host, and screenshotted: the person menu, the popout, Block and Ignore, the channel menu and its submenus, Pin, and Unpin in the pins popout.
  - The host can't build here: it is WinUI on `net11.0-windows`, and this is Linux. `DiscordCache.cs`, `DiscordCache.Admin.cs` and `DiscordRest*.cs` were compiled on .NET 10 against small stubs. The new read rules ran in a scratch harness against the real `DiscordCache` with READY, THREAD_CREATE and ack inputs.
  - `WebHostBridge.Discord.cs` and `DiscordSession.cs` were not compiled.
  - `dotnet test ExoLauncher.Tests` was not run.
  - Nothing ran in the installed app.

## Status at a glance

| Area | Match | Different | Missing | Fixed on this branch |
|---|---|---|---|---|
| Servers, channels, DMs, group DMs | 36 | 23 | 71 | 7 |
| Messages, composer, app commands | 69 | 49 | 48 | 3 |
| Notifications, Inbox, moderation, profiles | 45 | 37 | 44 | 3 |
| Voice and video UI, friends, members, presence | 43 | 44 | 58 | 5 |
| Settings (user, server, channel) | 67 | 36 | 67 | 6 |
| **All 761 rows** | **260** | **189** | **288** | **24** |

"Fixed" counts rows that were Different or Missing at the audit and are now Discord's behavior. Rows a fix only partly closed still read Different, with what is left.

## The two reported problems

### 1. The white unread dot that stays when you're caught up

Discord's rules are in its ReadStateStore (`web.d41b6bfa9f8fcb8b.js` mod 573163: class `ew`, `shouldAutomaticallyAck` `eU`, `ack()`, `getAckTimestamp()`, `canBeUnread()`, `getGuildChannelUnreadState()`, the MESSAGE_CREATE, MESSAGE_DELETE, CHANNEL_SELECT, WINDOW_FOCUS and UPDATE_CHANNEL_DIMENSIONS handlers). Exo differed in six ways, and each one can leave a dot lit:

| # | What Discord does | What Exo did | Fix |
|---|---|---|---|
| 1 | An automatic ack carries no message id, so `ack()` acks the read state's `lastMessageId`: the channel's last message as the gateway reported it. MESSAGE_DELETE only rebuilds counts and never moves it back. | Acked the newest message shown. When the newest message was deleted (a bot's, AutoMod's, anyone's), the channel's last message id stayed ahead of every ack and the dot never cleared. | `a025144`: `DiscordCache.MarkRead` reads to the newest of the message given, the channel's last message and where it was read, and never moves back (`DiscordCache.cs:514`). Mark Unread sets the mark exactly (`DiscordCache.cs:526`). |
| 2 | Acks when `ChannelScrollPositionStore.isAtBottom`, which is kept current by UPDATE_CHANNEL_DIMENSIONS on every scroll and resize. | Remembered "at the bottom" only from scroll events. A jump that lands on the newest message (from a notification, search, the Inbox or the NEW bar) or a channel too short to scroll sends no scroll, so it never acked. Reaching the present by scrolling down after a jump left the flag false too. | `a025144`: Chat measures the scroller whenever it might ack (`Chat.tsx:77-85`). |
| 3 | A forum on screen acks itself ("Ack - Forum Active Threads"). THREAD_CREATE sets the forum's `lastMessageId` to the new post and acks it when the post is yours. | The forum view never acked, so a forum with any post newer than its read mark stayed lit for good. New posts didn't move the forum's last message either. | `a025144`: ForumView acks when shown (`Forum.tsx:18`). THREAD_CREATE moves the forum's last message and reads your own post (`DiscordCache.cs:477`). |
| 4 | Mark As Read (`CHANNEL_ACK`, `BULK_ACK`) and Mark Unread change the read state locally at once. | Changed the host's cache but never told the page, so the dot stayed until Discord's MESSAGE_ACK echo, if one came. | `a1552c0`: the channel's Mark As Read goes through `DiscordSession.Ack`. A server's Mark As Read and Mark Unread send `discord.unread` for each channel (`DiscordSession.cs:393`, `:402`). A server's Mark As Read also takes muted channels' mentions, as `BULK_ACK` does. |
| 5 | `getAckTimestamp()`: a channel with no read state counts as read up to when you joined the server (`guild.joinedAt`). | Counted it unread from the channel's first message. | `8166098`: `ReadMark` falls back to the server's `joined_at` (`DiscordCache.cs:1461`). |
| 6 | `canBeUnread()` and `getGuildChannelUnreadState()`: in an opt-in server (`OPT_IN_CHANNELS_ON`, COMMUNITY), only channels opted in (or in an opted-in category) light up, plus channels read in the last 3 days or mentioning you, and none quiet for 7 days. | Lit the server pill for channels its own list hides, which you can't open to clear. | `8166098`: `Followed` and `OptInMode` (`DiscordCache.cs:1467`, `:1477`). Exo's list also shows private channels, and they keep their dot. Show All Channels and the list now use the same COMMUNITY test. |

**Still different:** Discord acks only while its window is focused and you aren't idle (`eU`: `isAnyWindowFocused`, `isIdle`), and acks again on WINDOW_FOCUS. Exo acks whenever the Discord room is on screen, so messages that arrive while Exo is behind a game are marked read. This is a proposal (P1 below): the page has no reliable window-focus signal today, and a wrong one would leave dots lit.

### 2. The profile popout's "..." menu

Discord has two menus where Exo had one:

- **The profile's More** (`navId:"user-profile-overflow-menu"`, `bbbb6bdccf864c15.js` mod 413737 `ej`), grouped as Discord groups it:
  1. View Full Profile, Share Profile, the Nitro name-style trial, Shop This Look, Invite to Server, Share Nitro.
  2. Ignore / Unignore (mod 399476), Block / Unblock (mod 239211), Report User Profile.
  3. Copy User ID, or a Copy User Info submenu.
- **The banner's friend button** beside More (mod 193738, from `user-profile-friend-request-buttons`): Add Friend, Accept Friend Request or Pending, and for a friend a Friends menu with Remove Friend. Not shown for bots, yourself or someone blocked.
- **The right-click person menu** (`navId:"user-context"`; in a server `7d2e0db879ea1519.js` mod 365689), in this order:
  1. Profile, Mention, Message, Start a Call, Add Note, Add Friend Nickname, Watch Stream.
  2. User Volume.
  3. Apps.
  4. Mute and Deafen, Change Nickname, Invite to Server, Add Friend / Remove Friend, game friends, Manage App, Ignore, Block.
  5. Move to.
  6. Roles, Server Mute, Server Deafen, Disconnect, Verify, Timeout, Kick, Ban.
  7. Copy User ID.

Exo's popout More (`extras.tsx:88`) and its full profile's More (`DmProfile.tsx:122`) both open the person menu (`UserMenu.tsx`). That menu had no relationship rows at all, and the popout had no friend button.

- **Fixed in `6937c04` (person menu, right-click and More):**
  - Start a Call: opens the DM and rings. Disabled while blocked; not shown for bots.
  - Add Friend, Accept Friend Request, Friend Request Sent (disabled) and Remove Friend, which asks "Remove '{name}'".
  - Ignore and Unignore (not while blocked), Block and Unblock. Block asks "Block {username}?" and Ignore asks "Ignore {username}?", each offering the other ("Ignore instead", "Block instead"). The dialogs keep only the points that hold in Exo.
  - The host gained `block`, `ignore`, `unignore` and a cache-only `state` on `discord.friend` (`WebHostBridge.Discord.cs:298-306`). It reads `user_ignored` from relationships, and people you ignore no longer notify (`DiscordCache.cs:860`), as Discord's `isBlockedOrIgnored` rule does.
- **Fixed in `1a9e87e` (popout):** the friend button beside More (`extras.tsx:86`, `:115`).
- **Still missing:**
  - **Report User Profile.** Discord's in-app reporting flow; a new host flow and a policy surface.
  - **Invite to Server.** Needs a host call that makes a one-use invite in each server's default invitable channel and DMs it.
  - **Add Note / Edit Note in the menu.** The note comes only with a profile fetch; the popout and full profile already have the note field.
  - **Add Friend Nickname.** Needs the relationship nickname PATCH and reading `nickname`.
  - **Share Profile and Copy User Info.**
  - Exo's **Copy Username** is a menu row; Discord's is a copy icon by the username in the profile.

## Everything fixed on this branch

| Discord behavior matched | Commit | Where |
|---|---|---|
| Ack to the channel's last message, never back; ack measured at the bottom; forums read when shown; new forum posts light the forum | `a025144` | `DiscordCache.cs`, `Chat.tsx`, `Forum.tsx` |
| Mark As Read (channel, Inbox, server) and Mark Unread change the dots at once | `a1552c0` | `DiscordSession.cs`, `WebHostBridge.Discord.cs` |
| Never-read channels read up to your join; opt-in servers light up only for listed channels | `8166098` | `DiscordCache.cs` |
| Person menu: Start a Call, Add Friend / Accept / Friend Request Sent / Remove Friend, Ignore / Unignore, Block / Unblock; ignored people don't notify | `6937c04` | `UserMenu.tsx`, `DiscordClient.tsx`, `DiscordRest.cs`, `DiscordCache.cs`, `WebHostBridge.Discord.cs` |
| Popout friend button beside More | `1a9e87e` | `extras.tsx`, `discord.css` |
| Channel menu: Mute Channel with durations, Unmute with its end, Notification Settings (Use Category or Server Default, All Messages, Only @mentions, Nothing; not on voice), Mark As Read off when nothing is unread, Invite People says it copied | `497ec0f` | `Sidebar.tsx` |
| Show All Channels in community servers; no Leave Server for the owner | `db49141` | `Sidebar.tsx` |
| Right-click a friend row opens the person menu | `7bbe38b` | `FriendsPage.tsx` |
| "A and B are typing…", "A, B, and C are typing…"; Esc in the message box marks the channel read | `a8c98e9` | `Composer.tsx`, `Chat.tsx` |
| A submenu's row keeps its text on hover (Roles, Move to and Apps had turned near-black) | `0df0dab` | `discord-admin.css` |
| Add Reaction first in the message menu | `0a486ae` | `Message.tsx` |
| discord.com/channels links open in the room at the message | `170896a` | `markdown.tsx` |
| Pin and Unpin ask first, in Discord's words; Shift skips | `606408a` | `MessageConfirm.tsx` (was `DeleteConfirm.tsx`), `Chat.tsx` |
| Unpin in the pins popout | `9e2d751` | `ChatHeader.tsx`, `results.tsx` |
| "Use data to personalize my Discord experience"; Deauthorize asks first | `8f765c0` | `settings/PrivacyPages.tsx` |
| Show spoiler content: "On servers I moderate" works | `75f0b35` | `markdown.tsx`, `Attachments.tsx` |
| Discord's wording: AutoMod actions and lists, Delete Role, Profiles tabs and Bio, Members sort, Engagement | `9814cda` | settings pages |

`9e2d751` carries `MessageConfirm.tsx`'s rename without its new content, so that one commit doesn't build alone; `606408a` completes it. History was not rewritten.

## What's left, by priority

"Needs host" means a new host RPC or cache field. Everything else is UI-only on RPCs Exo already has. Voice and video items stay out of `ExoLauncher/Services/DiscordClient/Voice`. Collectibles and the Shop belong to another session.

### P1: basics people hit every day

1. **Server right-click menu on the rail** (`navId:"guild-context"`, mod 237600):
   - UI-only: Mark As Read, Invite to Server, Show All Channels, Server Settings, Create Channel and Leave Server (not for the owner), Copy Server ID.
   - Needs host: Mute Server and Notification Settings, a `PATCH users/@me/guilds/{id}/settings`.
   - The WebView's own menus are off (`MainWindow.xaml.cs:614`), so today a right-click on a server, folder, category or group DM does nothing.
2. **Server Notification Settings and Mute Server** (D4 modal, mod 585617): level, suppress @everyone/@here, suppress roles, highlights, mobile push, overrides, Hide Muted Channels. Needs host.
3. **DM and group DM menus** (mod 385913, `gdm-context`):
   - Close DM and Copy Channel ID are UI-only.
   - Mark As Read needs host: the DM read path; `discord.channel` 'read' is server-only for DMs.
   - Mute @user / Mute Conversation needs host.
   - Leave Group needs Discord's "leave without notifying".
4. **Window focus and idle for acks** (ReadStateStore `eU`). Needs a reliable focus signal from the host (`App.MainAppWindow.WindowIsActive`) sent to the page, and an ack on focus.
5. **Collapse blocked and ignored users' messages** ("1 blocked message — Show"; `78f77b89` `MESSAGE_GROUP_BLOCKED` / `IGNORED`). Needs host: the blocked and ignored ids sent to the page; the cache already has both.
6. **Category right-click menu** (mod 740438): Mark As Read, Collapse All, Mute Category, Notification Settings, Edit and Delete Category. Mostly UI-only; the host's mute and notify paths accept a category id.
7. **Unread DMs on the rail** under Home (`@5617252`, `getUnreadPrivateChannelIds`). UI-only.
8. **Message menu and hover bar:**
   - the quick-reaction row and the hover bar's 3 frecent reactions (m355682 `tA`/`td`, 78f77b89 `nh`);
   - Forward and Edit hidden where Discord refuses them (polls, voice messages, forwards, ephemeral);
   - Copy Link and Open Link on a right-clicked link, and Copy on selected text;
   - Shift+Reply turns the ping off; no @ toggle in DMs.
9. **Failed sends stay in the list** with Retry and Delete (`78f77b89` `ng`), instead of the text going back to the box.
10. **Moderation dialogs:**
    - Timeout opens Discord's dialog: duration buttons and a reason. UI-only; the host already reads `reason`.
    - Ban starts with no reason chosen.
    - Kick and Ban use Discord's titles.
11. **Inbox matches Discord's current one.**
    - Discord retired For You; Unreads is first.
    - Add Mark All as Read (confirm, Shift skips).
    - Rename the Mentions filters to Discord's labels and keep them.
    - Each Mentions × becomes "Mark As Read".
    - Ctrl+I opens the Inbox.
12. **Thread notifications.**
    - Bug: the header bell on a thread saves a channel override Discord ignores for threads.
    - Fix: Discord keeps a thread's level in the thread member's flags (mod 607508, `PATCH channels/{id}/thread-members/@me/settings`). Needs host. Hide the bell on threads until then.
13. **Audio device menu on mute and deafen right-click** (`audio-device-context`): devices, volumes, Push to Talk, Noise Suppression. UI-only on `discord.audio`; coordinate with the voice session.

### P2: parity people notice

- **Composer:**
  - Character counter and "too long, Upload as File", with Discord's 2000/4000 limits by Nitro.
  - Drafts kept across restarts.
  - Built-in commands: /shrug, /tableflip, /unflip, /me, /spoiler, `+:emoji:`, `s/old/new/`.
  - "Too many uploads!" instead of silently dropping files.
  - @silent needs host: `flags` is hard-coded 0.
  - Ctrl+E, Ctrl+G and Ctrl+S open the pickers.
- **Message actions:**
  - Speak Message.
  - Remove Reactions and Remove All Reactions (needs host).
  - Remove all embeds (needs host).
  - Publish in announcement channels (needs host).
  - Show Results and Go Back to Vote in polls.
  - Voice message playback speed.
- **Rendering:**
  - Timestamps: the `<t:x:s>` and `<t:x:S>` formats, live relative times, tooltips on the time and on "(edited)".
  - Command mentions drawn as chips.
  - "Leaving Discord" warning on masked links.
  - Grouping that never tucks a message under a system line.
  - System lines that name their target and link to "See all".
- **Friends:**
  - Pending split into Received and Sent.
  - Empty tabs hidden; Discord's empty states and Add Friend messages.
  - Start Voice Call and Start Video Call in the row menu.
  - Friend nicknames (needs host).
  - The Ignore & Block lists, which Discord moved into Settings.
- **Profiles:**
  - The popout's "Message @{name}" box.
  - Bio without a heading in the popout, with "View Full Bio".
  - An Activity tab with its empty state.
  - "Originally known as".
  - Bots show "Created On".
  - Edit Per-server Profile from a server.
- **Settings:**
  - Per-server Privacy Settings dialog: the account fields already exist.
  - Streamer Mode hides invite codes in Server Settings > Invites.
  - Settings search.
  - Channel Settings: the Invites tab, Spoiler Channel, Announcement Channel and Hide After Inactivity.
  - Server Settings sections Exo lacks: Server Tag, Boost Perks, Soundboard, Access, Community, Template and Delete Server.
- **Notifications:**
  - Text for files, stickers, voice messages, polls and embeds.
  - Role and channel mentions resolved.
  - "Author (#channel, Category)" titles.
  - The badge keeps its count when the unread badge is off, and counts friend requests.
  - Calls ring until answered (needs host).
- **Voice and video UI** (the voice session owns `Voice/`):
  - Call view: Focus This Person, Show Non-Video Participants and Show My Own Camera.
  - Stream right-click menu.
  - Server-mute vs self-mute icons (needs host).
  - Per-person Mute Soundboard and Ring (needs host).
  - Soundboard search and preview.
  - The user limit badge on voice channels (needs host).
  - Set Status from the user panel; durations and custom status need host.

### P3: large, or a decision first

- **Rich composer:** live markdown, mention pills, # channel and @role / @everyone autocomplete, and a text area menu with spellcheck.
- **Message Requests and the Spam inbox** (needs host).
- **Threads listed under their channel** with the thread menu.
- **Stage channels, and Activities with the App Launcher.**
- **Bookmarks and reminders** (the Inbox's Bookmarks, Reminders and Scheduled tabs).
- **Report Message and Report User Profile:** a policy surface.
- **Media viewer:** carousel, zoom and download.
- **YouTube and Spotify players inline:** this has PRIVACY.md implications.
- **Code highlighting:** a dependency decision.
- **Upload limits above 25 MB:** stream from disk or use Discord's presigned uploads.
- **Moderation suite:** Member Safety, Mod View, raid protection, Security Actions, report to the mod channel, bulk ban.
- **Server settings suite:** the Community sections (Onboarding, Insights, Welcome Screen, Access) and the Roles parity items (Role Style, Role Icon, Linked Roles, View Server As Role).
- **Multi-stream watching, Pop Out windows, and quality changes while live.**
- **Go Live quality:** whether to keep Nitro-free 1440p, Source and 60 fps, which Discord gates behind Nitro or server boosts.
- **Switch Accounts:** multi-account token storage, which falls under the secrets hard stop.
- **Discord's settings tree as a test fixture,** so bundle updates show renames. The parsed tree has 482 nodes.

## The area audits

Each area below is a table of features with Status (Match, Different, Missing, Fixed), the Discord citation, the Exo citation and notes, then that area's safe fixes and proposals. A **Done** or **Partly done** mark on a fix names the commit.

## Area: servers and channels, DMs and group DMs

Exo's Discord room compared with Discord's current web client, read from the downloaded bundle. Discord labels were dehashed as described in How this was done.

- **Discord citations** name the bundle file and either the webpack module id (`mod N`) or a byte offset (`@N`) in that file.
- **Exo citations** are `path:line` from the repo root.
- **How Exo was checked.** Every Exo claim was checked by reading the component and the host handler. A label search alone was never used as proof.
- **No right-click menus in some places.** Exo's main WebView turns off the browser's own context menus (`ExoLauncher/MainWindow.xaml.cs:614`). Only elements with their own `onContextMenu`, or a `data-dc-user` attribute that `UserMenuHost` picks up, get a menu. A right-click on a server icon, a folder, a category or a group DM therefore does nothing.

### Server rail

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Home (DMs) button with the summed DM mention badge | Match | web.d41b6bfa9f8fcb8b.js @5617252 (guild list) | ui/src/components/discord/Sidebar.tsx:295-297 | |
| Unread DMs shown as avatars under Home | Missing | web.d41b6bfa9f8fcb8b.js @5617252 `t_()` uses `getUnreadPrivateChannelIds()` | Sidebar.tsx:295-298 (only the Home badge) | Discord stacks each unread DM (avatar plus count) above the server list. Exo already has `home.dms[].unread/mentions`. |
| Server icon or initials, hover tooltip with who is in voice or streaming | Match | guild list @5617252 | Sidebar.tsx:270-293, 325-343 | Exo reads voice members from the cached channel list. |
| Unread pill (short) and selected pill (tall) | Match | guild list item @5617252 | ui/src/styles/discord.css:82-99, 4824-4831 | Exo has no hover pill state. That is Exo's look, not a gap. |
| Mention badge on a server (99+ cap) | Match | guild list @5617252 | Sidebar.tsx:361 | |
| Muted server: no unread pill, mentions still counted | Match | 507238 (mute state), guild unread ignores muted | ExoLauncher/Services/DiscordClient/DiscordCache.cs:565-567 | Exo also dims muted servers to 0.6 opacity (discord.css:101). That is Exo's look. |
| Folders: collapsed 2x2 grid, click to expand or collapse, color | Match | guild list @5617252 (`getExpandedFolders`) | Sidebar.tsx:299-318; DiscordCache.cs:571-572 | |
| Unnamed folder label | Different | 36412 `M()`: folder name, else server names joined with ", ..." truncation, else "Server Folder" | Sidebar.tsx:309 `folder.name ?? 'Server folder'` | |
| Expanded folders remembered | Different | ExpandedGuildFolderStore (`getExpandedFolders`, persisted) | Sidebar.tsx:262 (component state only) | Expanded folders reset on every remount or reconnect. |
| Folder shows the selected pill when the open server is inside it while collapsed | Different | guild list folder item | Sidebar.tsx:309 `current={false}` | |
| Folder unread pill and summed mentions while collapsed | Match | folder item | Sidebar.tsx:309-310 | |
| Folder context menu (Mark Folder As Read, Move to, Folder Settings, Close All Folders) | Missing | 07903701be2ab43d.js mod 595139 (navId guild-context) | none; RailButton has no onContextMenu (Sidebar.tsx:345-364) | |
| Server context menu (navId guild-context): whole menu | Missing | 11c3e58c7aa81faa.js mod 237600 | GuildButton/RailButton, Sidebar.tsx:336-364: no onContextMenu | The rows below list each item. |
| - Mark As Read (disabled when nothing unread, ignoring muted) | Missing (rail) | 237600 `mark-guild-read` | Only in the header menu: Sidebar.tsx:181 | `host.discordMarkRead` exists (host.ts:1449). |
| - Invite to Server (opens the Invite modal) | Missing (rail) | 531335 `invite-people`, label "Invite to Server" for GUILD_CONTEXT_MENU | Header icon only: Sidebar.tsx:174-177 | |
| - Mute Server / Unmute Server with For 15 Minutes ... Until I turn it back on | Missing | 7d2fc329ba0d1bc7.js mod 507238; durations 995102 `g()` | none | Exo reads server mute (DiscordCache.cs:565) but cannot set it. No RPC. |
| - Notification Settings submenu (All Messages, Only @mentions, Nothing, Suppress @everyone and @here, Suppress All Role @mentions, Suppress Highlights, Mobile Push Notifications) | Missing | 237600 `guild-notifications` | none (ServerSettings.tsx:275 is the server *default* for admins) | |
| - Hide Muted Channels (checkbox) | Missing | 5890c49543f04426.js mod 153594 | none | |
| - Show All Channels (checkbox, COMMUNITY servers) | Missing (rail) | b0c89a63e8d8388e.js mod 36942 | Header only: Sidebar.tsx:185-190 | |
| - Server Settings submenu (each settings section) | Missing (rail) | 237600 `guild-settings` | Header only: Sidebar.tsx:182-184 | |
| - Privacy Settings (per-server DMs and Message requests) | Missing | 237600 `privacy`; modal 10206a38d2798340.js mod 382573 | none per server; account-wide only in settings/PrivacyPages.tsx:58-59 | |
| - Edit Per-server Profile | Missing | 4e8640ad7cc21741.js mod 346247 `change-nickname` | none in menus; Discord settings > Profiles > Server Profiles (settings/AccountPages.tsx:223-264) | |
| - Create Channel / Create Category / Create Event | Missing (rail) | 911612, 359047 | none on rail | |
| - Move to (folder, First/Last in List) | Missing | 237600 `move-to` | none | |
| - Leave Server (hidden for the owner) | Missing (rail) | 237600 `leave-guild` (`!er` = not owner) | Header only: Sidebar.tsx:191 | |
| - Copy Server ID (Developer Mode) | Missing (rail) | bf6c6c7e3a9f46db.js mod 810263 | Header only: Sidebar.tsx:192 | |
| Add a Server | Different | web.d41b6bfa9f8fcb8b.js @5623232 `openCreateGuildModal` (Create My Own, templates, Join a Server); its right-click: fefa4464832ac5d1.js mod 4571 (Join a server, Create a server) | ui/src/components/discord/extras.tsx:132-166 (invite link only) | Exo cannot create a server. |
| Discover button | Missing | web.d41b6bfa9f8fcb8b.js @5625020 `guild-discover-button` | none | |
| Drag to reorder servers or make folders | Missing | GuildsTree `GUILD_MOVE_BY_ID`, `GUILD_FOLDER_CREATE_LOCAL` | none | |

#### Safe fix candidates

- **Unread DMs on the rail.** File: `ui/src/components/discord/Sidebar.tsx` (Rail, lines 295-298). After the Home button, render each `home.dms` entry with `unread`, newest first, as a RailButton showing the avatar and its mention count. Clicking it calls `onGo('@me')` and then picks that DM. Discord: web @5617252 `t_()`.
- **Folder label.** File: `Sidebar.tsx:309`. When `folder.name` is empty, join the member servers' names the way mod 36412 `l()` does, and fall back to "Server Folder".
- **Folder memory and pill.** File: `Sidebar.tsx:262`. Keep `openFolders` in `localStorage`, in try/catch the way `exo.discord.where` is kept. Pass `current={!expanded && members.some(g => g.id === place)}` at line 309.
- **Folder context menu.** File: `Sidebar.tsx` (new component next to Rail).
  - Mark Folder As Read: call `host.discordMarkRead(id)` for each member server. Disable it when no member server is unread.
  - Close All Folders: clear `openFolders`.
  - Discord: mod 595139.
- **Server context menu on the rail.** File: `Sidebar.tsx`. Move the ServerHeader menu body (lines 179-194) into a shared `GuildMenuItems` and open it from `onContextMenu` on GuildButton.
  - The menu needs the server's list. Call `host.discordChannels(guildId)` (or use `shownLists`) to get `moderation`, `rows` and the first readable text channel for the invite.
  - Items that need only existing RPCs:
    - Mark As Read (`discordMarkRead`)
    - Invite to Server (`discordChannel(firstText,'invite')`)
    - Show All Channels (`discordServer('showAll')`)
    - Server Settings
    - Create Channel (`CreateChannel` with `parentId=null`)
    - Leave Server (`discordServer('leave')`)
    - Copy Server ID
  - Discord: mod 237600.

**Needs host:**

- **Mute Server and Notification Settings.** Add `discord.server` actions `mute` (with `minutes`) and `notify` (`message_notifications`, `suppress_everyone`, `suppress_roles`, `notify_highlights`, `mobile_push`). Each is a `PATCH users/@me/guilds/{id}/settings`, the same call as `DiscordRest.MuteChannelAsync` (ExoLauncher/Services/DiscordClient/DiscordRest.cs:405) without `channel_overrides`.
- **Hide Muted Channels.** `hide_muted_channels` in the same PATCH. Expose it in `discord.channels`.
- **Move to folder, Folder Settings.** Write `guild_folders` in settings-proto 1.

#### Proposals (large/risky)

- **Add a Server parity.** Create My Own, templates, and the Join tab. Needs `POST /guilds` and icon upload.
- **Discover.** Discord's whole global-discovery surface.
- **Drag and drop on the rail.** Reorder servers, create folders, Folder Settings for name and color, persisted to settings-proto.

### Server header dropdown (navId guild-header-popout)

Discord: 30ec2386284cca6d.js mod 342218 (normal server menu).

Exo: `ServerHeader`, ui/src/components/discord/Sidebar.tsx:141-204.

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Server Boost | Missing | 342218 `premium-subscribe` | none | Low priority (purchase flow). |
| Use Server Theme, Server Tag | Missing | 342218 `use-server-theme`, `guild-tag` (conditional) | none | Low priority. |
| Invite to Server | Different | 531335 with source GUILD_HEADER: "Invite to Server" opens the Invite modal | Sidebar.tsx:174-178, DiscordClient.tsx:254 | Exo has an icon that silently copies a 7-day invite **for the open channel**. With Server Guide, Browse Channels or Members open, `open` is `page:*`. `DiscordRest.Id()` (DiscordRest.cs:527) then throws, so nothing is copied and nothing is said. |
| Server Settings (canAccessGuildSettings) | Match | 342218 `settings` | Sidebar.tsx:182-184 | Exo shows it when any moderation flag is set. Close enough. |
| Server Insights (VIEW_GUILD_ANALYTICS + COMMUNITY) | Missing | 342218 `insights` | none | Low priority. |
| Create Channel (MANAGE_CHANNELS) | Missing (menu) | 342218 `create-channel` | Only the category "+" (Sidebar.tsx:396) and Browse "+ Create" (serverPages.tsx:222) | |
| Create Category (MANAGE_CHANNELS) | Missing | 342218 `create-category` | none; host only makes types 0 and 2 (WebHostBridge.Discord.cs:635-636) | |
| Create Event | Missing (menu) | 359047 `create-event` | Only inside the Events dialog: events.tsx:28 | |
| Active Threads (server has active threads) | Missing | 342218 `active-threads` | none server-wide; per channel: ChatHeader.tsx:47-51 | |
| App Directory, Linked Roles, Friends in Server | Missing | 342218 `application-directory`, 293260 `guild-connection-roles`, `friends` (experiment) | none | Low priority. |
| Show All Channels (checkbox) | Fixed | 36942: shown for **COMMUNITY** servers | Sidebar.tsx:185 shows it only when `rows.browse` (GUILD_ONBOARDING, DiscordCache.cs:664) | Fixed (db49141): shown for community servers. A community server without onboarding has no toggle in Exo. The host's opt-in mode now requires COMMUNITY (DiscordCache `OptInMode`). |
| Notification Settings (server) | Missing | 342218 `notifications` opens modal 585617 | none | |
| Privacy Settings (per server) | Missing | 342218 `privacy` opens modal 382573 ("Allow DMs from other members in this server", "Message requests") | none per server | |
| Edit Per-server Profile | Missing | 346247 (label "Edit Per-server Profile" for yourself) | none in the menu | Exo has Server Profiles in Discord settings (AccountPages.tsx:223), which opens on `guilds[0]` (AccountPages.tsx:234). |
| Hide Muted Channels | Missing | 342218 `hide-muted-channels` | none | |
| Security Actions, Report Raid | Missing | 5890c49543f04426/bf6c6c7e3a9f46db.js mod 664967; 342218 `report-raid` | none | Moderators only. Low priority. |
| Leave Server | Fixed | 342218 `leave` shown only when `!ej` (not the owner); confirm modal 20508 | Sidebar.tsx:191 shows it to owners too | Fixed (db49141): hidden for the owner. Discord refuses an owner leaving. Exo's confirm wording otherwise matches (Sidebar.tsx:196-200). |
| Copy Server ID (Developer Mode) | Match | 810263 | Sidebar.tsx:192 | |
| Mark As Read in the header menu | Different | Not in guild-header-popout. It lives in guild-context, 237600. | Sidebar.tsx:181 | An Exo extra. It is never disabled; Discord disables its own when nothing is unread. |

#### Safe fix candidates

- **Done (db49141).** **Leave Server for owners.** Hide it when `list.moderation?.owner`. File: `Sidebar.tsx:191`. Discord: 342218 `ej?null:leave`.
- **Done (db49141).** **Show All Channels condition.** Gate it on `list.rows?.guide` (community) instead of `rows.browse`. File: `Sidebar.tsx:185`. Discord: 36942.
- **Invite with a server page open.** When `open` starts with `page:` or is not a text, forum or voice channel, use the first readable text channel, as `GuideInvite` does (serverPages.tsx:173). File: `ui/src/components/discord/DiscordClient.tsx:254`. Better still, label the item "Invite to Server" and show the "Invite link copied" note even when the header menu is closed.
- **Create Channel item.** Open `CreateChannel` with `parentId=null` and `category=name`. Files: `Sidebar.tsx`, `ui/src/components/discord/serverPages.tsx:498`.
- **Create Event item.** Open `EventsDialog` straight into its editor. Add an `initial="new"` prop in `ui/src/components/discord/events.tsx:14-28`.
- **Edit Per-server Profile.** Call `requestDiscordSettings('profiles')` with this server preselected. Files: `ui/src/lib/discord.ts:332` (carry a guildId), `settings/AccountPages.tsx:234`.
- **Privacy Settings dialog.** Two switches that add or remove this server id in `restrictedGuildIds` and `messageRequestRestrictedGuildIds` through `changeAccountSettings` (ui/src/lib/discordPrefs.ts:183), the existing `discord.userSettings` RPC. Discord: 382573.
- **Mark As Read state.** Disable it when no channel in `list.channels` is `unread` and none has `mentions`.

**Needs host:**

- Create Category: let `createChannel` take `kind: 'category'`, type 4 (WebHostBridge.Discord.cs:635).
- Notification Settings modal and Hide Muted Channels (see Server rail).
- Active Threads needs the joined active threads per server.

#### Proposals (large/risky)

- **Invite modal parity** (531335 opens 405342): a friends list with Send, a link with Copy, and Edit invite link for expiry and max uses.
- **Server Notification Settings modal** (585617): per-server level, suppress options, and channel overrides.

### Channel list

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Rows above channels: Server Guide | Match | 3e54ccc0d7ab05f8.js `rj()` pushes GUILD_HOME when home is enabled; `ir()` "Server Guide" | ui/src/components/discord/serverPages.tsx:24-57; DiscordCache.cs:662 (community) | |
| Events row ("Events" or "N Events") | Different | 3e54ccc0d7ab05f8.js `rj()`: shown when 770666 (CREATE_EVENTS or admin) or events exist | DiscordCache.cs:663 `community or ManageEvents` | Exo shows an empty Events row in every community server; Discord hides it there for people who cannot create events. |
| Browse Channels / Channels & Roles row | Different | 3e54ccc0d7ab05f8.js `il()`: shown for COMMUNITY. Label "Channels & Roles" when 202776 (GUILD_ONBOARDING_HAS_PROMPTS), else "Browse Channels". | serverPages.tsx:20 always "Browse Channels"; shown only with GUILD_ONBOARDING (DiscordCache.cs:664) | Exo has no onboarding Customize page. |
| Members row (moderators, COMMUNITY) | Match | `rj()` GUILD_MOD_DASH_MEMBER_SAFETY | DiscordCache.cs:665; serverPages.tsx:337 | |
| Categories collapse, remembered on the account | Match | 6d55a8fa065a5565.js mod 740438 `collapse-category` | Sidebar.tsx:60-66; WebHostBridge.Discord.cs:858; DiscordRest.cs:259 | |
| Collapsed category still shows its unread unmuted channels and the open one | Match | channel list (collapsed filter) | Sidebar.tsx:400 | |
| Category "+" Create Channel (MANAGE_CHANNELS) | Match | 3e54ccc0d7ab05f8.js "Create Channel" | Sidebar.tsx:396 | |
| Category context menu: Mark As Read, Add to/Remove from Channel List, Collapse Category, Collapse All Categories, Mute Category (durations), Notification Settings, Move to, Edit Category, Delete Category, Copy Category ID | Missing | 6d55a8fa065a5565.js mod 740438 (navId channel-context) | Sidebar.tsx:391-397 (no onContextMenu on the category) | |
| Channel row hover buttons: Invite to Channel / Invite to Voice, Edit Channel (gear) | Missing | 3e54ccc0d7ab05f8.js @31010 `nV()` invite icon, `nw()` Edit Channel gear | Sidebar.tsx:404-415 | |
| Unread: bold name and pill at the edge; mention badge | Match | channel item | Sidebar.tsx:405, 413; discord.css:2861-2873 | |
| Muted channel dimmed, no unread | Match | channel item | Sidebar.tsx:405; discord.css:230; DiscordCache.cs `MutedChannel` | |
| Icons: text, locked text, announcement, rules, forum, locked voice | Match | channel icons | Sidebar.tsx:367-373; DiscordCache.cs:596-617 | |
| Stage channel (type 13) | Different | Stage icon; speakers, audience and Request to Speak (web.d41b6bfa9f8fcb8b.js @3317142 "Request to Speak"; `end-stage` in 119357) | Treated as plain voice: DiscordCache.cs `IsVoice`; Sidebar.tsx:368 voice icon | No speaker or audience handling. |
| Media channel (type 16) | Different | Media grid view (GUILD_MEDIA) | Shown as forum (DiscordCache.cs:596) with the forum icon and ForumView list (ui/src/components/discord/Forum.tsx:16) | |
| Forum channel view | Different | Sort & View (Recently Active, Date Posted; List, Gallery) 394da24c8ee8e712.js @108461; Post Guidelines | Forum.tsx:16-60 (search, tags, New Post, post cards) | Sort & View and guidelines are missing. |
| Opt-in mode lists followed and private channels only | Match | 969128 / opt-in stores | Sidebar.tsx:521-526 | |
| Active threads under their channel ("More Active Threads") | Missing | 3e54ccc0d7ab05f8.js strings "More Active Threads", "{channelName} (thread)"; 406704 `D()` active joined threads for a parent | none; `list.threads` (DiscordCache.cs `threads`) is used only to resolve jumps | |
| New Unreads / New Mentions jump bars | Missing | 3e54ccc0d7ab05f8.js "New Unreads", "New Mentions" | none | |
| Pinned Channels section (Pin Channel to Top) | Missing | 3e54ccc0d7ab05f8.js "Pinned Channels"; 0d4780a2331e78ed.js mod 704543 | none | |
| Voice channel: click joins; connected users listed with a speaking ring | Match | 119357; voice user row web.d41b6bfa9f8fcb8b.js @3285515 | Sidebar.tsx:420-432; ui/src/components/discord/voice.tsx:82-98 | |
| Voice user icons: muted, deafened | Match | @3285515 | voice.tsx:92 | Discord colours server mute and deafen differently; Exo's data has no self vs server distinction. |
| Voice user icons: camera (video) and LIVE (streaming) | Missing | @3285515 pushes `stream` badge "Live", video icon, "Watching Stream" | voice.tsx:89-93 ignores `member.video` and `member.stream` (sent by DiscordCache.cs:624-626) | |
| Voice user limit and "This channel is full." | Missing | 3e54ccc0d7ab05f8.js "This channel is full." | `UserLimit` is read (DiscordCache.cs:1589) but never sent in `Channels()` | |
| Open a voice channel's chat without joining (Open Chat) | Missing | 119357 `open-chat`; 3e54ccc0d7ab05f8.js "Open Chat" row button | DiscordClient.tsx:251 (clicking always joins) | |
| Voice channel status (Set Channel Status, shown under the name) | Missing | 671483 `set-status`/`clear-status`; "Set a channel status" | none | |
| Announcement channel Follow (into one of your servers) | Missing | e135f1d825d5d1d5.js @165872 `renderFollowButton`; API `CHANNEL_FOLLOWERS` web @269235 | none (ChatHeader.tsx has no Follow) | |
| Rules channel icon | Match | rules channel icon | Sidebar.tsx:369; DiscordCache.cs `rules` | |
| Events dialog (Interested, Create Event for managers) | Match | 359047 / events modal | events.tsx:14-28 | |
| Browse Channels page (search, Follow Category, per-channel checkbox) | Match | channel browser | serverPages.tsx:204-250 | |
| Drag to reorder channels and categories | Missing | channel list DnD (MANAGE_CHANNELS) | none | |

#### Safe fix candidates

- **Voice user icons.** In `VoiceMembers` render a camera icon for `member.video` and a "LIVE" chip for `member.stream`; the data is already sent. Files: `ui/src/components/discord/voice.tsx:89-93`, plus a small rule in `ui/src/styles/discord.css`. Discord: @3285515.
- **Hover buttons on channel rows.**
  - Invite icon: `host.discordChannel(id,'invite')`, then copy and say "Invite link copied".
  - Edit Channel gear when `c.canEdit`: open `ChannelSettings`.
  - Files: `Sidebar.tsx` ChannelList (404-415), passing `onEdit` like ChannelMenu does (lines 69-70). Discord: 3e54ccc0d7ab05f8.js `nV()`, `nw()`.
- **Category context menu.** File: `Sidebar.tsx`; the category button gets `onContextMenu`. Items and the existing RPCs they use:
  - Mark As Read: `discordChannel(child,'read')` for each unread child.
  - Collapse Category: the existing `onToggle`.
  - Collapse All Categories: `discordChannel(cat,'collapse',true)` for each category.
  - Mute Category with durations, and Notification Settings: `host.discordChannelNotify(categoryId, …)`. The host mute and notify cases accept any server channel id (WebHostBridge.Discord.cs:847-857).
  - Edit Category: `ChannelSettings`.
  - Delete Category: `discordChannel(id,'delete')`, labelled "Delete Category".
  - Copy Category ID.
  - Discord: mod 740438.
- **Stage and media icons.** Add a Stage icon for `type===13` and a media icon for `type===16`. Files: `Sidebar.tsx:367-373`, `ui/src/components/discord/icons.tsx`, `ChatHeader.tsx:254`.
- **Events row condition.** Show it only when `list.canManageEvents || list.events > 0`. File: `serverPages.tsx:34`, using `ServerRows` props. Discord: `rj()` with 770666.
- **Open Chat without joining.** Clicking a voice channel's name opens its chat; a separate Join (or a double click, as Discord does) joins. File: `DiscordClient.tsx:251`. Discord: 119357 `open-chat`.

**Needs host:**

- **User limit.** Send `userLimit` in `DiscordCache.Channels()` (DiscordCache.cs:578-627). Then show "n/limit" and disable Join when full.
- **Channels & Roles.** A `onboardingPrompts` flag (GUILD_ONBOARDING_HAS_PROMPTS) and a COMMUNITY-based `rows.browse`.
- **Active joined threads.** Joined state per thread, so the threads under each channel can be listed.

#### Proposals (large/risky)

- **Threads in the channel list**, with the thread-context menu (next section).
- **Stage channels**: speakers and audience, Request to Speak, End Stage.
- **Media channel grid and forum Sort & View.**
- **Announcement Follow**: pick a server and channel, `POST channels/{id}/followers`.
- **Pinned channels, Favorites, and drag and drop** of channels and categories.
- **Voice channel status and Join Without Entrance Sound.**

### Channel context menu (navId channel-context)

Discord's text channel menu is 3dea0b7068d1c842.js mod 468916 (`F`). Its voice variant is 21b4a7fc73e1a9cc.js mod 119357 (`K`).

Exo's menu is `ChannelMenu`, ui/src/components/discord/Sidebar.tsx:467-517. Exo opens the same menu for text and voice channels; categories get none.

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Mark As Read | Fixed | ddfc3f4d3bb8c7fd.js mod 868548 `mark-channel-read`, `disabled:!unread` | Sidebar.tsx:500 | Fixed (497ec0f) for the channel menu: off when nothing is unread or mentioned. Exo is always enabled. |
| Favorite (Favorites server) | Missing | b8312ce1eaacc8bf.js mod 438653 `favorite-channel` | none | Low priority. |
| Invite to Channel / Invite to Voice (when the user may invite) | Different | 531335 via 475777; label from `D()` | Sidebar.tsx:501-504 "Invite People" | Partly fixed (497ec0f): Invite People now says the link was copied (Streamer Mode hides it). The invite modal and the permission check are still missing. Exo copies a link silently: no modal, no confirmation, and it shows even without invite permission. |
| Pin Channel to Top / Unpin Channel | Missing | 704543 `pin-channel` | none | |
| Add to Channel List / Remove from Channel List / Unfollow Channel Category (opt-in mode) | Missing | 70f206add2796fda.js mod 969128 | none in the menu; Browse page only (serverPages.tsx:214, 231, 242) | |
| Copy Link | Match | 67ca00608f48791f.js mod 886393 | Sidebar.tsx:505 | |
| Mute Channel with For 15 Minutes / 1 Hour / 3 Hours / 8 Hours / 24 Hours / Until I turn it back on; Unmute Channel with "Muted until" subtext | Fixed | 58c5cd79a16c8073.js mod 995102 | Sidebar.tsx:506 is a plain toggle | Fixed (497ec0f). The durations already exist in the header bell (ChatHeader.tsx:199, 225-229) and the host takes `minutes` (WebHostBridge.Discord.cs:847-853). |
| Notification Settings submenu (Use Category/Server Default with subtext, All Messages, Only @mentions, Nothing) | Fixed | 037809489270256b.js mod 288104 | Header bell only: ChatHeader.tsx:231-240 | Fixed (497ec0f): Use Category Default or Use Server Default with the inherited level, All Messages, Only @mentions, Nothing; not on voice channels. The forum "New Posts Created" switch is still missing. The bell says "Use Default"; Discord says "Use Category Default" or "Use Server Default". |
| Edit Channel | Match | e27f2b658da77efd.js mod 317910 | Sidebar.tsx:507 (`canEdit` = ManageChannels or ManageRoles, DiscordCache.cs `canEdit`) | Discord also allows it with Manage Webhooks alone. |
| Duplicate Channel | Missing | aad4149354684b58.js mod 533957 `clone-channel` | none | |
| Create Text Channel / Create Voice Channel (in this channel's category) | Missing | 061aebfad16c398b.js mod 477190 | none in the menu | `CreateChannel` exists (serverPages.tsx:498). |
| Delete Channel | Different | 2b5b380ed1fc8d59.js mod 307623: needs MANAGE_CHANNELS **on the channel**; label Delete Category / Thread / Post | Sidebar.tsx:508-510 uses server-wide `list.canManage` | A channel with Manage Channels denied by an overwrite still shows Delete in Exo, and Discord answers 403. |
| Move to (category, First/Last) | Missing | 173522 `move-to` | none | |
| Copy Channel ID (Developer Mode) | Match | 50268 | Sidebar.tsx:513 | |
| Voice only: Hide Names | Missing | 119357 `hide-voice-names` | none | |
| Voice only: Open Chat | Missing | 119357 `open-chat` | none | |
| Voice only: Set Channel Status, Join Without Entrance Sound | Missing | 671483, 217563 | none | |
| Voice: notification items | Match | 288104 returns null for GUILD_VOICE | n/a | |

#### Safe fix candidates

- **Done (497ec0f).** **Mute durations.** Turn Mute Channel into a submenu that uses `MUTE_FOR` (export it from `ChatHeader.tsx:199`) and calls `host.discordChannelNotify(id, {muted:true, minutes})`. Unmute shows "Muted until" from `channel.mutedUntil`. Reuse the `Submenu` pattern from `UserMenu.tsx:248-276`. File: `Sidebar.tsx:506`. Discord: mod 995102.
- **Done (497ec0f).** **Notification Settings submenu.** Items: Use Category Default or Use Server Default (subtext from `notifyDefault`), All Messages, Only @mentions, Nothing, each through `host.discordChannelNotify(id,{level})`. Hide it for voice channels. File: `Sidebar.tsx` ChannelMenu. Discord: mod 288104.
- **Done (497ec0f).** **Mark As Read state.** Disable it when `!channel.unread && !channel.mentions`. File: `Sidebar.tsx:500`.
- **Partly done (497ec0f): it says the link was copied; the label is still "Invite People".** **Invite.** Label it "Invite to Channel" or, for `kind==='voice'`, "Invite to Voice". Show "Invite link copied" (with Streamer Mode's hide rule, as at Sidebar.tsx:178) instead of closing silently. File: `Sidebar.tsx:501-504`.
- **Opt-in items.** When `list.optIn`, add Add to Channel List / Remove from Channel List, or Unfollow Channel Category when the category is followed, through `host.discordChannel(id,'optin',bool)`. File: `Sidebar.tsx` (pass `list.optIn` to ChannelMenu). Discord: mod 969128.
- **Create Text / Create Voice Channel.** Open `CreateChannel` with the channel's category: `parentId` from the preceding category row. Add an `initialKind` prop. Files: `Sidebar.tsx`, `serverPages.tsx:498-505`. Discord: mod 477190.

**Needs host:**

- Delete Channel per-channel permission: send a `canManage` per channel (`Perms(c) & ManageChannels`) from `DiscordCache.Channels()`.
- Duplicate Channel: create with the clone's type, topic, overwrites and parent.
- Move to: `PATCH guilds/{id}/channels` positions.
- Pin Channel to Top and Favorite.
- Hide Names (local) and Set Channel Status: `PUT channels/{id}/voice-status`.

### Thread context menu (navId thread-context)

Discord: f24b6dfcba78da88.js mod 612826 (desktop `Z`).

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Whole thread menu: Mark As Read, Favorite, Open in New Tab, Leave Thread / Join Thread, Start Call, Close Thread / Open Thread, Lock Thread, Edit Thread, Edit Tags, Copy Link, Mute Thread, Notification Settings, Anyone can invite, Pin Post, Summarize, Delete Thread, Copy Thread ID | Missing | 612826; labels from 660273, 40389, 182061, 707792, 200273, 565846, 57907, 375500, 707653, 41402, 307623 | none. Threads are not in the sidebar, and the header's Threads popover rows (ChatHeader.tsx:187-193) have no menu. | |
| Thread browser (Active / Archived) in the channel header | Match | e135f1d825d5d1d5.js header Threads | ChatHeader.tsx:167-197 | |

#### Safe fix candidates

- **A small thread menu** on the header Threads popover rows and forum post cards: Copy Link (`https://discord.com/channels/{guild}/{thread}`) and Copy Thread ID in Developer Mode. Files: `ChatHeader.tsx:187-193`, `Forum.tsx:104-122`.

**Needs host:** Join/Leave, Close/Open, Lock, Edit, Mute and Notification Settings for threads.

#### Proposals (large/risky)

- **Full thread support in the sidebar**: joined active threads under their parent, unread and mentions per thread, and the full thread-context menu.

### Direct messages: list and rows

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Sorting: newest message first, live | Different | web.d41b6bfa9f8fcb8b.js mod 645959 (PrivateChannelSortStore): pinned ("FAVORITE") then the rest by `lastMessageId`; every MESSAGE_CREATE re-sorts, your own too | DiscordCache.cs:538 sorts on fetch; DiscordClient.tsx:307-311 `sortDms` moves a DM up only on an **unread** event | A message you send in an older DM does not move it to the top until the next `discord.home` refresh. |
| Message requests and spam left out of the DM list | Different | 645959 `isRequest` (isMessageRequest or isSpam) gets no bucket, so it is not listed | DiscordCache.cs:533-575 `Home()` lists every private channel; `MessageRequest` is read (DiscordCache.cs:1591) and used only to silence notifications | Message requests appear as ordinary DMs. |
| Message Requests row and page (Requests and Spam tabs; accept, ignore) | Missing | web.d41b6bfa9f8fcb8b.js @1641235 `LH()` row; 07d31a985888dc7b.js page (Requests and Spam tabs, "Clear All") | none (Sidebar.tsx:99-108) | |
| Close DM (x on hover) | Match | web.d41b6bfa9f8fcb8b.js @773836 "Close DM" | Sidebar.tsx:117, 129-130; DiscordRest.cs:192 | |
| Leave Group (x on a group) with confirmation | Different | @773836 `e8`; modal 925785 has the checkbox "Leave without notifying other members" (`closePrivateChannel(id, selected, silent)`) | Sidebar.tsx:131-135; DiscordRest.cs:192 (no `silent`) | |
| Closing or leaving the open DM moves you to Friends | Different | `closePrivateChannel(t.id, selected)` | Sidebar.tsx:117 (no navigation) | Exo is left on "Pick a conversation." (DiscordClient.tsx:313-315). |
| Unread DM: bold, pill and mention badge | Match | @773836 (`ez` mention count; `highlighted`) | Sidebar.tsx:120, 127; discord.css:2861 | |
| Typing indicator on the DM avatar | Missing | @773836 `isTyping` passed to the avatar | none; `discord.typing` arrives (host.ts:870) but DmRow ignores it | |
| Subtext: activity (1:1) or "N Members" (group) | Match | @773836 `subText` | DiscordCache.cs:546; Sidebar.tsx:125 | |
| Muted DM or group dimmed (also ignored or blocked users) | Missing | @773836 `e$ = muted or ignored or blocked` | `DiscordDm` has no `muted` (ui/src/lib/discord.ts:14); the host knows DM mutes (DiscordCache.cs `ApplyGuildSettings` reads the @me overrides) | |
| Ongoing call icon on the row | Missing | @773836 `eq` call message, call icon | Sidebar.tsx:114-138 ignores `dm.call` (sent by DiscordCache.cs `call`) | |
| Group avatar without an icon: stacked recipients | Different | @773836 `ef.A recipients` | Avatar falls back to the first letter (presence.tsx) | |
| Group status dot (online if anyone is) | Missing | @773836 `isMultiUserDM` status | Sidebar.tsx:122 passes `null` for groups | Minor. |
| "Find or start a conversation" | Different | opens the Quick Switcher | Sidebar.tsx:96 filters the DM list in place | |
| Friends row; Direct Messages header with "+" Create DM | Match | DM list | Sidebar.tsx:99-106 | |
| Pin DM (pinned DMs first) | Missing | 1ae2a64dc57ce093.js mod 439191 `pin-dm`; 645959 FAVORITE bucket | none | |

#### Safe fix candidates

- **Live DM order.** Listen to `discord.message` with `kind==='create'` for a DM channel and move that DM to the top of `home.dms`, whoever sent it. File: `DiscordClient.tsx:272-311` (`useLiveLists`, `sortDms`). Discord: mod 645959.
- **Typing on the avatar.** Track `discord.typing` events per DM, clearing after about 10 s, and show Discord's typing dots on DmRow's avatar. Files: `Sidebar.tsx` (DmSidebar/DmRow), CSS in `discord.css`.
- **Call icon.** Show a call icon on the row when `dm.call?.length`. File: `Sidebar.tsx:114-128`.
- **Leaving the open conversation.** After Close DM or Leave Group on the current DM, `onPick(FRIENDS)`. File: `Sidebar.tsx:117`, passing `current`/`onPick` into the close handler.
- **Group status dot.** Show online when any recipient is online. This needs recipient presence, which Home does not send, so it is effectively **needs host**.

**Needs host:**

- **Message requests out of the DM list.** In `DiscordCache.Home()` (DiscordCache.cs:533-538), drop private channels with `MessageRequest`. Also read `is_spam` in `ReadChannel` (DiscordCache.cs:1572) and drop those.
- **`muted` and `mutedUntil` per DM** from `Home()`.
- **Silent leave.** Take `silent=true` in `CloseDmAsync` (DiscordRest.cs:192, `?silent=true`), exposed as a param on `discordChannel(id,'close')`.

#### Proposals (large/risky)

- **Message Requests page.** Requests and Spam tabs; accept, ignore, clear all; the requests count on the DM list row. Needs new host RPCs for accepting (consent) and spam actions. Discord: @1641235 and 07d31a985888dc7b.js.
- **Pin DM** to the top of the list.

### DM context menu (navId user-context, DM variant)

Discord: 931ac85955447285.js mod 385913 (desktop `V`).

Exo: a right-click on a 1:1 DM row hits `data-dc-user` (Sidebar.tsx:121) and opens the generic person menu, `UserMenu` (ui/src/components/discord/UserMenu.tsx:141-163). A group row has no menu.

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Mark As Read | Missing | 385913 `ip` (868548) | none; host `read` is server-only: DM channels go to `DiscordDmActionAsync` (WebHostBridge.Discord.cs:843, 705-718) | |
| Pin / Unpin (DM), Favorite | Missing | 385913 `iF` (439191), `iS` (438653) | none | |
| Profile | Match | 243949 `user-profile` | UserMenu.tsx:149 | |
| Start a Call (and Ring / Stop Ringing during a call) | Different | 385913 `ir` (373397), `ih` (313998) | none in the menu; the header call button (ChatHeader.tsx:42-46) | Partly fixed (6937c04): the person menu has Start a Call (opens the DM and rings). Ring / Stop Ringing one person is still missing. |
| Add Note / Edit Note | Missing | 385913 `il` (497600) | none in the menu; the note field in the profile (extras.tsx:97) | |
| Close DM | Missing (menu) | 385913 `iA` (d017fc153659f724.js mod 486524) | Only the row x (Sidebar.tsx:129) | |
| Invite to Server (submenu of your servers) | Missing | 385913 `ic` (889460) | none | |
| Add Friend / Remove Friend | Fixed | 385913 `ix` (803664) | none in the menu; Friends page only (FriendsPage.tsx:125-139) | Fixed (6937c04): Add Friend, Accept Friend Request, Friend Request Sent, Remove Friend (asked first). |
| Ignore, Block | Fixed | 399476 `ignore`; 239211 `block` | none | Fixed (6937c04): Ignore / Unignore and Block / Unblock with Discord's questions. |
| Apps | Match | 646911 `apps` | UserMenu.tsx:155 | |
| Mute @user (durations) | Missing | 995102: label "Mute {name}" for DMs | none; the host mute path builds `users/@me/guilds/{guild}/settings` and DMs are refused (WebHostBridge.Discord.cs:843; DiscordRest.cs:405) | |
| Copy User ID | Match | 50268 `Copy User ID` | UserMenu.tsx:159 | |
| Copy Channel ID | Missing | 385913 `iE` | none | |
| Mention / Message / Copy Username in the DM list menu | Different | Not in Discord's DM variant (385913). Mention and Message belong to the server user menu. | UserMenu.tsx:150-151, 158 | Exo shows its server-style person menu here. |

#### Safe fix candidates

- **DM variant of the person menu.** Put `data-dc-dm={dm.id}` on DmRow (Sidebar.tsx:120-121) and carry it through `UserMenuHost` (UserMenu.tsx:73-79). When it is set, show:
  - Close DM: `host.discordChannel(dmId,'close')`.
  - Copy Channel ID, in Developer Mode.
  - Start a Call: dispatch an event that `DiscordClient` maps to `voice.call(dmId)`.
  - Add Note: open the full profile, which has `NoteField`.
  - Remove Friend: `host.discordFriend('remove',{userId})`, with the existing confirm text from FriendsPage.tsx:116-118.

  Drop Mention and Message in that variant. Discord: mod 385913.

**Needs host:**

- **Mark As Read for DMs.** Let `case "read"` run before the server check (WebHostBridge.Discord.cs:843), or add it to `DiscordDmActionAsync`.
- **Mute @user and Mute Conversation.** A DM mute that PATCHes `users/@me/guilds/@me/settings`.
- **Add Friend by user id.** The host takes a username only (WebHostBridge.Discord.cs:298-301).
- **Block and Ignore.**
- **Invite to Server.** Could be composed from `discordChannel(invite)` plus `discordSend`, but Discord sends an invite message, so a dedicated action is cleaner.

### Group DMs (navId gdm-context, creation, members, owner actions)

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Group DM context menu: Mark As Read, Pin/Favorite, Invites (owner), Edit Group, Mute Conversation (durations), Leave Group, Copy Channel ID | Missing | 719a2e0b9b3abdd8.js mod 4027 (desktop `O`); Leave Group e8d968020053e511.js mod 605691; Edit Group 116c6ef6d13c2185.js mod 73028 | Group rows have no `data-dc-user` and no `onContextMenu` (Sidebar.tsx:120-121) | |
| Edit Group (name and icon; older "Change Icon") | Missing | 73028 opens the group edit modal; strings "Edit Group", "Group Name" | none; admin `channelSave` is server-only (WebHostBridge.DiscordAdmin.cs:313) | |
| Invites (owner): the group's invite links | Missing | 4027 `instant-invites` | none | |
| Create a group DM: Select Friends with multi-select checkboxes, then create | Different | 033931e609a1c7d0.js "Select Friends" with checkboxes | Sidebar.tsx:435-442 StartDm picks **one** friend and opens a 1:1 DM; FriendsPage.tsx:72 "New Group DM" opens the same 1:1 picker | Exo's "New Group DM" cannot make a group. |
| Add Friends to DM (header) | Different | Select Friends multi-select | ChatHeader.tsx:291-306 (one friend per pick; a 1:1 becomes a new group: DiscordRest.cs:196-198) | Adds one person at a time. |
| Group member list (header toggle; owner crown) | Missing | group DM member list ("Group Owner", "Owner") | ui/src/components/discord/Chat.tsx:722-727 `SidePane` returns null for groups; ChatHeader.tsx:281 no toggle for groups | |
| Remove From Group (owner) | Missing | string "Remove From Group" (user-context on a GDM member) | none | |
| Make Group Owner / Transfer Ownership | Missing | strings "Make Group Owner", "Transfer Ownership" | none | |
| Leave Group | Match | 605691 | Sidebar.tsx:129-135 | The silent checkbox is missing (see the DM list). |

#### Safe fix candidates

- **Group DM menu with what the host already supports.** A group row `onContextMenu` opening:
  - Leave Group: the existing Confirm with `discordChannel(id,'close')`.
  - Copy Channel ID, in Developer Mode.

  File: `Sidebar.tsx` DmRow (114-138). Discord: mod 4027.
- **"New Group DM" label.** Until multi-select exists, either label the Friends header button "Create DM" or open the picker in multi-select mode (needs host, below). File: `FriendsPage.tsx:72`.

**Needs host:**

- **Multi-recipient group creation.** `POST users/@me/channels` with N recipients. Today `OpenDmAsync` takes one recipient (DiscordRest.cs:200) and `AddToDmAsync` adds one (DiscordRest.cs:196).
- **Edit Group.** PATCH name and icon on the DM channel.
- **Remove From Group.** `DELETE channels/{id}/recipients/{user}`.
- **Make Group Owner.** PATCH `owner`.
- **Group invites.** List the group's invites.
- **Group member list data.** Recipients' names, avatars and status, plus `owner_id`. Home sends only recipient ids (DiscordCache.cs `recipients`).
- **Mark As Read and Mute Conversation for group DMs** (same as for 1:1 DMs).

#### Proposals (large/risky)

- **Full group DM management.** Member list pane with the owner crown, owner-only Remove From Group and Make Group Owner, the Edit Group modal (name and icon upload), the Invites list, and multi-select creation with the "You can add N more friends" limit.

## Area: messages, the composer and app commands

Scope: `ui/src/components/discord/{Message,Chat,Composer,MessageTools,Reactions,Attachments,markdown,EmojiPicker,GifPicker,Poll,PollCreator,SearchBar,Components,AppCommands,ImageMenu,VoiceMessage,LottieSticker,DeleteConfirm,ChatHeader,results,extras}.tsx`, `ui/src/lib/discord*.ts`, host `ExoLauncher/Services/WebHostBridge.Discord*.cs` and `Services/DiscordClient/DiscordRest*.cs`.
Discord reference: the downloaded web bundle, dehashed as described in How this was done. Bundle names are shortened to their hash; `m123456` is a webpack module id; `id:"x"` is a menu item id; quoted text is the en-US string.

Key Discord modules:
- Message context menu: `727ee9421e324cee.js m355682` (`nB`). It builds both `navId:"message"` (right-click) and `navId:"message-actions"` (the hover bar's More).
- Hover bar: `78f77b89ac557284.js` (`nm` buttons, `nh` quick reactions, `ng` failed-send bar, `tE` message keyboard handler).
- Item modules: pin `681e846fab87a0c8.js m979766`, mark-unread `m20883`, copy-link `m345254`, delete `m483768`, copy selection `m373963`, bookmark/reminder `8b980d157af44f08.js m880457`, link items `d5621fd9c10fcdb0.js m777933`, favorite `bb55059a200739ad.js m253925`, Search with Google `c8dac9511d99c3cd.js m826308`.

Status key: **Match**, **Different** (exists, but works or looks different), **Missing**.

### Message context menu and More menu (navId "message" / "message-actions")

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Right-click opens the message menu at the pointer | Match | m355682 `nk` navId "message" | Message.tsx:238, 569-577 (clicks the More button with the pointer position) | |
| Quick-react row at the top of the menu | Missing | m355682 `td`: up to 4 frecent emoji as `id:"quickreact-*"`, shown when no text is selected and you can react | Message.tsx:531-546 has no quick-react row | |
| Add Reaction submenu (12 frecent emoji, then "View More") | Different | m355682 `tA` `id:"add-reaction"`, `id:"other-reactions"` "View More" | Not in the menu (Message.tsx:533-545). Reacting only works from the hover-bar button | Partly fixed (0a486ae): the menu now opens with Add Reaction, which opens the reaction picker. The frecent row and "View More" are still missing. |
| View Reactions | Match | m355682 `id:"reactions"` "View Reactions" (when reactions exist; for a poll only when Gh) | Message.tsx:538 → ReactionsDialog (Reactions.tsx:118) | |
| Edit Message | Different | m355682 `id:"edit"`. Allowed by web m697470: own message, sent, not a poll, not a voice message, not a MEDIA_MENTION | Message.tsx:522, 533: own message of type 0/19 only | Exo also offers Edit on your own polls and voice messages, which Discord does not. |
| Reply | Different | m355682 `id:"reply"`. Action `$b` in 727ee9421e324cee.js: `shouldMention:!shiftKey&&!own`, `showMentionToggle:!isPrivate&&!own`. m517997: not on ephemeral or failed messages | Message.tsx:534. Chat.tsx:109 always pings unless the message is your own | Shift does not turn the ping off. A DM still shows the @ toggle. Exo offers Reply on ephemeral messages. |
| Forward | Different | m355682 `id:"forward"`. Allowed by `8cdede5f3b0ae93b.js m843626`: not polls, voice messages, forwards (HAS_SNAPSHOT), messages with a thread, crossposts, SUPPRESS_EMBEDS, failed sends, or servers with FORWARDING_DISABLED | Message.tsx:529, 535: always shown | Exo offers Forward on messages Discord refuses to forward. |
| Create Thread | Different | m355682 `id:"thread"`. `406704` `n`/`S` need CREATE_PUBLIC_THREADS and READ_MESSAGE_HISTORY | Message.tsx:537. Chat.tsx:111 `threadable` checks only the channel type | Exo shows it without the permission. Discord opens a thread side panel; Exo opens a name dialog (MessageTools.tsx:93-120). |
| Apps (message commands) | Match | m355682 `tS` = m646911 (commandType MESSAGE) | AppCommands.tsx:304-324, Message.tsx:536 | Exo puts Apps in the first group; Discord puts it in the utility group. |
| Copy Text | Match | m355682 `id:"copy-text"` (content, or the components' text for V2) | Message.tsx:540 | Exo copies `content` only. A Components V2 message's text is not copied. |
| Pin Message / Unpin Message | Fixed | m979766 `id:"pin"`/`id:"unpin"`. Shift pins at once; otherwise confirmPin/confirmUnpin ("Pin It. Pin It Good.", "Hey, just double checking…"). Needs PIN_MESSAGES (m492841) | Message.tsx:541. Chat.tsx:215-220 pins at once | Fixed (606408a): Pin and Unpin ask first with Discord's words ("Pin It. Pin It Good." / "Oh yeah. Pin it", "Unpin Message" / "Remove it please!"); Shift skips. Exo never asks for confirmation. |
| Mark Unread (and Alt+click) | Match | m20883 `id:"mark-unread"`. Tip `aes5VI` "…a message to mark it as unread." | Message.tsx:239 (Alt+click), 542. Chat.tsx:204-209 | |
| Copy Message Link | Match | m345254 `id:"copy-link"` | Message.tsx:519, 543 | |
| Copy Message ID (Developer Mode) | Match | m355682 `tq` m50268 "Copy Message ID" | Message.tsx:518, 545 | |
| Delete Message (Shift skips the confirm) | Match | m483768 `id:"delete"`: Shift deletes at once; otherwise confirmDelete. Tip `IxY7E6` | Message.tsx:544. Chat.tsx:221. DeleteConfirm.tsx:9-38 | Exo's tip is shorter: "Tip: hold Shift…". |
| Bookmark Message / Create Reminder ("Remind Me", Edit Reminder, Mark Complete) | Missing | m880457 `id:"bookmark"`, `id:"reminder"`. REST `/users/@me/saved-messages/{c}/{m}` (web bundle) | No item and no RPC | Needs a host RPC. |
| Speak Message / Stop Speaking Message | Missing | m355682 `id:"tts"` (when content is not empty). web m100767 uses `window.speechSynthesis` | None | Can be done in the UI alone. |
| Translate / Revert Translation | Missing | m355682 `id:"translate"`, behind an experiment check (`nu.b`); `/ai/translate` | None | Discord ships it behind an experiment. Low priority. |
| End Poll Now (author, poll still running) | Different | m355682 `M[END_EARLY]` `id:"end-poll-early"` | Poll.tsx:108 has an "End Poll" button in the poll's footer | It is in a different place, with a different label. Exo has no menu item. |
| Resend message (failed send) | Missing | m355682 `id:"resend"` when state is SEND_FAILED | Chat.tsx:575-580 removes the failed message | See Sending. |
| Remove Reactions (per emoji) / Remove All Reactions | Missing | m355682 `id:"remove-emoji-reactions"`, `id:"remove-reactions"` (Manage Messages; Shift skips the confirm) | Only one user at a time, from the Reactions dialog (Reactions.tsx:151-157) | Needs a host RPC (DELETE `/reactions/{emoji}`, `/reactions`). |
| Report Message / Report to Discord | Missing | m355682 `id:"report"` (others' messages), `id:"report-to-mod"` | None | Needs the host reporting flow. |
| Publish (announcement crosspost) | Missing | Hover bar `"publish"` "Publish"/"Published" (78f77b89 `nm`, `canPublish`). REST `MESSAGE_CROSSPOST` | None | Needs a host RPC. |
| Configure (join message, Manage Server) | Missing | m355682 `id:"configure"`, hover `"configure"` | None | Low priority. |
| View Interaction Info (installer and triggering user) | Missing | m355682 `id:"view-interaction-info"` | None (only CommandLine, Message.tsx:398-407) | |
| Mark as Official / Unmark as Official | Missing | m355682 `id:"guild-official-set/unset"` (`kn` check, feature-gated) | None | Behind a Discord feature gate. |
| Copy (selected text) and Search with Google | Missing | m373963 `id:"copy"` (Ctrl+C), m826308 `id:"search-google"`, in the desktop build (isPlatformEmbedded) | Message.tsx:570 returns early when text is selected. MainWindow.xaml.cs:614 turns off WebView's own menus, so no menu opens at all | |
| Copy Link / Open Link on a right-clicked link (also Copy Email Address / Phone Number) | Missing | m777933 `id:"copy-native-link"`, `id:"open-native-link"` | Right-clicking a link opens the plain message menu (Message.tsx:569-577) | |
| Favorite / Unfavorite Emoji or Sticker from a message | Missing | m253925 `id:"favorite"` / `id:"unfavorite"` (data-type emoji or sticker) | ImageMenu.tsx:97-127 catches the right-click and shows image items only | The host already has an RPC for this (`discordEmojiFavorites`, `discordStickers` add). |
| Copy Image / Save Image / Copy and Open link (images) | Match | m355682 `tF` = m115184, image-context | ImageMenu.tsx:63-127 | |
| Save File (voice message) | Missing | m355682 `id:"save-voice-message-audio"` | None | |
| Mark As Not Sensitive | Missing | m355682 `id:"report-image-false-positive"` | None (Attachments.tsx:86-98 blurs only) | |
| Menu grouping and order | Different | Groups: copy selection / search / quick reactions + Add Reaction + View Reactions / Edit, Reply, Forward, Thread / Copy Text … Pin, Bookmark, Apps, Mark Unread, Copy Link, Speak, End Poll / Remove Reactions, Delete, Report / item links / Copy Message ID | Message.tsx:533-545: Edit, Reply, Forward, Apps, Thread, View Reactions / Copy Text, Pin, Mark Unread, Copy Link, Delete, Copy ID | |

### Hover bar and message keyboard

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| 3 quick reactions in the hover bar (frecent; fallback :100: :laughing: :sparkling_heart:), with tooltips "Click to react with …" / "Click to remove …" | Missing | 78f77b89 `nh`, `nc` | Message.tsx:525-530 | |
| Add Reaction button | Match | 78f77b89 `ns` "Add Reaction" | Message.tsx:481-492, 525 | |
| Separate Add Super Reaction button | Different | The bar has no burst button. Super reactions come from the picker's burst toggle (web: `isBurstReaction`, `onBurstReactionToggle`, "Find the perfect Super Reaction") or the reaction tooltip's "Add Super Reaction" (bdac07af) | Message.tsx:526 shows a second button when you have Nitro | |
| Reply (others' messages) / Edit (own messages) | Different | `nm`: `reply-other` when the message is not editable; `edit` when it is. `reply-self` only in the Shift-expanded bar | Message.tsx:527-528 shows Reply and Edit together on your own messages | |
| Forward / Create Thread / View Thread | Different | `nm`: Forward if forwardable; Create Thread only when Forward is not shown; View Thread when the message has a thread | Message.tsx:529 Forward is always shown. Thread actions live in the menu and ThreadBox | |
| Shift-held expanded bar (Copy ID, Copy Link, Configure, Mark Unread, Bookmark, Pin, Thread, Reply, Delete) | Missing | `nm` `isExpanded` (Shift held, keyboard mode off) | None | |
| Failed send bar: Retry and Delete | Missing | 78f77b89 `ng` "Retry", `delete-usent` "Delete" | Chat.tsx:575-580 removes the message | |
| Bar hidden while editing | Match | `nf` returns null while editing | Message.tsx:249 | |
| Message keyboard shortcuts (e Edit, Backspace Delete, p Pin, + React, r Reply, f Forward, s Speak, t Thread, Ctrl+C Copy Text, Alt+Enter Mark Unread, Esc Focus text area) | Missing | 78f77b89 `tE`. Shortcut list in web.d41b6bfa (group "MESSAGE") | Messages cannot take focus. No handler | |
| Action availability by permission (ADD_REACTIONS, lurking, archived thread) | Different | `nm`/`tx.A` `disableReactionCreates` | Message.tsx:525 always shows React. DiscordChannel has no canReact (lib/discord.ts:19-40) | Needs the host to send the permission. |

### Editing, deleting, replying, forwarding, threads

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Up arrow edits your last message | Match | composer behavior | Composer.tsx:273-274. Chat.tsx:118-121 | |
| Edit in place: Enter saves, Shift+Enter adds a line, Esc cancels, footer "escape to cancel • enter to save" | Match | `wDsPXs` "escape to cancel • enter to save"; 78f77b89 `tE` (Esc ends the edit); 9abd77d7 m205120 | Message.tsx:437-478 | |
| Saving an empty edit asks to delete the message | Different | m205120 `onSubmit`: empty content and no attachments → onConfirmDelete | Message.tsx:457 empty → cancels | |
| Edit box shows mentions and emoji as typed, with autocomplete | Different | The edit uses `unparse` to show text as typed and `parse` on save (m205120, 26ae7858 `sV.Ay.unparse`) | Message.tsx:438 starts from raw `content`, so `<@id>` and `<:x:id>` show as they are. No autocomplete | |
| "(edited)" mark | Match | d3907dbc ("edited") | Message.tsx:291 | |
| "(edited)" and time tooltip ("Edited {time}", LLLL date) | Missing | bd5374d4 m606049 (tooltip after 750 ms) | Message.tsx:264, 291 plain text | Exo rules out native `title=`, so this needs Exo's own tip. |
| Delete confirm with message preview | Match | `AMvpS4` "Are you sure you want to delete this message?" | DeleteConfirm.tsx | |
| Reply bar "Replying to {name}" with @ ON/OFF | Different | `eleYb7`, `ApAu9f`/`64pl82` ON/OFF. Toggle hidden in DMs and on your own messages (727ee942 `C`) | Composer.tsx:342-355. Chat.tsx:109 hides it only for your own messages | Exo shows the toggle in DMs. |
| Reply quote jumps to the original; "Original message was deleted" / "Click to see attachment" | Match | 78f77b89 `nU`; `mE3KJN`, `6hGo0c` | Message.tsx:586-596 | |
| Forward To: up to 5 places, optional message | Match | 9e4bd0be: "Forward To", "Maximum {count} places at once." (5) | MessageTools.tsx:38-90 | |
| Forward modal details (preview of the message, "Send ({count})", Copy Link action, slowmode warning, per-destination failures) | Different | 9e4bd0be | MessageTools.tsx:51-55 sends one place at a time and stops at the first failure. Label "Send to N". No preview | |
| Forwarded message rendering ("Forwarded", snapshot, origin and time) | Match | `ToyvLk` "Forwarded" | Message.tsx:352-369 | |
| Thread box under the message | Different | Shows the thread name, message count and a preview of the last message | Message.tsx:387-395: name and count only | |
| New thread auto-archive | Different | Uses the channel's default_auto_archive_duration | DiscordRest.cs:162, 166 always send 4320 | Host-side. |

### Sending and composer

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Enter sends, Shift+Enter adds a line | Match | | Composer.tsx:269-270 | |
| Placeholder "Message #channel" / "@user" | Match | `4c+CAx` "Message {channel}" | Chat.tsx:149, 713-716 | |
| No-permission placeholder | Different | `IYKTTc` "You do not have permission to send messages in this channel." | Chat.tsx:149 "You can't send messages here" | |
| Pending message shown at once, then replaced | Match | | Chat.tsx:565-603 | |
| Failed send stays in the list, red, with Retry/Delete and "Resend message" | Different | 78f77b89 `ng`; m355682 `id:"resend"` | Chat.tsx:575-580 removes it and puts reply and error back. Composer.tsx:237-243 puts the text back | |
| @silent prefix (SUPPRESS_NOTIFICATIONS 4096) | Missing | web m677413 `"@silent"`, rule `silentPrefix` | Sent as literal text. DiscordRest.cs:127 `["flags"] = 0` | Needs the host to accept flags. |
| Built-in commands /shrug /tableflip /unflip /me /spoiler /tts /nick, `+:emoji:` reacts to the last message, `s/old/new/` edits your last message | Missing | 9abd77d7 m751258 | AppCommands.tsx:18-66 lists app commands only | Everything except /tts and /nick can be done in the UI alone. |
| Character counter (shows at ≤10% left: "{count} characters remaining", "Message is too long") | Missing | web.d41b6bfa `qH8uFW`, `YSRIqa`; limit `uvi`=2000, 4000 with Nitro (`canUseIncreasedMessageLength`) | Composer.tsx:307 `maxLength={4000}` for every account | Free accounts can type 4000 characters and Discord then rejects the message. |
| "Your message is too long..." with Upload as File | Missing | 57f90b94 m798612 | None | |
| Drafts kept per channel | Different | `DraftStore` is a PersistedStore (web.d41b6bfa) and survives a restart | Composer.tsx:22-23 keeps them in an in-memory Map only | Files and the reply are not kept. |
| Typing indicator text | Fixed | 54d3f728: "$b{a} is typing...", "$b{a} and $b{b} are typing...", "$b{a}, $b{b}, and $b{c} are typing...", "Several people are typing..." (4+) | Composer.tsx:377-381: "A, B are typing…", "A, B, C are typing…" | Fixed (a8c98e9): "A and B are typing…", "A, B, and C are typing…", "Several people are typing…". |
| Sends the typing ping | Match | | Composer.tsx:250-253 (every 8 s) | |
| Slowmode icon and countdown | Match | `IWntYg`, `4rMiHy` | Composer.tsx:321-339, 368-372 | |
| Age-restricted (NSFW) channel gate | Missing | web.d41b6bfa "Age-Restricted Channel", Continue (NSFW_CHANNEL_VERIFIED) | No gate. DiscordChannel has no `nsfw` field | Needs the host to send the flag. |
| `:` emoji autocomplete (custom and Unicode, 2+ characters, Nitro rules) | Match | | Composer.tsx:49-55, 173-178 | |
| Sticker suggestions in autocomplete | Match | | Composer.tsx:25-41 | |
| `@` autocomplete | Different | Users, roles, @everyone/@here; opens on a bare "@" | Composer.tsx:17 needs 2+ characters. Users only (Composer.tsx:58-75, 177) | No roles, @everyone or @here. |
| `#` channel autocomplete | Missing | web: rule for `#` (channel disambiguation) | Composer.tsx:17 only handles `:` and `@` | |
| `/` app command list | Match | | AppCommands.tsx:18-66. Composer.tsx:287-290 | |
| Rich editor (live markdown, mention pills, emoji images, formatting toolbar on selection) | Missing | Slate editor rules (web.d41b6bfa: strong/em/u/s/inlineCode/codeBlock…). Tip "Highlight text in your chat bar to bold…" | Plain `<textarea>` (Composer.tsx:300-312) | |
| Text area menu (Cut, Copy, Paste, Paste as Plain Text, Slash Commands, Suggestions, Send Message Button, spellcheck) | Missing | 1691193d m258360 navId "textarea-context" | MainWindow.xaml.cs:614 turns off WebView's own menus. No custom menu | |
| Send button (optional) | Missing | m258360 `id:"submit-button"` "Send Message Button" | None | |
| + menu (channel-attach) | Different | 78f77b89 navId "channel-attach": Upload a File, Upload Text as File, Create Thread, Create Poll, Use Apps, App Launcher, Clips, Schedule Message, activity invites | PollCreator.tsx:89-114: Upload a File, Create Poll | Upload Text as File can be done in the UI alone. |
| Paste files | Match | | Composer.tsx:311 | |
| Drag and drop | Different | Overlay over the whole chat: "Upload to $b{destination}", "You can add comments before uploading. Hold shift to upload directly." | Composer.tsx:280-285: the composer area only | |
| Upload size limit | Different | web.d41b6bfa: 10 MB free (`TbF`=0xa00000), 50 MB Basic, 500 MB Nitro, servers with MAX_FILE_SIZE_50/100_MB | Chat.tsx:19, 563 and host WebHostBridge.Discord.cs:165: a fixed 25 MB | Too generous for free accounts and too small for Nitro. |
| Upload count (10) | Different | "Too many uploads!" / "You can only upload {limit} files at a time!" | Composer.tsx:16, 205 drops extra files silently | |
| Upload chips: spoiler, Modify Attachment (name, alt text), Remove Attachment | Different | web.d41b6bfa "Modify Attachment", "Remove Attachment" | Composer.tsx:384-403: spoiler and remove only | Alt text needs the host upload to carry `description`. |
| Upload progress | Match | | Message.tsx:425-434. Chat.tsx:583-594 | |
| Composer buttons: GIF, Sticker, Emoji | Match | "Open GIF picker", "Select emoji" | Composer.tsx:442-470 | Exo's aria labels differ ("GIFs", "Stickers", "Emoji"). |
| Composer buttons: Send a gift, App Launcher | Missing | `g86YiI`, `hSQgW9` | None | |
| Chat shortcuts: Esc marks the channel read, Ctrl+E/G/S open the pickers, Ctrl+Shift+U uploads, Ctrl+P pins, Shift+PageUp jumps to the oldest unread | Different | web.d41b6bfa shortcut list, group "CHAT" | Composer.tsx:259-276 handles Enter, Esc (reply) and Up only | Partly fixed (a8c98e9): Esc in the message box marks the channel read (after closing a reply or suggestions). Ctrl+E/G/S, Ctrl+Shift+U and Ctrl+P are still missing. |
| Stickers sent with the text in the box or as a reply | Different | web `t5`: with text or uploads in the box, the sticker joins the draft; otherwise it is sent at once | Composer.tsx:189-194 always sends at once, apart from the text. DiscordRest.cs:410-411 sends no reference, so a sticker cannot be a reply | Needs the host. |
| GIF picker | Different | Front page of Favorites, Trending GIFs and category tiles (`/gifs/trending`), search suggestions (`/gifs/suggest`) | GifPicker.tsx:67-142: Trending and Favorites tabs, search | |
| Emoji picker | Different | Skin tone (`diversitySelector`), Shift+click keeps it open (`willClose`), burst toggle when reacting | EmojiPicker.tsx:53-171. Composer.tsx:463-464 closes on every pick. No skin tone | |

### Reactions

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Reaction chips: click to toggle, super reactions with their colors, super ones first | Match | | Reactions.tsx:13-21, 66-112 | |
| Who-reacted tooltip | Different | bdac07af `eP`: "{emojiName} reacted by {a}, {b}, and {n}" with a large emoji; "n others" opens the list; blocked users left out; "Add Super Reaction" CTA for Nitro | Reactions.tsx:54-59: "A, B and 4 others reacted with :x:", text only | |
| Add Reaction button at the end of the reactions row | Missing | 8212d570 (`reactionBtn`, "Add Reaction") | Reactions.tsx:91-111 | |
| Reactions list (View Reactions), Manage Messages removes one user's reaction | Match | m355682 `id:"reactions"` → modal 112753 | Reactions.tsx:118-197 | |
| Super reaction from the picker (burst toggle) | Different | web picker `isBurstReaction` / `onBurstReactionToggle` | Message.tsx:480-492 uses a separate hover button | |
| "Show emoji reactions" setting | Match | | Reactions.tsx:75 | |

### Message rendering: markdown, mentions, timestamps

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Bold, italic, underline, strikethrough, inline code, escapes | Match | Rules `strong`/`em`/`u`/`s`/`inlineCode`/`escape` | markdown.tsx:27-44, 107-127 | |
| Headings #/##/###, subtext `-# `, lists (- and 1.) | Match | Rules `heading`, `subtext`, `list` | markdown.tsx:196-210 | |
| Block quotes `>` and `>>>` | Match | Rule `blockQuote` | markdown.tsx:213-233 | |
| Code blocks with syntax highlighting | Different | `codeBlockLang`/`codeBlockSyntax`, highlight.js (`hljsTypes`), ANSI | markdown.tsx:181, 186: the language is dropped and nothing is highlighted | |
| Spoilers | Match | Rule `spoiler` | markdown.tsx:49-62 | |
| Masked links `[text](url)` | Match (render) | Rule `link` | markdown.tsx:40, 123 | |
| "Leaving Discord" warning for untrusted or masked links | Missing | m777933 `shouldConfirm`/`trusted`. `3w1QGl` "Leaving Discord" | markdown.tsx:163-167 opens the link straight away | |
| Discord message and channel links (discord.com/channels/…) open inside the app, as a chip | Different | Rule `channelOrMessageUrl` | markdown.tsx:41, 124 → lib/music.ts:44-52 opens the browser | Partly fixed (170896a): a discord.com/channels link opens its server, channel or message in the room. Drawing it as a chip is still missing. jumpToMessage (lib/discord.ts:316) already exists. |
| User, role and channel mentions; @everyone/@here | Match | Rules `mention`, `roleMention`, `channelMention` | markdown.tsx:130-161, 125 | |
| Slash command mentions `</name:id>` | Missing | Rule `commandMention` | Not in INLINE (markdown.tsx:27-44) | |
| Server navigation links `<id:home\|browse\|customize\|guide\|linked-roles>` | Missing | Rule `staticRouteLink` (regex in web.d41b6bfa) | Not in INLINE | |
| Timestamps `<t:…>` | Different | web m379418: formats t T d D f F **s S** R. m785562: R updates live, tooltip shows the full date | markdown.tsx:39 regex `[tTdDfFR]` (no s/S, so `<t:x:s>` shows raw). formatTimestamp (64-87) is static, with no tooltip | |
| Custom emoji, animated per setting; jumbo when the message is emoji only | Match | | markdown.tsx:117, 169 | |
| Unicode emoji drawn as images | Different | Twemoji URLs (`es.Ay.getURL`) | Drawn as text glyphs (system emoji font) | A look difference. |
| Emoji and sticker popouts on click | Missing | Emoji popout (which server it comes from); sticker popout | None | |
| Highlight for messages that mention you | Different | Your user, @everyone/@here, your roles | Message.tsx:564-566 checks your user and mention_everyone. `mention_roles` is not checked (the UI doesn't know your roles) | The host knows them (DiscordCache.cs:1443-1451 `MyRoles`). |
| Message time "Today at / Yesterday at", 12/24-hour setting | Match | | Message.tsx:152-163 | |
| Grouping (same author within 7 minutes, same day) | Different | 9abd77d7 m857069: 7 min; a new group after a system message, on a change in ephemeral or scheduled flags, a different webhook username, a different application, a thread, or a command | Chat.tsx:18, 274-275 | Exo can tuck a user's message under their own join or call line, and groups webhook posts that use different names. |
| Compact mode | Match | | discord-settings.css:333-371. Message.tsx:231-233 | |
| Day dividers, NEW line, "N new messages since…" + Mark As Read, Jump to Present | Match | | Chat.tsx:138-143, 247-259, 262-287 | |
| Blocked and ignored users' messages collapsed ("N blocked messages", Show) | Missing | 78f77b89 `eA` → MESSAGE_GROUP_BLOCKED / IGNORED / SPAMMER / SUSPENDED_USER with `collapsedReason`, `canUncollapse` | Shown like any other message | The host knows who is blocked and ignored (DiscordCache.cs:127-128 `_blocked`, `_ignored`) but sends no list with messages. This branch added a per-user `discordFriend('state')` (6937c04), which is one request per person, not a list. |
| @silent marker on received messages | Missing | `RO/KYj` "This is a @silent message." | None | |

### Embeds, attachments, media, stickers

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Rich embed card (color bar, provider, author, title link, description, fields, image, thumbnail, footer) | Different | Embed component in web.d41b6bfa | Message.tsx:84-122: no author icon or URL, no footer icon, no embed timestamp | |
| Bare image or GIF link shows only the media | Match | | Message.tsx:49-67, 579-583 | |
| GIFV playback follows the GIF autoplay setting | Match | | Message.tsx:25-41 | |
| Video embeds (YouTube) play in the message | Different | web: `youtube.com/embed/{id}` video component with a play button | Message.tsx:97-104, 107: the thumbnail opens in the image viewer and never plays | |
| Spotify embed player | Different | web: `open.spotify.com/embed` builder | A plain embed card. Only Jam links get a card with Join (Message.tsx:316-349) | |
| Server invite card ("You've been invited to join a server", Join / Joined) | Missing | `BoQUFf` | Shown as a plain link | The host's join RPC (`discordServer('join')`) exists. Reading the invite needs the host. |
| "Remove all embeds" (author or Manage Messages) | Missing | web.d41b6bfa `renderSuppressButton`, `vXZ+Fo` | Message.tsx:294 respects flag 4 but offers no way to set it. The host edit only sends content (DiscordRest.cs:145-146) | Needs the host. |
| Star (favorite) on GIFs in messages | Missing | 8212d570 `getGifFavButton` | None. The GIF favorites RPC exists | |
| Image mosaic (2–10 pictures) | Match | | Attachments.tsx:106-130 | |
| Spoiler attachments (SPOILER_ / flag 8), "Show spoiler content" setting | Match |  | Attachments.tsx:58-67, 85, 98 | Fixed (75f0b35): "On servers I moderate" now shows spoilers where you may Manage Messages. |
| Sensitive media blur and block per setting | Match | | Attachments.tsx:51-55, 86-87 | |
| Alt text: ALT tag ("Show image descriptions") | Match | | Attachments.tsx:95-100 | |
| Text file preview with Expand | Match | | Attachments.tsx:152-180 | |
| Audio and video attachments | Match | | Attachments.tsx:94, 185-191 | |
| Voice message: waveform, seek, time | Match | | VoiceMessage.tsx:26-65 | |
| Voice message playback speed | Missing | `LgCPMt` "Change playback speed. Current speed is {playbackRate}x" | VoiceMessage.tsx has no speed control | |
| Media viewer (zoom, carousel through the message's media, Download, Forward, Open in Browser) | Different | `Kt4gZ6`/`vOFof8` Zoom, `q5jLJB` "Open in Browser", Download | extras.tsx:109-129: one picture, no controls | |
| Stickers: PNG, APNG, GIF, Lottie; animation setting | Match | | Message.tsx:301-308, 413-422. LottieSticker.tsx | |

### System messages

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Join message | Different | `getSystemMessageUserJoin`: random lines ("Welcome, {username}. We hope you brought pizza.", …), "Wave to say hi!" button | Message.tsx:130 "joined the server." | |
| Group add, remove, leave | Different | `MMN2Jq` "added {other} to the group.", `L2FyVq` removed, `5v2xa8` "left the group." | Message.tsx:125-126: "added someone…", "removed someone…" | The target's name is missing, and leaving shows as removed. |
| Channel name change | Different | `oItgEw` "changed the channel name: **{channelName}**" | Message.tsx:127 leaves out the new name | |
| Pin notice | Different | `7mvRNF` "pinned **a message** to this channel. See all **pinned messages**." (both are links) | Message.tsx:129 plain text | |
| Thread created | Different | `cZ9Uf2` "started a thread: **{name}**. See all **threads**." | Message.tsx:211-216 has the thread link but not "See all threads" | |
| Boosts | Different | `ihxM9x` "just boosted the server!", `rbj006` "…{n} times!", "{guild} has achieved {tier}!" | Message.tsx:131-134 a fixed sentence | |
| Calls (started, missed, length) | Match | `43phHx`/`AcqBmO`/`7TeC1P` | Message.tsx:599-619 | |
| Poll result line | Match | | Poll.tsx:121-140 | |

### Polls

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Create poll: question, 2–10 answers, duration 1h–2 weeks, multiple answers | Match | `GD/8X8` "Create a Poll", `B2Uvme` "Add another answer", `Ux+iQU` | PollCreator.tsx:19-86 | Exo's title reads "Create a poll". |
| Emoji on each answer | Missing | `iMJO37` "Add Emoji", `59QgaD` "Select emoji" | PollCreator.tsx:57-67. The host poll RPC takes texts only | Needs the host. |
| Vote, Remove Vote, results bars, "{n} votes · {time} left" | Match | 105a1124 | Poll.tsx:33-115 | |
| Show results without voting / Go back to vote | Missing | 105a1124 `/KHAUF` "Show results", `gNj6In` "Go back to vote" | Poll.tsx:51-52 shows results only after you vote or when the poll closes | |
| See who voted for an answer | Missing | 105a1124 "Reveal voters" (`showVoterDetails`) | None | Needs the host. |

### Pins and search

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Pins popout list with Jump | Match | Pins popout (`fmyaWJ` empty state, "Jump") | ChatHeader.tsx:160-164. results.tsx:24-51 | |
| Unpin from the popout (confirm; Shift skips) | Fixed | Same popout: `confirmUnpin`, "Unpin Message" | results.tsx has an `onRemove` slot, but Pins does not use it | Fixed (9e2d751): Unpin on each pin where you may pin, asked first, Shift skips. The host RPC exists: `discordPins(channelId, id, false)`. |
| More pins ("Load more messages") | Missing | Same popout | DiscordRest.cs:222-223 fetches `limit=50` once | |
| Search filters from:, mentions:, has:, in:, before:/during:(on:)/after:, pinned:, authorType:, negation | Match | Filter keys `1TUdFo`, `i96lO+`, `CqCvir`, … | SearchBar.tsx:12-76 | Exo labels has:forward "forwarded"; Discord says "forward". |
| Sort Newest / Oldest / Most Relevant, pages of 25 | Match | 8f6b480f navId "search-result-sort-menu" | SearchBar.tsx:249-309 | |
| Search all DMs | Missing | 8f6b480f `id:"xdm-search-enabled"` "Search all DMs" | SearchBar.tsx:117-121 searches this DM only | |
| Result tabs (`/messages/search/tabs`) | Missing | web REST `SEARCH_TABS_*` | One list | |

### App commands and interactions

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Slash command options (required first, optional chips), choices, user/role/channel pickers | Match | | AppCommands.tsx:75-235 | |
| Autocomplete options | Match | | AppCommands.tsx:238-281 | |
| Attachment options | Match | | AppCommands.tsx:174-182, 93-112 | |
| "Sending command..." / "The application did not respond" | Match | `RiLfBY`, `h8hzPd` | discordApps.ts:153-160 | |
| "{name} is thinking..." (deferred response) | Match | `kz3mf0` | Message.tsx:268, 372-374 | |
| "Only you can see this • Dismiss message" | Match | `vgUjKR` | Message.tsx:377-384 | |
| Ephemeral messages allow only Dismiss (no Reply, Forward, Pin) | Different | m517997 (no reply on EPHEMERAL), m843626, `tb` | Message.tsx:249 shows the full bar on ephemeral messages | |
| "used /command" line over the response | Match | `SSrolr`, `mqKdCM` (with ›target for user commands) | Message.tsx:398-407 | No ›target for user commands. |
| Buttons, selects, Components V2 layout; "This interaction failed" | Match | `VCsUJu` | Components.tsx:25-254. discordApps.ts:119-134 | Premium (SKU) buttons are disabled in Exo. |
| Modals (text inputs, labels, radios, checkboxes, file upload) | Match | | AppModal.tsx | |
| Message and user context commands (Apps) | Match | m646911 | AppCommands.tsx:288-324 | |

### Fixes

#### Safe fix candidates
These are UI-only, or use a host RPC that already exists. Ones marked **needs host** need a small host change.

- **Partly done (0a486ae): Add Reaction is in the menu; the quick-react row is not.** **Add Reaction in the More/right-click menu, and a quick-react row** (Message.tsx:531-546). Discord m355682 `tA` and `td`: up to 12 frecent emoji, then "View More" (opens EmojiPicker). Reuse `actions.onReact`. The frecent list comes from `host.discordEmojiFavorites` (`reactions`); export `loadSaved` from EmojiPicker.tsx:30.
- **3 quick reactions in the hover bar** (Message.tsx:525). Discord 78f77b89 `nh`: frecent reactions, falling back to :100: :laughing: :sparkling_heart:. Clicking one adds or removes your reaction.
- **Hover bar layout like Discord's** (Message.tsx:526-529): Reply on others' messages and Edit on your own. Drop the separate Super Reaction button and add a "Super React" toggle to the reaction EmojiPicker (EmojiPicker.tsx `reacting`).
- **Forward and Edit availability** (Message.tsx:522, 529, 535). Hide Forward on polls, voice messages (flag 8192), forwards (`message_snapshots`), messages with a thread, and ephemeral messages (m843626). Hide Edit on polls and voice messages (m697470). On ephemeral messages, offer only Dismiss.
- **Reply ping rules** (Chat.tsx:109, Message.tsx:527/534). Shift+Reply turns the ping off. In DMs there is no @ toggle (`showMentionToggle:!isPrivate&&!own`).
- **Done (606408a).** **Pin/Unpin confirm, with Shift to skip** (Chat.tsx:215-221). Discord: "Pin It. Pin It Good." / "Hey, just double checking that you want to pin this message to #{channelName}…"; unpin tip `oCVB3Y`. Reuse the DeleteConfirm.tsx pattern.
- **Done (9e2d751).** **Unpin from the pins popout** (ChatHeader.tsx:160-164, results.tsx `onRemove`). Use the existing `host.discordPins(channelId, id, false)`, gated by `channel.canPin` or a DM.
- **Done (a8c98e9).** **Typing text** (Composer.tsx:377-381): "A and B are typing…", "A, B, and C are typing…", "Several people are typing…" for 4 or more.
- **Timestamps** (markdown.tsx:39, 64-87). Add the `s`/`S` formats (`L LT`/`L LTS`), refresh `R` live, and show the full date in a tooltip on hover.
- **Tooltips on the message time and "(edited)"** (Message.tsx:264, 291): the full date and "Edited {time}" (m606049), using Exo's own tip, not `title=`.
- **Done (170896a).** **Discord links open inside Exo** (markdown.tsx:163-167). For `discord.com/channels/{g|@me}/{c}[/{m}]`, call `jumpToMessage`/`openDiscord` (lib/discord.ts:316) instead of the browser.
- **Link items on right-click** (Message.tsx:569-577). When the target is inside an `<a>`, add Copy Link and Open Link (m777933). With text selected, show Copy (m373963) instead of no menu at all.
- **Favorite Emoji or Sticker from a message** (ImageMenu.tsx:97-127). For an emoji or sticker picture, add Favorite/Unfavorite (m253925) through the existing `discordEmojiFavorites('add'|'remove')` and `discordStickers(guildId,'add'|'remove')`.
- **Star on GIFs in messages** (Message.tsx:58-67) through the existing `host.discordGifFavorites`.
- **Command mentions `</name:id>`** (markdown.tsx:27-44). Draw them as a command chip; clicking one opens that command in the composer (CommandList pick).
- **Built-in text commands** (Composer.tsx `submit` at 221-244). /shrug, /tableflip, /unflip, /me, /spoiler change the text. `+:name:` reacts to the last message via `discordReact`. `s/old/new/[g]` edits your last message via `discordEdit`, and deletes it if the result is empty (9abd77d7 m751258).
- **Character counter and "too long"** (Composer.tsx:307). Limit 2000, or 4000 with `useNitro`. Show "{count} characters remaining" once 10% or less is left. Over the limit, show "Your message is too long..." with Upload as File, which turns the text into a `message.txt` File and uses the existing upload path. Exact Nitro tiers **needs host** (premium_type; the host only sends `premium` as true/false at DiscordCache.cs:155).
- **Upload Text as File in the + menu** (PollCreator.tsx:105-110), when the box has text (78f77b89 `UPLOAD_TEXT_AS_FILE`). A fenced block becomes `message.<lang>`.
- **"Too many uploads!"** (Composer.tsx:205) instead of silently cutting the list to 10 files.
- **Drafts kept across restarts** (Composer.tsx:22-23). Save text and mentions to `localStorage` per channel, with try/catch, as Discord's DraftStore does.
- **Partly done (a8c98e9): Esc in the message box; the other shortcuts are not.** **Esc marks the channel read** when there is no reply or open list (Composer.tsx:271-275 → Chat.tsx `markAllRead`). Add Ctrl+E, Ctrl+G and Ctrl+S for the pickers, Ctrl+Shift+U for upload and Ctrl+P for pins (shortcut list in web.d41b6bfa).
- **Speak Message** (Message.tsx menu): `speechSynthesis`, "{username} said {body}", and Stop Speaking Message, as web m100767 does.
- **Polls**: "Show results" and "Go back to vote" (Poll.tsx:50-52, 104-111). Move End Poll into the menu as "End Poll Now" for the author (m355682).
- **Grouping** (Chat.tsx:274-275). Do not group under a system or call line (`before.type` not 0/19). Start a new group when the webhook username, `application_id` or EPHEMERAL flag changes (m857069).
- **Failed sends** (Chat.tsx:575-580). Keep the message, marked failed, with Retry (calls `discordSend` again with the kept File objects) and Delete, instead of putting the text back in the composer.
- **System message text** (Message.tsx:124-221). Name the target for group add and remove (`mentions[0]`). Show "left the group." when the author removed themselves. Include the new channel name (`content`). Add the "See all pinned messages" link (opens Pins) and "See all threads". Use the boost count wording.
- **Empty edit means delete** (Message.tsx:457). Open DeleteConfirm instead of cancelling (m205120).
- **"Leaving Discord" confirm** for masked links whose text differs from the URL (markdown.tsx:123, 163-167).
- **No-permission placeholder** (Chat.tsx:149): "You do not have permission to send messages in this channel."
- **Voice message speed** 1x / 1.5x / 2x (VoiceMessage.tsx), set with `audio.playbackRate`.
- **Reaction tooltip** wording and the trailing "+" chip (Reactions.tsx:54-59, 91-111). Use "{emoji} reacted by A, B, and N others" and add an Add Reaction button after the chips (8212d570).
- **Emoji picker Shift+click** keeps the picker open (Composer.tsx:463-464, Message.tsx:487-488).
- **Drop zone over the whole chat** with "Upload to #channel" (Composer.tsx:280-285 → Chat.tsx section).
- **Video embeds** (Message.tsx:97-104): clicking a video embed's thumbnail opens `embed.url` (the video) instead of showing the still image in the lightbox.
- **Embed author and footer** (Message.tsx:84-121): author `icon_url` and `url`, footer `icon_url`, and `timestamp`.
- **Forward dialog** (MessageTools.tsx:38-90): a preview of the message, the label "Send (N)", and sending to every destination with a report of the ones that failed instead of stopping at the first.
- **@silent**: remove the prefix and send flag 4096. **needs host**: DiscordRest.cs:127 hard-codes `flags = 0`; add a `silent` option to `discord.send`.
- **Remove Reactions / Remove All Reactions** (m355682). **needs host**: DELETE `/reactions/{emoji}` and `/reactions`, next to RemoveReactionAsync (DiscordRest.Inbox.cs:85-86).
- **Remove all embeds** (suppress). **needs host**: PATCH `flags` on edit (DiscordRest.cs:145-146).
- **Publish** in announcement channels (hover bar). **needs host**: POST `/channels/{c}/messages/{m}/crosspost`.
- **Mentioned-role highlight** (Message.tsx:564-566). **needs host**: send your role ids for the server (the cache has `MyRoles`, DiscordCache.cs:1450).
- **Permission-aware controls** (React, attach button, Create Thread). **needs host**: add `canReact`, `canAttach`, `canCreateThreads` and `nsfw` to `DiscordChannel` (lib/discord.ts:19-40).
- **Sticker as a reply or with text** (Composer.tsx:189-194). **needs host**: `discord.sticker` should take a reply and content (DiscordRest.cs:410-411).

#### Proposals (large/risky)
- **Rich composer**: a contenteditable or Slate-style editor with live markdown, mention pills, emoji images, a formatting toolbar on selection, and the same editor for edits (text shown as typed, encoded on save). This is a big change and touches Composer, InlineEdit and the autocomplete.
- **Text area menu** (Cut, Copy, Paste, Paste as Plain Text, spellcheck suggestions, Send Message Button): either a custom menu that reads the clipboard through the host, or turning WebView's own menus back on for editable targets only (MainWindow.xaml.cs:614). That second option is a host and WebView policy change.
- **`@` roles, @everyone/@here and `#` channel autocomplete**, with MENTION_EVERYONE checks (Composer.tsx:17, 58-75). Needs role and channel lists per server in the composer, and the host's permission check.
- **Collapse blocked and ignored users' messages** ("N blocked messages", Show). The host must send the blocked and ignored ids as a list, or flag them on each message. DiscordCache.cs:127-128 already tracks both; the per-user `discordFriend('state')` added on this branch is one request per person, too slow for a message list. The UI groups runs as MESSAGE_GROUP_BLOCKED does.
- **Bookmarks and reminders** (Bookmark Message, Create Reminder with preset times, Mark Complete, and a list in the Inbox): new host RPCs over `/users/@me/saved-messages`.
- **Report Message**: Discord's multi-step reporting menu. It is a new host flow and a policy surface, so be careful with it.
- **Nitro- and server-aware upload limits**: 10 / 50 / 500 MB, plus MAX_FILE_SIZE_50/100_MB servers. The host holds uploads in memory with a 25 MB cap (WebHostBridge.Discord.cs:165). Going higher means streaming from disk or Discord's attachment-upload (presigned PUT) flow.
- **Modify Attachment** (rename, alt text description) on upload chips: the host upload and message attachments need `description`.
- **Media viewer**: carousel through the message's media, zoom, Download, Open in Browser and Forward (extras.tsx:109-129).
- **Inline YouTube and Spotify players**: iframes in a WebView2 with Exo's CSP. This has privacy and PRIVACY.md implications, because those pages would load inside Exo.
- **Invite cards**: the host reads `/invites/{code}` for the server name, icon and counts; Join uses the existing `discordServer('join')`.
- **Poll voters and answer emoji**: new host RPCs for `/polls/{m}/answers/{id}` and emoji in `discordPollCreate`.
- **Focusable messages with the message keyboard** (e, r, p, f, +, Backspace, s, t, Alt+Enter) and the **Shift-expanded hover bar**. This needs focus management across the list.
- **Thread side panel** instead of the Create Thread modal, and a thread box with a preview of the last message. Use the channel's default auto-archive duration (host).
- **Search all DMs and result tabs** (`/users/@me/messages/search/tabs`, `/messages/search/tabs`).
- **Age-restricted channel gate**: the host sends `nsfw` and `nsfwAllowed`, and the UI shows the gate with Continue or the underage notice.
- **Syntax highlighting in code blocks**: needs a highlighter library (highlight.js is what Discord uses). Weigh it against Exo's bundle-size and dependency rules.
- **Unicode emoji as Twemoji images**: a look decision that also changes the bundle or network use. Leave it to DESIGN.md.

## Area: notifications, the Inbox, moderation and profiles

Exo lines are as of `discord-audit` @ 8166098. Discord citations are to the bundle snapshot (`web.d41b6bfa9f8fcb8b.js` is the main bundle; other files are lazy chunks). `mod N` means webpack module N. Unread dots and acks are left out on purpose; they come up only where notifications depend on them.

Key Discord sources used throughout:
- **shouldNotify**: web `mod 176154`, functions `K` (spammer, managed, lurking, self, blocked/ignored, DND, quietMode, `allowNoMessages`) and `$` (export `lx`: SUPPRESS_NOTIFICATIONS, message request, selected channel, thread flags, voice-chat rule, mention test with suppress flags). Notification text is built by `TB` in the same module.
- **UserGuildSettingsStore**: web `mod 543465` (`resolvedMessageNotifications`, `allowNoMessages`, `allowAllMessages`, `isGuildOrCategoryOrChannelMuted`, `isGuildCollapsed` = hide_muted_channels).
- **Thread notification level**: web `mod 607508` and `mod 37411`. Member flags are ALL=2, MENTIONS=4, NOTHING=8. A thread you have not joined resolves to NO_MESSAGES.
- **NotificationStore**: web `mod 592329` (MESSAGE_CREATE → showNotification, sound `message1` at 0.4, `message3` for the selected channel, clearing on CHANNEL_SELECT / MESSAGE_ACK / WINDOW_FOCUS, cap of 20 per channel, and the THREAD_CREATE, stage, event, reaction and reminder notifications).
- **NotificationSettingsStore**: web `mod 803224` (desktopType, disabledSounds, ttsType, disableUnreadBadge, taskbarFlash, notifyMessagesInSelectedChannel).

### Notifications

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Never notify for your own messages, blocked people, or while DND | Match | web mod 176154 `K` | DiscordCache.cs:843 | |
| Ignored users never notify | Fixed | mod 176154 `K` → `isBlockedOrIgnored` | DiscordCache.cs:843 checks `_blocked` only | Fixed (6937c04): the host tracks ignored people (relationship user_ignored) and they no longer notify. Discord also drops users you Ignore (relationship `user_ignored`). Exo has no Ignore. |
| @silent (SUPPRESS_NOTIFICATIONS flag) | Match | mod 176154 `$` | DiscordCache.cs:844 | |
| Message requests don't notify | Match | mod 176154 `$` (`isMessageRequest`) | DiscordCache.cs:847 | |
| Spammer-flagged authors and GROUP_DM RECIPIENT_REMOVE system messages don't notify | Missing | mod 176154 `K`/`$` | DiscordCache.cs:841-862 | Exo notifies for every message type, including system messages. |
| Level resolution: channel → category → server → server default | Match | mod 543465 `resolvedMessageNotifications` | DiscordCache.cs:1465-1477 | |
| Muted server, category or channel (timed mutes expire) | Match | mod 543465 `isGuildOrCategoryOrChannelMuted`, `allowNoMessages` | DiscordCache.cs:845,853,1487-1500 | |
| Opt-in servers notify only from opted-in channels | Match | mod 543465 `allowNoMessages` / `isChannelRecordOrParentOptedIn` | DiscordCache.cs:853,865 | |
| @everyone/@here and role-mention suppression | Match | mod 176154 (`bG` with suppressEveryone/suppressRoles) | DiscordCache.cs:1427-1435 | |
| Voice-channel chat notifies All Messages only while you are in that call | Match | mod 176154 `$` (`OU` = voice types, selected voice channel) | DiscordCache.cs:856 | |
| Thread notifications | Different | web mod 607508 + mod 37411; mod 176154 `$` (thread branch, no suppress) | DiscordCache.cs:1465-1471 | Discord uses the thread's member flags. A thread you have not joined never notifies. A joined thread with no setting of its own follows its parent, including All Messages. Exo ignores thread membership and thread settings, takes the parent's level, and caps it at mentions (`Math.Max(1, level)`). Exo also applies @everyone/role suppression in threads, which Discord does not. |
| Thread mute (per-thread) | Missing | 5ab9f1c421ba6b8f.js mod 57907 (Mute Thread / Mute Post) | none | Exo does not read THREAD_MEMBER_UPDATE mute or flags. |
| DMs: all messages unless muted | Match | mod 176154 `$` (`allowAllMessages` on a private channel) | DiscordCache.cs:846-851 | |
| Per-DM mute is honored | Match | mod 543465 (`channel_overrides` under the null guild) | DiscordCache.cs:1610-1630 (ReadMute on overrides) | |
| Per-DM mute can be set ("Mute @user" / "Mute Conversation" with durations) | Missing | 58c5cd79a16c8073.js mod 995102 (DM/GROUP_DM labels) | Sidebar.tsx:114 DmRow (only Close/Leave); WebHostBridge.Discord.cs:711 DiscordDmActionAsync (close/add only) | Needs host. |
| Selected-channel rule (no notification for the channel on screen while focused; notify it when unfocused) | Match | mod 592329 MESSAGE_CREATE `lx(a,r,!ed)` | DiscordNotifier.cs:37-42 Route; WebHostBridge.Discord.cs:153 DiscordView | Discord also treats a thread open in the sidebar as viewed. Exo tracks one viewed conversation. |
| "New Message in the channel I'm currently reading" sound (message3) | Different | mod 592329 (`getNotifyMessagesInSelectedChannel() && kY`); mod 176154 `z` | DiscordSession.Settings.cs:204-209; DiscordSounds.cs:209-210 | Discord's `z` skips DND, blocked people, spammers, managed channels and channels muted or set to Nothing. Exo plays it for any message from anyone else. |
| Desktop notifications off → sound only | Match | mod 592329 (desktopType NEVER → `playNotificationSound(message1)`) | DiscordNotifier.cs:40 | |
| Notification sound `message1` with each notification | Match | mod 592329 (`ea="message1"`, `es=.4`) | DiscordSession.cs:427 Sound("message1") | Discord plays it at volume 0.4; Exo's MediaPlayer uses 0.6 (DiscordSounds.cs:241). |
| Notification title | Different | mod 176154 `J`/`Z`: "Author (#channel, Category)"; group DM "Author (Group)" | DiscordNotifier.cs:69-71; DiscordCache.cs:869-886 | Exo uses the author as the title and "#channel · Server" as attribution. Discord shows the category, not the server. |
| Notification body for non-text messages | Different | mod 176154 `TB`: "Uploaded {filename}", "Sent a sticker: {stickerName}", "Sent a voice message", "[poll] {question}", "The poll {question} has closed", embed title+description, "Forwarded a message", Spotify/game invites, system messages via stringify | DiscordCache.cs:879 | Exo says "Sent an attachment" / "Sent a sticker" / "Sent a message". |
| Mentions and emoji in the body | Different | mod 176154 `TB` (`unparseWithMeta` resolves users, roles, channels, emoji) | DiscordCache.cs:874-878 | Exo resolves user mentions and custom emoji only. `<@&role>` and `<#channel>` stay raw. |
| Avatar as icon | Match | mod 176154 `TB` (`getAvatarURL(guild,128)`) | DiscordNotifier.cs:72-73,114-139 | |
| Click opens the conversation (a voice channel's chat opens) | Match | mod 592329 onClick (`transitionToChannel`, `updateChatOpen`) | DiscordNotifier.cs:149-156; MainWindow.Discord.cs:84-88 | |
| Notifications cleared on read, open or focus; a deleted message's notification removed | Match | mod 592329 (CHANNEL_SELECT, MESSAGE_ACK, WINDOW_FOCUS → clearChannel) | DiscordSession.cs:437-447; DiscordCache.cs:224,434 | Discord keeps at most 20 per channel (`el=20`). Exo has no cap. |
| In-app banner while the app is in front | Different | mod 592329 (OS notification even when focused, unless it is the selected channel) | DiscordNotices.tsx:16-78; DiscordNotifier.cs:41 | Deliberate Exo design: its own banner replaces the OS toast while Exo is in front. |
| Streamer Mode "Disable notifications" / "Disable Sounds" | Match | 73f98035cbc74893.js STREAMER_MODE_DISABLE_NOTIFICATIONS | DiscordNotifier.cs:40; DiscordSounds.cs:200,209 | |
| Game focus suppression | Different | mod 592329 (skipped only when a game with the overlay is focused and overlay text-chat notifications are on) | DiscordSession.cs:423-424 ("Quiet while playing"); AppPages.tsx:23 | An Exo-specific setting, not Discord's rule. |
| Focus Mode (BETA) / quietMode suppresses notifications | Missing | mod 176154 `K` (`C.NO` = notifications.quietMode, mod 885386) | none | |
| Taskbar flashing | Match | mod 803224 `taskbarFlash` | MainWindow.Discord.cs:47-52; DiscordSession.cs:430 | |
| Unread badge count = mentions + DM unreads | Match | web.d41b6bfa9f8fcb8b.js @1830630 (`getTotalMentionCount()`) | DiscordCache.cs:775-780 | DM messages count as mentions through MentionsMe (DiscordCache.cs:1429). |
| Badge adds pending friend requests | Missing | web @1830630 `+ dH([RelationshipStore, GameRelationshipStore])` (mod 912309 `getPendingCount()+getPendingIncomingCount()`) | DiscordCache.cs:775 | |
| Badge dot when there are unreads but no mentions | Missing | web @1830630 (`0===r&&hasAnyUnread&&!disableUnreadBadge → -1`) | TaskbarBadge.cs:20-38 (count only) | |
| "Enable Unread Message Badge" effect | Different | web @1830630: the setting removes only the dot | MainWindow.Discord.cs:40-41 | Discord keeps showing the mention count when the setting is off. Exo shows 0. |
| Incoming call ring sound | Different | web @2169551 (`call_ringing` loops while `hasIncomingCalls()`, stops on answer or decline) | DiscordSession.Voice.cs:183-194 | Exo plays the ring once. |
| Incoming call flash/attention | Missing | web @1830630 (`bounceDock("critical")`, title flash "Incoming Call" ×50) | DiscordSession.Voice.cs:194 | Exo shows a Windows notice ("Incoming call / Open Discord in Exo to join.", not Discord's copy) and never flashes the taskbar for a call. |
| New forum post notifications (THREAD_CREATE) and channel "New Posts Created" toggle | Missing | mod 592329 THREAD_CREATE; 037809489270256b.js mod 288104 `new-forum-threads-created` | none | |
| Stage live, scheduled event starting, reaction, and reminder-due notifications | Missing | mod 592329 (STAGE_INSTANCE_UPDATE, GUILD_SCHEDULED_EVENT_UPDATE, REACTION_NOTIFICATION_SENT, MESSAGE_REMINDER_DUE) | none | Exo's reaction setting (AppPages.tsx:37) saves the account option but Exo never shows reaction notifications. |
| Text-to-Speech Notifications (ttsType) | Missing | mod 803224 `ttsType`; 73f98035cbc74893.js TEXT_TO_SPEECH_NOTIFICATIONS | AppPages.tsx (only "Allow playback and usage of /tts command") | |
| User Notifications page toggles | Different | 73f98035cbc74893.js (DESKTOP_NOTIFICATIONS, ENABLE_UNREAD_MESSAGE_BADGE, sounds, "A server I'm in is heating up!", "A server has an upcoming event", Mobile Notification Delay) | AppPages.tsx:8-50 | Exo has the main toggles. It lacks the upcoming-event, server-heating-up, TTS and mobile-delay rows. |
| Server **Notification Settings** modal | Missing | 046406f7a5feb3e2.js mod 585617 (`eI`: Mute {server} + Mute duration, Server Notification Settings, Suppress @everyone and @here, Suppress All Role @mentions, Suppress Highlights, Mute New Events, Mobile Push Notifications, override picker, Notification Overrides table) | Sidebar.tsx:141-200 ServerHeader (no entry point) | Exo reads suppress_everyone/suppress_roles (DiscordCache.cs:1617-1619) but has no UI to change them. Needs host. |
| Notification Overrides list (per channel or category: All/Mentions/Nothing/Mute, remove ×) | Missing | mod 585617 `eM`/`ee` | none | |
| Guild-context "Notification Settings" submenu (radio levels + suppress checkboxes + Mobile Push) | Missing | 11c3e58c7aa81faa.js mod 237600 (`guild-notifications`, `suppress-everyone`, `suppress-roles`, `suppress-highlights`, `mobile-push`) | Sidebar.tsx:336-343 (no context menu on rail icons) | |
| Mute Server (15 m / 1 h / 3 h / 8 h / 24 h / Until I turn it back on) | Missing | guild-context mod 507238 (`mute-guild`); mod 769591 `b8` | none | Exo honors a server mute set elsewhere (DiscordCache.cs:853) and dims the icon (Sidebar.tsx:339). |
| Hide Muted Channels | Missing | guild-context mod 153594 (`hide-muted-channels`); mod 543465 `isGuildCollapsed` | Sidebar.tsx:378-416 (muted channels always listed) | |
| Channel-context "Mute Channel" with durations submenu | Fixed | 58c5cd79a16c8073.js mod 995102 (submenu: For 15 Minutes … Until I turn it back on; unmute shows "Muted until" subtext) | Sidebar.tsx:506 | Fixed (497ec0f): Mute Channel submenu with Discord's durations; Unmute shows until when. Exo's item only toggles and has no durations. The host already takes `minutes` (WebHostBridge.Discord.cs:847-852). |
| Channel-context "Notification Settings" submenu (Use Category/Server Default with subtext, All Messages, Only @mentions, Nothing; none for voice; stage: All Live Stages) | Missing | 037809489270256b.js mod 288104 (`channel-notifications`) | Sidebar.tsx:467-516 ChannelMenu | The header bell has the same choices (ChatHeader.tsx:231-240). The context menu has none. |
| Category context menu (Mute Category, Notification Settings) | Missing | 329d053ebb070bdc.js mod 22496 | Sidebar.tsx:392 (category row has no onContextMenu) | |
| Header bell popout (Mute Channel + levels) | Different | 4427b1a9b9dff431.js mod 187360 → aa865ad16b16d430.js mod 461678 | ChatHeader.tsx:199-251 | Three differences. (1) Discord nests the durations under "Mute Channel"; Exo lists "Mute for 15 Minutes"… flat. (2) Exo's "Use Default" should be "Use Category Default" or "Use Server Default". (3) Discord shows a bell-slash when muted **or** when the level isn't All Messages, and Shift+click toggles mute. Exo shows bell-slash only when muted and has no Shift+click. |
| Thread "Notification Settings" (All / Only @mentions / Nothing; disabled until joined) | Different | 46399b368d3ad2ca.js mod 375500 (`thread-notifications`, `setNotificationSettings(thread,{flags})`) | ChatHeader.tsx:52 (Bell on threads) → WebHostBridge.Discord.cs:853-857 → DiscordRest.cs:419 | Exo writes a guild `channel_overrides` entry for the thread id. Discord reads thread-member flags, and Exo's own rule ignores the thread's override (DiscordCache.cs:1468-1470), so the setting does nothing. |
| "@Mentions + Unreads" option | Missing | mod 288104 / mod 585617 (new-notifications experiment `useNewNotifications`) | — | Behind an experiment. Note only. |
| Default Notification Settings (server-wide default) | Match | 23TVhl "Default Notification Settings" | ServerSettings.tsx:275-280 | |

#### Safe fix candidates
- **Done (497ec0f).** **Channel context menu** (`ui/src/components/discord/Sidebar.tsx` ChannelMenu ~467-516): replace the Mute toggle with a "Mute Channel" submenu that reuses `MUTE_FOR` from ChatHeader.tsx:199 and `host.discordChannelNotify(id,{muted,minutes})`, and show "Muted until …" on Unmute. Add a "Notification Settings" submenu (Use Category/Server Default with the inherited level as subtext; All Messages; Only @mentions; Nothing) through `host.discordChannelNotify(id,{level})`. Discord shows no notification submenu for voice channels (mod 288104 returns null for GUILD_VOICE). UI-only.
- **Category context menu** (Sidebar.tsx:392): add `onContextMenu` with Mute Category (durations) and Notification Settings, using the same RPC with the category id. UI-only, but first check that `Channel()` / `GuildOf` in WebHostBridge.Discord.cs accepts a category id.
- **Header bell** (ChatHeader.tsx:203-246): use Discord's icon rule (`muted || (level ?? channel.notifyDefault) !== 0` → BellOff), add Shift+click to toggle mute, and nest the durations under "Mute Channel". UI-only. The exact "Use Category Default" vs "Use Server Default" label needs the channel's parent in the DTO; that is one field in DiscordCache's channel list, so it needs host.
- **Hide the header bell on threads** (ChatHeader.tsx:52) until thread settings use the thread-member endpoint, because today it saves a setting that has no effect. UI-only.
- **Selected-channel sound** (`ExoLauncher/Services/DiscordClient/DiscordSession.Settings.cs:204-209`): skip when DND, when the author is blocked, or when the channel is muted or set to Nothing, as Discord's `z` in mod 176154 does. A small host change; no RPC.
- **Notification text** (`DiscordCache.cs` Notice ~869-886): "Uploaded {filename}", "Sent a sticker: {name}", "Sent a voice message" (flags & 1<<13), "[poll] {question}", embed title+description, "Forwarded a message", and resolved `<@&role>`/`<#channel>`. Title "Author (#channel, Category)" (DiscordNotifier.cs:69-71). A small host change; no RPC.
- **Badge** (`MainWindow.Discord.cs:40-41`, `DiscordCache.Badge`): keep the count when "Enable Unread Message Badge" is off (Discord only drops the dot), and add incoming friend requests (relationship type 3). A small host change.
- **Ring loop** (`DiscordSession.Voice.cs:183-194`, `DiscordSounds`): loop `call_ringing` until the ring list drops you, and flash the taskbar. Needs host (DiscordSounds has no loop/stop today).

#### Proposals (large/risky)
- A server Notification Settings modal and guild-context submenu. Add a `discord.server` action `guildNotify` that PATCHes `users/@me/guilds/{id}/settings` with `muted`, `mute_config`, `message_notifications`, `suppress_everyone`, `suppress_roles`, `notify_highlights`, `mute_scheduled_events`, `mobile_push` and `hide_muted_channels`. Expose the cached flags in `discord.channels`. Then build the modal from mod 585617 (`eI`) and its override table.
- Thread notifications done properly: cache THREAD_MEMBER_UPDATE / thread `member.flags` and `muted`; change `NotifyLevel` to Discord's mod 607508 rule; save through `PATCH channels/{id}/thread-members/@me/settings`.
- DM/group mute through `PATCH users/@me/guilds/@me/settings` channel_overrides, with Mute @user / Mute Conversation menus.
- The other notification types (new forum posts, event start, stage live, reactions, reminders), and Focus Mode/quietMode honored from the account setting.

### Inbox

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Inbox popout with tabs | Different | web mod 938483 (InboxPopout); 26ae78586eb25251.js mod 173842 `su`: tabs Unreads, Mentions, Bookmarks, Reminders ({count}), Scheduled | Inbox.tsx:9-35 | Exo has For You, Unreads and Mentions. |
| For You tab | Different | web mod 938483: `(y===GAME_INVITES\|\|y===FOR_YOU) && setTab(UNREADS)` | Inbox.tsx:197-247 | Discord's current client has retired For You and redirects it to Unreads. Exo still shows it, never acks items (`NOTIF_CENTER_ITEMS_ACK`), and leaves the "New" dot on. |
| Bookmarks / Reminders / Scheduled tabs | Missing | mod 173842 (`o.Y2.BOOKMARKS/REMINDERS/SCHEDULED`) | none | Needs host (saved messages / reminders / scheduled messages APIs). |
| Default and remembered tab | Different | web mod 938483 (`settings.inbox.currentTab`, default UNREADS, saved in the account settings proto) | Inbox.tsx:11-15 | Exo stores the tab in localStorage and defaults to Mentions. |
| Ctrl+I opens the Inbox; Ctrl+Shift+E marks the top item read | Missing | mod 938483 `TOGGLE_INBOX`; mod 173842 tip text | ChatHeader.tsx:59 | |
| View Friend Requests button (with count) in the header | Missing | mod 173842 `e6` | Inbox.tsx:24-31 | |
| Unreads: Mark All as Read (confirm "Mark all as read?" / "This will mark all your unmuted channels as read…"; Shift+click skips the confirm) | Missing | mod 173842 `tv`/`tI` | Inbox.tsx:112-148 | |
| Unreads: per-channel Mark As Read / Mark Server As Read | Match | mod 173842 `tF` | Inbox.tsx:122-129,141,174 | Exo groups channels under their server header. Discord lists channel cards and shows a server mark only when that setting is on. |
| Unreads: per-channel Notification Settings bell (recents-notifications: Mute Channel, Mute Server, levels; threads: recents-thread-notifications) | Missing | mod 173842 `tz` → aa865ad16b16d430.js mod 461678 (`includeGuildMute`) | Inbox.tsx:168-175 | |
| Unreads: Expand/Collapse, "Messages older than 10 days" and "Notifications disabled" dividers, Ctrl+Z undo of a mark-read | Missing | mod 173842 (`t_`, dividers, `undoMarkChannelRead`) | Inbox.tsx:150-189 | Exo shows the three newest unread messages per channel. |
| Unreads: empty state | Different | mod 173842 `tj` "You're all caught up!" + Ctrl+I tip | Inbox.tsx:135 "You're all caught up." | |
| Mentions: Filter menu (navId mentions-filter) with Include @everyone mentions, Include @role mentions, Include all servers (hidden in DMs) | Different | mod 173842 `ta` | Inbox.tsx:77-81 | Exo uses toggle chips labelled "@everyone and @here", "Role mentions" and an inverted "This server only". |
| Mentions filters remembered | Missing | web mod 409686 (localStorage `recentMentionFilterSettings`, default ALL_SERVERS/everyone/roles) | Inbox.tsx:45-47 | Exo resets the filters on every open. |
| Mentions: Jump to Message | Match | mod 173842 `sy` | results.tsx:28-45 | |
| Mentions: remove (X) | Different | mod 173842 `sy`: "Mark As Read" (checkmark) → `deleteRecentMention` | results.tsx:46; Inbox.tsx:71-74 → DiscordRest.Inbox.cs:73 | Same endpoint. Exo labels it "Remove from Inbox" with ×. |
| Mentions: paging | Match | mod 173842 `sp` ("Load more messages") | Inbox.tsx:82 (scroll) → DiscordRest.Inbox.cs:68-70 | |
| Mentions: empty state | Different | "You made it through everything!" / "Whenever someone mentions you it will be saved here for 7 days." | Inbox.tsx:84 "You're all caught up." | |
| New mentions arrive live while the Inbox is open | Missing | web mod 409686 (MESSAGE_CREATE adds to the list) | Inbox.tsx:55-70 (loads once) | |
| Inbox item right-click (notifications-inbox-message-context): Mark As Read / Mark Unread, Mute Channel/Thread, Notification Settings | Missing | de6fd105d31bc271.js mod 594005 | results.tsx:24-51 | |

#### Safe fix candidates
- **Inbox.tsx**: default the tab to `'unreads'`. Rename the Mentions toggles to "Include @everyone mentions" and "Include @role mentions", replace "This server only" with "Include all servers" (on by default), and persist the three values in localStorage as Discord's `recentMentionFilterSettings` does. UI-only.
- **results.tsx:46**: relabel the × as "Mark As Read" with the check icon. UI-only.
- **Mark All as Read** on Unreads (Inbox.tsx Unreads): a header button with Discord's confirm copy, where Shift+click skips the confirm, calling the existing `host.discordMarkRead(guildId)` for each server in the list. UI-only. It covers only servers among the ≤50 listed channels (DiscordCache.Unreads(50)).
- **Empty states**: copy Discord's strings for Unreads and Mentions. UI-only.
- **Ctrl+I** toggles the Inbox in ChatHeader.tsx. UI-only.
- **View Friend Requests** in the Inbox header, counting `host.discordFriends()` entries of kind `incoming` and opening Friends › Pending. UI-only, provided `openDiscord` can open the Friends page.
- **For You**: once these land, delete the For You tab (Inbox.tsx:197-247), `discord.forYou`, and `DiscordRest.ForYouAsync`. Discord no longer shows it (AGENTS.md: delete, don't flag).

#### Proposals (large/risky)
- Bookmarks, Reminders and Scheduled tabs (saved messages, reminders, scheduled messages): new host APIs.
- Unreads rebuilt as Discord's channel cards: collapse, undo, and the per-channel bell (which needs guild mute from the Notifications proposal).

### Moderation

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Timeout: menu item opens a dialog | Different | 300785a1abdd94ea.js mod 291247 (`timeout` "Timeout {user}" → modal; Ctrl+click = 60 s at once) | UserMenu.tsx:234-239 | Exo shows a submenu of durations with no dialog and no Ctrl+click. |
| Timeout dialog: Duration buttons and Reason | Missing | a24c3a4f175accbb.js mod 631963 (60 SEC … 1 WEEK; Reason placeholder "Enter a reason. This will only be visible in the Audit Log and will not be shown to the member."; Cancel/Timeout) | UserMenu.tsx:188-189,236 | The host already accepts `reason` for timeouts (WebHostBridge.DiscordAdmin.cs:167,186-193). |
| Remove Timeout | Different | mod 291247 `removetimeout` "Remove Timeout From {user}" (opens a confirm) | UserMenu.tsx:237 (inside the submenu, no confirm) | |
| Kick dialog with Reason for Kick | Match | 11e7e738a4527ce0.js mod 547166 | UserMenu.tsx:323-331 | Copy differs. Discord's title uses the username; its body reads "Are you sure you want to kick @{name} from the server? They will be able to rejoin again with a new invite." Exo says "…from {server}? They will be able to re-join…". Discord also shows an app-specific modal for bots. |
| Ban dialog: Reason for Ban (Suspicious or spam / Compromised / Breaking server rules / Other) | Different | 2883b701d34b94fb.js mod 333179 (no reason preselected; "Ban reason must be provided"; title "Ban @{username}?") | UserMenu.tsx:279-282,295,332-347 | Exo preselects the first reason and titles the dialog "Ban {name}?". |
| Ban: Delete Message History choices (default Previous Hour) | Match | mod 333179 `y()`, default `y()[1]` | UserMenu.tsx:279-281,296 | |
| Roles submenu (assignable roles checkable; held unmanageable roles shown disabled) | Match | 23d9454fc4126829.js mod 180798 | UserMenu.tsx:214-224 | Without Manage Roles, Discord still shows a read-only "Roles" list. Exo hides it. |
| Server Mute / Server Deafen / Disconnect | Match | mod 291247 (`voice-mute`, `voice-deafen`, `voice-disconnect`) | UserMenu.tsx:231-233; WebHostBridge.DiscordAdmin.cs:204-217 | |
| Move to (voice) | Match | mod 252726 `voice-move` | UserMenu.tsx:225-229 | |
| Verify Member / Unverify Member (onboarding servers) | Missing | mod 291247 `verify` (member flags BYPASSES_VERIFICATION) | none | Needs host. |
| Remove {user} from Thread / Post; "Kick/Ban {user} from Server" wording in threads | Missing | 1230ee844693086c.js mod 175269 (`remove`, ThreadUserContextMenu) | none | Needs host (`DELETE channels/{id}/thread-members/{user}`). |
| Report to mod channel (message menu "report-to-mod") | Missing | 727ee9421e324cee.js @19724 `report-to-mod` | Message.tsx (no item) | |
| Mod View ("Open Mod View" from Members) | Missing | 7c8888c5fd059b8f.js @28087 | serverPages.tsx:486-495 (View profile + More only) | |
| Members page columns (Name, Member Since, Joined Discord, Join Method, Roles) | Match | 7c8888c5fd059b8f.js | serverPages.tsx:390-394,458-484 | |
| Members page: Signals column | Missing | 7c8888c5fd059b8f.js ("Signals", unusual DMs / timed out / spam / quarantine) | serverPages.tsx:392-393 | |
| Members page filters: Member Safety Flags (Unusual DM Activity, Timed Out, Unusual Account Activity, Quarantined), Member Since / Joined Discord date ranges, role filter | Missing | 7c8888c5fd059b8f.js @14329 (`toggle-require-*`) | serverPages.tsx:337-415 (search + sort only) | Needs host (member-search filters). |
| Members page: search, sort, paging | Match | 7c8888c5fd059b8f.js ("Search by username or id", "Sort") | serverPages.tsx:278-283,365-410 | |
| Members page: multi-select + Ban Members (bulk) | Missing | 7c8888c5fd059b8f.js ("Ban Members", "Clear selection") | none | |
| Prune: Last Seen 7/30 days | Match | 7c8888c5fd059b8f.js @38375 | serverPages.tsx:418-456 | |
| Prune: "Also include members with these roles" | Missing | 7c8888c5fd059b8f.js @38375 | serverPages.tsx:418-456 | Needs host (`include_roles`). |
| Raid protection / Security Actions (Pause Invites, Pause DMs, raid alert bar, Mark as Resolved, Report Raid) | Missing | guild-context mod 664967 `server-lockdown`; 7c8888c5fd059b8f.js strings | none | |
| Safety Setup: Raid Protection and CAPTCHA, DM and Spam Protection, Activity Alerts, Remove risky permissions, Warn members before outbound links | Missing | keys 53eF5o, O0SfFy, Qp98s9, +vaUlM, EkSAHZ | ServerSettings.tsx:301-318 (Verification Level, Sensitive content filters, 2FA only) | |
| Community Activity Alerts (In-app alerts / Push notifications) | Missing | 046406f7a5feb3e2.js mod 585617 `eg`/`eO` | none | |
| AutoMod rule kinds (Mention Spam, Suspected Spam, Commonly Flagged Words, Custom Words, Member Profile Names) | Match | strings pX7i6n, puF/Os, LnGhZv, ffR2cM, q1L2v8 | settingsAutomod.tsx:13-21 | |
| AutoMod action names | Fixed | 76693a2664689121.js mod 677420: "Block message", "Send alert", "Timeout member", "Block member interactions" (not editable) | settingsAutomod.tsx:22 | Fixed (9814cda). Exo uses "Send an alert", "Time out member", "Block member from using text, voice and other interactions". |
| AutoMod exempt roles/channels, custom response, alert channel, timeout length | Match | mod 677420 helper texts | settingsAutomod.tsx:201-246 | |
| Audit Log: Filter by User / Filter by Action | Match | 14d638c12241018a.js @224898 | settingsModeration.tsx:210-215 | Discord's user list is the server's moderators, searchable ("Search Members"). Exo lists only the people in the loaded entries. |
| Audit Log: action list | Different | 71809ee872fe97e1.js `ACTION_FILTER_ITEMS` (69 entries) | settingsModeration.tsx:137-153 | Exo lacks Move Channel, Move Role, the Onboarding and Server Guide entries, Start/Update/End Stage, Update Command Permissions, Feature/Remove Item on Home, the three Soundboard Sound entries, and Create/Delete Voice Channel Status. Discord calls three of Exo's entries "Delete Messages", "Cancel Event" and "Cancel AutoMod Rule". |
| Audit Log: change details | Different | 71809ee872fe97e1.js (per-key strings such as "Set timeout for user until…", "$bMuted them", "manually verified them") | settingsModeration.tsx:163-176 | Exo's wording is generic. |
| Audit Log: empty state | Different | "No Logs Yet" / "Once moderators begin moderating, you can moderate the moderation here." | settingsModeration.tsx:218 "Nothing here yet." | |
| Bans: list, ban reason, Revoke Ban | Match | 14d638c12241018a.js @419776 (modal 392516) | settingsModeration.tsx:263-322 | |
| Bans: search | Different | @419776: "Search Bans by User Id or Username" + Search button (server-side `searchGuildBans`), 100 per page | settingsModeration.tsx:283-286,270 | Exo filters on the client over ≤10k loaded bans (WebHostBridge.DiscordAdmin.cs AdminBansAsync). |
| Bans: title and empty state | Different | "Server Ban List"; "No Bans" / "You haven't banned anybody... but if and when you must, do not hesitate!" | settingsModeration.tsx:281,289 ("Bans", "No bans. Whew.") | |

#### Safe fix candidates
- **Timeout dialog** (`ui/src/components/discord/UserMenu.tsx`): make "Timeout {username}" open a PersonDialog kind `timeout` with Discord's six duration buttons and a Reason textarea using Discord's placeholder, then call `admin('timeout',{minutes,reason})`. The host already reads `reason`. Make "Remove Timeout From {username}" a top-level item with a confirm. UI-only.
- **Ban dialog** (UserMenu.tsx:295,332): start with no reason selected, block Ban until one is chosen ("Ban reason must be provided"), and title it `Ban @{name}?`. UI-only.
- **Kick dialog** copy (UserMenu.tsx:325-326): use Discord's body string and the username in the title. UI-only.
- **Roles submenu**: show a read-only list when `!can.roles` (UserMenu.tsx:214). Person data already carries `roles`/`choices`. UI-only.
- **Audit Log** (settingsModeration.tsx:137-153,218): add the missing actions (Discord's numeric codes), fix the three labels, and use Discord's empty-state copy. UI-only.
- **Bans copy** (settingsModeration.tsx:281,285,289): title, placeholder and empty state. UI-only.
- **AutoMod action names** (settingsAutomod.tsx:22). UI-only.
- **Verify/Unverify Member**, **Remove from Thread**, **Prune include roles**, **member safety filters/Signals**: each needs host.

#### Proposals (large/risky)
- Member Safety: member-search filters (safety flags, join and account date ranges, roles), a Signals column, multi-select bulk ban (`POST guilds/{id}/bulk-ban`), and Mod View (member panel with recent messages, roles, permissions and actions).
- Raid protection: Security Actions (incident actions: pause invites/DMs), the raid alert bar and Mark as Resolved, plus Safety Setup's Raid Protection and CAPTCHA and DM and Spam Protection sections.
- Report to mod channel (message menu → mod report thread), with resolve-flag actions in the Kick/Ban/Timeout modals as Discord's `modReportId` path does.

### Profiles and popouts

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Popout banner (per-server first, accent color fallback) | Match | f040b784d95d6d22.js `eF` (b.A banner) | profileParts.tsx:81-88; extras.tsx:83 | |
| Popout avatar with status | Match | f040b784d95d6d22.js `eF` (R.A) | profileParts.tsx:91-106 | Discord: a click opens "View Profile Picture" when there is an avatar, else the full profile. Exo always opens the full profile. |
| Display name, username, pronouns, badges | Match | f040b784d95d6d22.js `eb` (eh.Ay) | profileParts.tsx:109-124 | |
| Custom status | Different | f040b784d95d6d22.js `ei.A` (bubble by the avatar, custom emoji included) | profileParts.tsx:112,120; DiscordCache.cs:1055-1066 | Exo shows it as text under the name, and custom (non-unicode) status emoji are dropped. |
| Bio | Different | web mod 442228 (heading hidden, clamped, "View Full Bio") | profileParts.tsx:157-162 | Exo heads it "About Me" and shows it in full. |
| Mutual friends/servers row | Match | f040b784d95d6d22.js `eA.A` (opens the modal tab) | profileParts.tsx:430-442; extras.tsx:92 | |
| Current activity (stream, live activity, voice channel cards; several activities) | Different | web mod 646986 ("Current activity", card types stream/live/voice) | profileParts.tsx:273-291; DiscordCache.cs:1063 | Exo shows only the first rich presence, with no stream or voice cards. |
| Spotify Play on Spotify / Listen Along | Missing | web @8275014 (PRESS_LISTEN_ALONG_ON_SPOTIFY_BUTTON) | profileParts.tsx:273-291 | |
| Join Activity / Ask to Join | Missing | strings 4i2vj+, Ckxb6j | none | |
| Roles section with Add Role / remove | Match | 6049fd192b7e0569.js mod 403369 (expand/collapse, allowEditing); 857ea1cbed43ed0b.js mod 465738 (Add Role picker, "Role" placeholder) | profileParts.tsx:195-268 | |
| Member Since in popout | Different | f040b784d95d6d22.js `eb` (none in the popout; it is in the DM sidebar and the full profile) | extras.tsx:95 | Exo adds it to the popout. |
| Note in popout | Different | d8294482a39d24ee.js @68341 (Note lives in the full profile, "Note (only visible to you)"); d49d09a557c85e4a.js (Add Note / Edit Note menu opens the full profile at Note) | extras.tsx:97; profileParts.tsx:294-311 | Exo shows the note in the popout and heads it "Note". |
| "Message @{name}" quick reply in popout | Missing | 6049fd192b7e0569.js mod 848674 → d6e6ed1d2616e771.js mod 784886 (sends the DM, opens it) | extras.tsx:100 ("Message" button) | |
| Own popout: Edit Profile | Different | 810e4989cb31874e.js mod 822775 (in a server: menu "Edit Per-server Profile" / "Edit Main Profile") | extras.tsx:99 | Exo always opens Settings › Profiles. |
| Popout overflow (…) | Different | f040b784d95d6d22.js `ee.yo` (has "View Full Profile") | extras.tsx:85-88 (opens the person menu) | Partly fixed (6937c04, 1a9e87e): More (the person menu) now has Start a Call, Add Friend / Remove Friend, Ignore and Block; the popout banner has the Add Friend button. Report User Profile, Invite to Server and Share Profile are still missing. Being fixed separately. Exo's overflow is the person menu: Profile, Mention, Message, nickname, Apps, mod items, Copy Username, Copy User ID (UserMenu.tsx:149-159). It lacks Add/Edit Note, Start a Call, Invite to Server, Add/Remove Friend, Ignore, Block and Add Friend Nickname (1230ee844693086c.js mod 175269). |
| Bot profile: Add App / Open App, Store, popular commands | Missing | 273a8bcc775acc98.js (bot popout); 332c2dd450c6bc32.js mod 570314 ("Add App"/"Open App", Copy Add App URL) | profileParts.tsx:118 (only the "App" tag) | |
| Bot "Created On" instead of Member Since | Different | e135f1d825d5d1d5.js @90414 (`t.bot?"Created On":"Member Since"`) | profileParts.tsx:167-183 | |
| "Originally known as {legacyUsername}" | Missing | key bhrgkA | lib/discord.ts:383 (typed, never rendered) | |
| Full profile modal tabs | Different | d8294482a39d24ee.js @152900: Board (widgets), Activity (always), Wishlist, Mutual Friends ({n}), Mutual Servers ({n}); web mod 518477 tab enum | DmProfile.tsx:183-189 | Exo has About Me / Activity (only while active) / Mutual Friends / Mutual Servers. Discord's Activity tab also shows Recent activity. |
| Full profile left column (Bio, Member Since, Friends Since, Roles, Connections, Note) | Different | d8294482a39d24ee.js @147152 | DmProfile.tsx:191-211 | Exo puts these under an "About Me" tab and lacks Friends Since. |
| Full profile: Message, Add Friend / Pending / Accept / Ignore / Remove Friend, More | Match | d8294482a39d24ee.js (`profileButtons`) | DmProfile.tsx:118-126,146-180 | |
| Connections (marks, verified, links) | Match | d8294482a39d24ee.js "Connections" | profileParts.tsx:361-388 | |
| Mutual Servers list (opens server, shows nick) | Match | d8294482a39d24ee.js MUTUAL_GUILDS | profileParts.tsx:396-411 | |
| Mutual Friends list | Match | d8294482a39d24ee.js MUTUAL_FRIENDS | profileParts.tsx:414-427 | |
| DM side profile panel: banner, avatar, names, bio, Member Since, "Mutual Servers — n" / "Mutual Friends — n" toggles, View Full Profile | Match | e135f1d825d5d1d5.js @73906-76500 (SIDEBAR) and @88500-92500 (`oN` section header "— count") | DmProfile.tsx:18-75 | Discord heads the bio "Bio", not "About Me". Discord's sidebar also has widgets, wishlist and popular bot commands. |

#### Safe fix candidates
- **Popout** (`ui/src/components/discord/extras.tsx:91-101`): remove Member Since and Note (Discord keeps both in the full profile), and replace the Message button with a "Message @{name}" input. It opens the DM through the existing `host.discordFriend('dm',{userId})`, sends with the existing send RPC, then closes, matching mod 784886. UI-only (existing RPCs).
- **Bio heading** (profileParts.tsx:161): in the popout, no visible heading plus a clamp with "View Full Bio" (opens the full profile); "Bio" in the DM panel and full profile. UI-only.
- **Bots**: "Created On" for `user.bot` in MemberSince (profileParts.tsx:171). UI-only.
- **Full profile** (DmProfile.tsx:183-211): always show the Activity tab (empty state "{name} doesn't have any activity to share here"), and head the note "Note (only visible to you)". UI-only.
- **"Originally known as"** from `profile.legacy_username` (already typed at lib/discord.ts:383). UI-only.
- **Edit Profile in a server**: a two-item menu (Edit Per-server Profile / Edit Main Profile), provided Exo's Settings › Profiles can open on the server-profile view. Probably UI-only; check `requestDiscordSettings`.
- **Friends Since**: needs host (relationship `since` not exposed in DiscordCache).

#### Proposals (large/risky)
- Activity cards: all activities, plus stream and voice-channel cards with Spotify Play/Listen Along (needs Spotify connection and sync handling) and Join/Ask to Join (needs host).
- Bot profiles: Add App / Open App and popular commands (needs the profile `application` and install flow).
- A full profile layout matching Discord's V2 (left info column + right tabs, Recent activity). Board/Wishlist are collectibles/Shop and stay out of scope.

## Area: voice and video, friends, members, presence and apps

Branch `discord-audit` as of commit 8166098. This audit was read-only and made no Discord requests.

Discord references come from the bundle snapshot. They are given as `file.js mod N`, or as `@offset` for code inside the large web module. «Text» is a dehashed string key, and `navId` is a context-menu id. Exo references are `path:line` as of commit 8166098. `web.js` means `web.d41b6bfa9f8fcb8b.js`.

Status values:
- **Match** means the same feature, label and behavior.
- **Different** means Exo has the feature but it differs in the way noted.
- **Missing** means I searched Exo's labels, RPCs and behavior and found nothing.

---

### 1. User panel and voice connection panel

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Mute button (user panel) | Match | web.js mod 829773 audio button «Mute»/«Unmute» | ui/src/components/discord/Sidebar.tsx:227-230 | Toggles self mute. Unmuting also undeafens (DiscordSession.Voice.cs:113-128). |
| Deafen button (user panel) | Match | «Deafen»/«Undeafen» (same module) | Sidebar.tsx:231-234 | |
| Right-click mute/deafen opens `audio-device-context` | Missing | web.js mod 829773 and fc732ddf2e0ef476.js mod 829773 (`navId:"audio-device-context"`). Items: Input Device / Output Device submenus, Input Profile (Voice Isolation / Studio / Custom), Input Volume, Output Volume, «Input Level» meter, «Push to Talk» (`input-mode`), «Deafen», Spatial Audio. | Sidebar.tsx:227-234 has no onContextMenu, and neither does voice.tsx:237-240 | The host already serves this menu's data through `discord.audio` (WebHostBridge.Discord.cs:238-274). Its own comment reads "The user panel's device menus". |
| «Input Options» / «Output Options» carets next to mic and headphones | Missing | keys fRzCbB, aA4Vce | Sidebar.tsx:227-234 | Same menu as the row above. |
| Own button states: «Server Muted», «Server Deafened», «Suppressed» (disabled, red) | Missing | web.js mod 829773 region (labels «Server Muted», «Server Deafened», «Suppressed») | Sidebar.tsx:227-234. `VoiceStatus` sends only `muted`/`deafened` (self) at DiscordSession.Voice.cs:35-40 | Needs host: report self `mute`/`deaf`/`suppress` from the VOICE_STATE_UPDATE that DiscordSession.Voice.cs:150-158 already receives. |
| Settings gear | Different | «User Settings» (cduTBL) | Sidebar.tsx:235 `aria-label="Discord settings"` | Label only. |
| Avatar/name opens account popout (Edit Profile, Set Status submenu, custom status, Switch Accounts, Copy User ID) | Different | web.js @6549049 (popout with `navId:"set-status-submenu"`, `"switch-accounts-submenu"`, «Edit Profile», «Copy User ID») | Sidebar.tsx:220 calls `requestDiscordSettings('account')` | Exo opens Discord settings > Account instead of a popout. |
| Line under your name | Different | Shows your custom status (emoji and text) when set, else the status | Sidebar.tsx:224, 243-250 (`statusLabel`) | Exo always shows the status word. |
| «Voice Connected» label | Match | web.js mod 917592 `getStatus` | voice.tsx:400 | |
| Connection state labels | Different | mod 917592: «Connecting», «Authenticating», «Waiting for Voice Server», «RTC Connecting», «Checking Route», «DTLS Connecting», «No Route», «RTC Disconnected», «Disconnected», and «Video Connected» when the camera is on | voice.tsx:400 shows only `Connecting…` / `Voice Connected` / `Voice` | There is no "Video Connected" state and no error states. |
| Click «Voice Connected» to open the RTC connection popout (graph, hostname, «Average ping», «Last ping», «Outbound packet loss rate», Debug) | Missing | web.js mod 978171 | voice.tsx:398-405 (plain text) | Needs host and Voice/. VoiceConnection.cs:168 only sets `_acked` on op 6 and measures no round-trip time. The Voice/ session owns this. |
| "channel / server" link that opens the call | Match | panel subtitle | voice.tsx:403-405; DiscordClient.tsx:331-339 | |
| Disconnect | Match | «Disconnect» (web.js @1670112 `vr`) | voice.tsx:407-410 | |
| Noise Suppression button in the panel (Krisp popout with «Mic Test») | Missing | web.js @1664000-1684000 (RTC panel strings «Noise Suppression», «Mic Test», «Powered by») | Only a settings toggle at settings/VoicePage.tsx:91-92 | Can be wired to the existing `discord.clientSettings` `noiseSuppression`. |
| Panel's second row: camera, Share Your Screen, Start An Activity, Open Soundboard | Different | web.js @1680415 («Share Your Screen», «Video», «Screen», «Open Soundboard») | VoicePanel at voice.tsx:393-416 has only status and hang-up. The buttons are only in the call bar (voice.tsx:233-247) | Not reachable from other channels while in a call. |
| E2EE lock and «View Verification Code» | Different | 479335 `secure-frames-user-verification`, 304006 `secure-frames-stream-verification` | voice.tsx:401 lock icon only | No codes to compare (DAVE lives in Voice/). |
| `rtc-channel` menu («Pop Out», «Show Voice Users») | Missing | 7065b345345011a1.js mod 576701 | none | |

### 2. Call / voice channel view

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Participant tile grid | Match | call grid | voice.tsx:187-199, 249-276 | |
| Speaking ring (not for people you muted) | Match | VoiceUserIcons and tile ring | voice.tsx:89, 195, 287 | |
| Focus / «Focus This Person» / «Unfocus This Person» | Missing | d4f2e592ceb5d6e1.js mod 984894 (`focus-video`, `selectParticipant`) | VoiceTile voice.tsx:280-299 has no click and no menu item | The UI keeps only grid state. |
| «Show Non-Video Participants» | Missing | d44a87b48eb80efc.js mod 854182 (`no-video-hide`) | voice.tsx:194-197 always lists everyone | Could be UI-only. |
| «Show My Own Camera» | Missing | 0f90dde439a66ca7.js mod 422229 (`self-video-hide`) | voice.tsx:283-285 always draws your local camera | Could be UI-only. |
| Pop Out (user, video or stream tile; whole call) | Missing | 374d6f4618e3e769.js mod 810845 «Pop Out User»/«Pop Out Video»/«Pop Out Stream»; rtc-channel «Pop Out» | none | Needs a second WebView window (see Proposals). |
| Full Screen | Different | «Full Screen»/«Exit Full Screen» on the call | voice.tsx:332-343, 379-381 | Exo offers it only on a watched stream, not on the call or a camera tile. |
| «Show Members» / «Hide Members» | Different | web.js mod 80051 (`toggleParticipants`) | voice.tsx:178-179, 374-378 | Exo shows it only while watching a stream. |
| «Show Chat» / «Hide Chat» | Match | keys 5KxXrK / nthdxB | voice.tsx:221-227; DiscordClient.tsx:381-393 | |
| «Invite to Voice» in the call header | Missing | 17364b8ada94d8f4.js mod 220995 | VoiceHead voice.tsx:216-230 | The UI could use the existing `host.discordChannel(id,'invite')` (Sidebar.tsx:501-504). |
| Not-joined view | Different | fbfa46cfef77bedb.js @114009: up to 5 faces, the channel name, «No one is currently in voice» / «{a} is currently in voice» / «{a} and {b} are currently in voice» / «{a}, {b}, and {n} are currently in voice», then «Join Voice» or a disabled «Channel Locked» | voice.tsx:188-193 (full tiles, `No one is here yet.`) and 208 (disabled `Join Voice`) | Wording, and "Channel Locked" when the user can't connect. |
| Join Voice / Join Video | Different | «Join Voice» (zSqdrS area). The camera button before joining reads «Connect to Video» (server) or «Join Video Call» (DM) (web.js mod 675991). «Join Video» (JZTA6C) appears unused in the bundle. | voice.tsx:208 shows only `Join Voice` / `Join Call` | There is no way to join with the camera on. |
| Camera button labels | Different | mod 675991: «Turn On Camera»/«Turn Off Camera», plus «Camera Unavailable», «You can’t stream into this channel.», «Video chat isn't available for more than {limit}» | voice.tsx:306-315 (On/Off only; errors go to the notes bar) | The on/off labels match. The unavailable, no-permission and limit states are missing. |
| Camera right-click `video-device-context` («Camera» list, «Video Settings») | Missing | web.js mod 329072 / 8b0d21a02e06e1f2.js | CameraButton voice.tsx:306-315 | Can be UI-only: `setDiscordPrefs({cameraId})` as at VoicePage.tsx:306, and `requestDiscordSettings('voice')`. |
| «Change Video Background» / «Preview Camera» modal (blur, backgrounds) | Missing | web.js mod 855187; «Always preview video» (3Ppr1h) | Only settings/VoicePage.tsx:275-311 (Test Video, camera choice) | No preview before going on camera and no background effects. |
| Mute / Deafen / Disconnect in the call bar | Match | call controls | voice.tsx:237-244 | |
| Voice user icons in the sidebar | Different | 3417ac5cbcf656c5.js @3714 (VoiceUserIcons) and web.js @1670112: «Muted by you», «Server Muted» (red), «Muted», «Server Deafened» (red), «Deafened», video icon («Video disabled by you»), LIVE, ringing, activity/game icons, stream preview on hover | voice.tsx:82-98 shows one mute or deaf icon | The host merges self and server mute into `mute`/`deaf` (DiscordCache.cs:902), and the channel DTO (624-626) drops `ServerMute`/`ServerDeaf`. `video` and `stream` are sent but VoiceMembers doesn't draw them. There is no "muted by you" distinction in the sidebar. |
| Voice channel user count / limit badge ("02 / 05") | Missing | 3e54ccc0d7ab05f8.js @31436 (padStart count / limit); aria «{userCount} of {limit} users» (web.js mod 963027) | Sidebar.tsx:420-432 | Needs host: `UserLimit` is parsed (DiscordCache.cs:41, 1589) but not sent by `Channels()` (DiscordCache.cs:578-626). |
| Voice channel status («Set Channel Status» / «Clear Channel Status», status line under the channel) | Missing | ee132f8c257db923.js mod 671483 («Status for {channelName}», «What are we chatting about?») | none | Needs host: the voice-status endpoint and the VOICE_CHANNEL_STATUS_UPDATE event. |
| «Hide Names» (voice channel context menu) | Missing | 21b4a7fc73e1a9cc.js mod 119357 (`hide-voice-names`) | ChannelMenu at Sidebar.tsx:467-517 is the same for voice and text | Can be UI-only. |
| «Open Chat» (voice channel context menu) | Different | mod 119357 `open-chat` | Show Chat toggle in the room (voice.tsx:221-227) | Not in the context menu. |
| Entrance sounds («Join Without Entrance Sound», «Set as Server/Global Entrance Sound») | Missing | ee132f8c257db923.js mod 217563; 66d1335ea635b0a6.js mod 710339 | none | Nitro feature. |
| AFK channel | Different | AFK voice state is `suppress`; tile and buttons show «Suppressed» | DiscordCache.cs:622 `afk` is used only to leave it out of the rail tooltip (Sidebar.tsx:280) | `suppress` is never read, so Exo shows no "can't speak" state. |
| Stage channels (speakers/audience, «Request to Speak», «Speak on Stage», «Move to Audience», «End Stage», «Join Stage») | Missing | ce97f42e830b2b0e.js mod 717965 («Request to Speak»); 73ea915464945905.js mod 844065; e92470f333daa136.js mod 237749; 21b4a7fc73e1a9cc.js mod 119357 (`end-stage`) | Type 13 is treated as voice (DiscordCache.cs `IsVoice` `type is 2 or 13`); voice.tsx draws it as a normal call | You join as audience and get no feedback. |
| Push to Talk indicator / quick toggle | Different | `audio-device-context` checkbox «Push to Talk» (`input-mode`) | Only settings/VoicePage.tsx:96-104 | No panel toggle or indicator. The PTT sounds exist (VoicePage.tsx:19-22). |
| Start An Activity / activity shelf | Missing | fbfa46cfef77bedb.js mod 686502; web.js mod 782091 («Start An Activity») | ui/src/lib/discordApps.ts:132-133 ("This app opens an Activity, which Exo does not run.") | See Proposals. |

### 3. Per-person voice menu in a call (`navId:"user-context"`)

Exo's menu lives in ui/src/components/discord/UserMenu.tsx. Discord's guild-voice variant is 265d2524365c96b7.js mod 107632, and its self/voice variant is 1e756429fdf20515.js mod 661455.

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| «User Volume» slider 0-200% | Match | 20b1bd41050bf79c.js mod 972432 | voice.tsx:121-131 | Uses the same curve and saves to the account proto. |
| Local «Mute» | Match | f9ce04957fe5d17d.js mod 198229 (`toggleLocalMute`) | voice.tsx:133 | |
| Self «Mute» / «Deafen» in your own menu | Match | mod 198229 (self branch) | voice.tsx:112-119 | |
| «Mute Soundboard» (per person) | Missing | mod 198229 `soundboard-sound-mute` | voice.tsx:121-135 | Needs host: DiscordUserSettings.cs:119 and 169-175 already round-trip `SoundboardMuted`, but there is no setter (DiscordSession.Settings.cs:101) and no filter (DiscordCache.cs:329 checks only the global mute). |
| «Disable Video» (per person) | Missing | mod 198229 `disable-video` | none | Hiding the tile can be UI-only. Saving bandwidth needs Voice/. |
| «Focus This Person» | Missing | mod 984894 | none | See section 2. |
| «Voice & Video Settings» (own menu) | Missing | 11c368a1d2df2f60.js mod 470743 | CallPersonItems voice.tsx:112-119 | UI-only: `requestDiscordSettings('voice')` (ui/src/lib/discord.ts:332). |
| «Show My Own Camera» / «Change Video Background» (own menu) | Missing | mod 422229 / web.js mod 855187 | none | See section 2. |
| «Server Mute» / «Server Deafen» / «Disconnect» | Match | 300785a1abdd94ea.js mod 291247 | UserMenu.tsx:231-233 | Discord also hides Server Mute and Deafen for a suppressed stage member. |
| «Move to» | Different | e14cf5c9e6ab3f57.js mod 252726: on yourself it lists channels you can Connect to (no Move Members needed) | UserMenu.tsx:210, 225-229; host sets `move = … && !me` (DiscordCache.Admin.cs:189) | You can't move yourself from the menu. |
| «Ring to Call» / «Ring to Voice» / «Stop Ringing» (one person) | Missing | c476187f1764e6b0.js mod 313998 | none | Needs host: `RingAsync` always sends `recipients: null` (DiscordRest.cs:175-180). |
| «Start a Call» | Fixed | 02088a593ed4c8dd.js mod 373397 | UserMenu.tsx:149-160 | Fixed (6937c04). Exo has only the DM header button. |
| «View Verification Code» | Missing | mod 479335 | none | DAVE is in Voice/. |
| Order of items | Different | Profile, Mention, Message, Start a Call, Ring, Note, Friend Nickname, then User Volume, then Mute, Mute Soundboard, Disable Video, Focus, ..., Roles, Move to, Pop Out, Show Non-Video, Show My Own Camera, then moderation | UserMenu.tsx:149-160 | Exo's subset is in roughly Discord's order. |

### 4. Streams (`stream-context`, `manage-streams`, Go Live `stream-options`)

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| «Watch Stream» | Match | 31a9e8620616a31a.js mod 744960 (`watch`) | voice.tsx:173-176, 292 | |
| «Stop Watching» | Match | mod 744960 | voice.tsx:177, 382 | |
| «Stream Volume» | Match | mod 972432 (STREAM context) | voice.tsx:371-372 (saved as audioStreams) | |
| Mute a stream | Different | mod 744960 checkbox «Mute» | voice.tsx:367-370 (`Mute Stream`/`Unmute Stream` button) | Label only. |
| «Pop Out Stream» | Missing | mod 810845 | none | |
| «Stream Attenuation» and «Stream Attenuation Strength» | Missing | mod 744960 (`sidechain-compression`) | none | Mixing happens in Voice/. |
| «Report Problem» | Missing | web.js mod 74329 | none | |
| Right-click a stream (`stream-context` menu) | Missing | mod 744960 | StreamStage voice.tsx:321-387 has inline buttons only | |
| Several streams at once («Stop Watching — {username}» per stream) | Different | web.js mod 848362 (`manage-streams`); `forceMultiple` in mod 810845 | voice.tsx:169-177; host `WatchStream` takes one user (WebHostBridge.Discord.cs:519-521) | Exo watches one stream at a time. |
| Stream preview thumbnail on a LIVE person | Missing | 3417ac5cbcf656c5.js @3714 (`previewUrl`) | voice.tsx:291 shows a LIVE badge only | |
| Own-stream menu: «Stream Quality» (Resolution / Frame Rate while live), «Share Audio», «Change Windows», «Stop Streaming», «Voice Settings» | Different | web.js mod 405916 | GoLivePicker.tsx:141-147 (Hide/Show Preview, Stop Streaming, live stats) | Exo can't change quality or window while live without restarting. |
| «Show My Screen Share» | Different | 573c89e605892ff7.js mod 666328 (checkbox) | GoLivePicker.tsx:144, 175 (`Hide Preview`/`Show Preview`) | "Hide Preview" is not a Discord string. |
| «Pause Preview When Discord Isn’t Focused» | Different | mod 744960 (setting) | GoLivePicker.tsx:162-168 always pauses when Exo isn't active | Exo has no toggle. |
| «Stop Streaming» | Match | mod 405916 | GoLivePicker.tsx:145, 176 | |

### 5. Go Live picker and camera

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Source tabs «Applications» / «Screens» | Match | keys HU3RFw / /iX8uz | GoLivePicker.tsx:224-227 | |
| «Devices» tab | Missing | keys +1h0k/, MhJ43A | none | |
| Live preview of the chosen source | Match | Go Live modal preview | GoLivePicker.tsx:266, 287-289 | |
| «Stream Mode» presets: «Smoother video ({resolution}, {frameRate}fps)», «Clearer text», «Adjust to content», «Custom» | Missing | 8154cf734a0b1a88.js mod 266536 (`navId:"stream-options"`, keys P2pjmy, G5O1Mz, 8tcFL5, m4jtlc) | GoLivePicker.tsx:291-292 (raw choices only) | |
| Resolution 720p / 1080p / 1440p / Source | Different | same module (`tS` list); label «Screen Resolution» (IG5n0X) | GoLivePicker.tsx:23-28, 291 (`Resolution`) | Same options, different label. |
| Frame rate 15 / 30 / 60 | Different | `tC` list; label «Frame Rate» | GoLivePicker.tsx:37-41, 292 (`Frame rate`) | Exo uses sentence case. |
| Nitro / server boost quality gating | Different | web.js mod 327649 (`canStreamQuality`, guild premium tier) with a Nitro upsell («Stream in HD resolution with Nitro») | GoLivePicker.tsx:23-41; GoLiveCapture.cs:20-30 (no premium check) | Exo lets any account send 1440p, Source or 60 fps. Whether to keep this is a policy decision. |
| Stream audio | Different | «Mute stream audio», «Share Audio»/«Share Stream Audio», «Audio Device» submenu | GoLivePicker.tsx:276-299 (`Stream sound` switch) | No audio device choice, and the label is not Discord's. |
| «Hide stream preview» (advanced) | Different | mod 266536 `stream-option-share-preview` | Done after going live (GoLivePicker.tsx:96-111) | |
| «Notify my friends in the server» | Missing | mod 266536 `stream-option-notify` | none | |
| Camera Turn On/Off | Match | web.js mod 675991 | voice.tsx:306-315; ui/src/lib/goLive.ts:253-260 | |
| Camera choice | Different | `video-device-context` | Settings only (settings/VoicePage.tsx:304-308) | |

### 6. Soundboard

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Soundboard button label | Different | «Open Soundboard» (yPH4xm, web.js mod 994778) | Soundboard.tsx:65 (`Soundboard`) | |
| Disabled while deafened, server muted or suppressed | Missing | web.js @1667374 («Soundboard disabled when deafened» / «…server muted» / «…suppressed») | Soundboard.tsx:65 is always enabled | The deafened case is UI-only. Server mute needs the host to report self server mute. |
| Categories: «Favorites», «Frequently Used», «Top Sounds in {guildName}», each server, «Discord Sounds» | Different | web.js mod 476254 (`navId:"soundboard-picker-categories"`) | Soundboard.tsx:72-83 (`This server`, `Discord`) | The host comment says "with Nitro, every server's", but it fetches only this server (WebHostBridge.DiscordMedia.cs:120, 132-135). |
| Search sounds («Search Results», «No sounds match your search») | Missing | mod 476254 | none | Can be UI-only. |
| Play a sound | Match | sound button «Play {emojiName} {soundName}» | Soundboard.tsx:55-61 | |
| Preview a sound (only you hear it) | Missing | «Preview {emojiName} {soundName}» (/8fYO5) | Soundboard.tsx:14-18 has `playHere` but no button for it | Can be UI-only. |
| Favorite / Unfavorite a sound | Missing | «Favorite {emojiName} {soundName}» (Y5DOs4); frecency proto `favoriteSoundboardSounds` (web.js @2101743) | none | Needs host: the same frecency proto as DiscordFavoriteGifs.cs. |
| `sound-button-context` («Edit Sound», «Set as Server/Global Entrance Sound», «Download Sound», «Copy Sound ID») | Missing | 66d1335ea635b0a6.js mod 710339 | SoundTile Soundboard.tsx:20-27 has no context menu | «Copy Sound ID» can be UI-only (Developer Mode). |
| «Add Sound» / «Upload sound for {guildName}» | Missing | mod 476254 | none (no soundboard page in Server Settings) | |
| Hearing others' sounds at Soundboard Volume; global Mute Soundboards | Match | soundboard settings | Soundboard.tsx:94-102; DiscordCache.cs:326-335 | |

### 7. DM calls

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| DM header voice call button | Different | «Start Voice Call» (focH1t) | ChatHeader.tsx:42-46 (`Start a voice call`; `In call` while in the call) | Label case. |
| «Start Video Call» | Missing | oCqlGG; friend-row `start-video-call` | none | Can be UI-only: join, then `startCamera()`. |
| Start call rings the others | Match | 373397 `openPrivateChannel({joinCall})` plus ring | voice.tsx:64-66; DiscordSession.Voice.cs:130-135 | |
| Incoming call UI | Different | 992e7cab2da9d461.js mod 417680: popout with ringing avatar, the name, «Incoming Call...» / «Incoming Video Call...», Decline, Join, join with video; Esc stops ringing | voice.tsx:419-427 (`{name} is calling`, Join, Decline) | Wording; no join-with-video; Esc does nothing. |
| Decline (stop ringing me everywhere) | Match | `stopRinging` | voice.tsx:71-74; DiscordSession.Voice.cs:137-143 | |
| «Join Call» to an ongoing DM call | Match | 0D/6Rz | voice.tsx:208; DiscordClient.tsx:414-428 | |
| People being rung shown as «Ringing…» | Missing | Xuzre8; participant `ringing` (mod 313998) | DmCall shows only people in the call (DiscordClient.tsx:424) | |
| Ringtone and toast | Match | incoming call | DiscordSession.Voice.cs:183-195 | |

### 8. Friends

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Tabs Online / All / Pending / Add Friend | Different | web.js @1248932: Online is shown only if someone is online, All only with friends, Pending only with requests (with a badge), «Suggestions» when there are any. Opens on Add Friend when you have no relationships. | FriendsPage.tsx:64-71 always shows all four | |
| «Suggestions» tab | Missing | web.js @1249903 («Friend Suggestions — {count}») | none | Needs host. |
| Blocked list | Missing | There is no Blocked tab now. The friends list says «Looking for accounts you've blocked or ignored? Go to settings» (web.js @1241870, Settings > Restricted users). | No list anywhere. The host tracks `_blocked` (DiscordCache.cs:128, 186, 363) for notifications only. | |
| Pending sections «Received — {count}» / «Sent — {count}» | Different | web.js @1245676 | FriendsPage.tsx:56, 81 (single `Pending — N`) | |
| «Clear all» (5 or more incoming) | Missing | web.js @1248932 and mod 507019 (confirm «Are you sure?») | none | |
| «View other requests» / «View spam requests» | Missing | web.js @1248932 | none | |
| «Online — {online}» / «All friends — {count}» | Match | web.js @1245676 | FriendsPage.tsx:56, 81 | |
| Search in the list | Match | per-section search | FriendsPage.tsx:77-80 | |
| Empty states | Different | «There are no friends online at this time. Check back later!», «There are no current friends. Click “Add Friend”…», «There are no pending friend requests…» | FriendsPage.tsx:110 | Wording. |
| Row «Message» button | Match | friend row | FriendsPage.tsx:94 | |
| `friend-row` More menu: «Start Video Call», «Start Voice Call», «Remove Friend» | Different | web.js mod 433552 @1229320 | FriendMenu FriendsPage.tsx:125-139 (Remove Friend only) | |
| Right-click a friend row (person menu) | Fixed | user-context | The row has no `data-dc-user` (FriendsPage.tsx:84-87) | Fixed (7bbe38b). One-attribute fix. |
| Accept / Ignore / Cancel a request | Match | «Accept», «Ignore», «Cancel» tooltips | FriendsPage.tsx:101-107 | |
| «Remove '{name}'» confirm | Match | 803664 | FriendsPage.tsx:115-119; DmProfile.tsx:172-176 | |
| «Block» (modal «Block {username}?» with «Ignore instead») / «Unblock» | Fixed | bf30656fe9f84d9a.js mod 239211; modal 262797 | none | Fixed (6937c04) in the person menu; host: PUT relationships/{id} {type:2}, Unblock = DELETE. Needs host: no block REST call (DiscordRest.cs:182-189). |
| «Ignore» / «Unignore» | Fixed | 399476 | none | Fixed (6937c04); host: PUT/DELETE relationships/{id}/ignore. Needs host. |
| «Add Friend Nickname» / «Change Friend Nickname» (modal «Friend Nickname», «Reset Friend Nickname») | Missing | 02088a593ed4c8dd.js mod 29293; de5f564d68f633f0.js mod 609422 | none | Needs host: the relationship's `nickname` is ignored (DiscordCache.cs:185) and there is no PATCH call. |
| Add Friend by username | Match | web.js mod 237309 | FriendsPage.tsx:145-173; WebHostBridge.Discord.cs:298-299 (strips `@`) | |
| Add Friend success and error text | Different | «Success! Your friend request to $b{discordTag} was sent.»; errors mapped by code: «Hm, didn’t work. Double check that the username is correct.», «You're already friends with that user!», «…is not accepting friend requests», «You've maxed out…» (web.js mod 395422 `vU`) | FriendsPage.tsx:154 (`Friend request sent to {name}.`); errors are the raw `Discord: <API message>` (DiscordRest.cs `ErrorLine`) | |
| Add Friend input placeholder | Different | «You can add friends with their Discord username» | FriendsPage.tsx:166 (`Their username`) | |
| Active Now column | Different | web.js @1192588 and @1218833: activity cards include «In a Voice Channel» and streams. Empty text: «When a friend starts an activity—like playing a game or hanging out on voice—we’ll show it here!» | FriendsPage.tsx:55, 197-245 (rich activities only; different empty line at 214) | |
| Mutual servers / mutual friends | Match | profile Mutual tabs | DmProfile.tsx:177-183; extras.tsx:92 | |
| «Message Requests» | Missing | keys 5IN9wR and others | `is_message_request` is used only to mute notifications (DiscordCache.cs:49, 1591). Requests sit in the normal DM list. | Needs host. |
| Add / Remove Friend in the person menu | Fixed | 803664 (`add-friend`, `remove-friend`) | UserMenu.tsx:149-160 (only in the full profile, DmProfile.tsx:146-178) | Fixed (6937c04). Can be UI-only (`host.discordFriend`). |
| «New Group DM» | Match | web.js `AU` «New Group DM» | FriendsPage.tsx:72 | |

### 9. Member list

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Hoisted role groups «{role} — {count}» | Match | member list groups | MemberList.tsx:43-44; DiscordCache.cs:1142 | |
| Online / Offline sections (offline dimmed) | Match | `online`/`offline` groups | DiscordCache.cs:1141; MemberList.tsx:47 | |
| Name in role color | Match | | MemberList.tsx:51 | |
| «APP» tag for bots | Match | | MemberList.tsx:52 | |
| Activity line | Different | web.js @2753186: game name with tooltip «Playing $b{game}», Spotify artist, custom status with emoji, activity icon | DiscordCache.cs:1157 gives `type==4 ? state : name` (no emoji, artist or icon); MemberList.tsx:54 | |
| «Server Owner» crown | Missing | web.js mod 589158 @754851 | none | Needs host: an owner flag in `MemberList()`. |
| Booster icon «Server boosting since {date}» | Missing | web.js mod 589158 @754974 | none | Needs host: `premium_since` from the list member. |
| Status dot (streaming purple, mobile) | Different | presence status | presence.tsx:2-9 (online/idle/dnd only) | |
| Right-click opens the person menu | Match | user-context | MemberList.tsx:47 (`data-dc-user`) | |
| Click opens the profile popout | Match | | MemberList.tsx:48 | |
| Loading more on scroll | Match | lazy ranges | MemberList.tsx:32-38 | |
| `member-list-settings-menu` / Recent Activity cards («Hide Activity Cards», «About Recent Activity») | Missing | e135f1d825d5d1d5.js mod 38055 | none | |
| Member list toggle label | Different | «Show Member List» / «Hide Member List» | ChatHeader.tsx:269 (`Show member list` / `Hide member list`) | Case. |

### 10. Presence and status

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Online / Idle / Do Not Disturb / Invisible | Different | web.js @6542863 `set-status-submenu` from the account popout, with subtexts «You will not receive desktop notifications» and «You will appear offline» | settings/AccountPages.tsx:12-17, 93-113 (Settings > Account dropdown only) | Not reachable from the user panel, and no subtexts. |
| Status durations («For 15 Minutes», «For 1 Hour», «For 8 Hours», «For 24 Hours», «For 3 Days», «Forever») and «Until {time}» | Missing | web.js @6542863 (`n$` list); setter web.js mod 827827 (proto `status` plus `statusExpiresAtMs`) | Host uses the legacy `PATCH users/@me/settings {status}` (DiscordRest.cs:209-210; DiscordSession.cs:450) | Needs host (settings proto). |
| Custom status (emoji, text, «Clear after»: today, 4 hours, 1 hour, 30 minutes, «Don't clear»; «Clear Status») | Missing | 27b6d01027514662.js (custom status modal, «Set your status») | none in the UI or host | Needs host. |
| «Switch Accounts» / «Manage Accounts» | Missing | web.js @6550027 (`switch-accounts-submenu`) | none | See Proposals. |
| Focus Mode (BETA) | Missing | web.js @6542863 (`quiet-mode`) | none | Gated experiment; low priority. |

### 11. App commands and activities

| Feature | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Slash commands from `/` | Match | command autocomplete | Composer.tsx:288; AppCommands.tsx:18 | |
| User commands («Apps» submenu in the person menu) | Different | 646911 («Apps», or «No Commands Available» when empty) | UserMenu.tsx:166-183 (hidden when empty) | |
| Message commands («Apps» in the message More menu) | Match | 646911 | Message.tsx:536; AppCommands.tsx:303-320 | |
| «App Launcher» button / «Search Apps & Commands» | Missing | 78f77b89ac557284.js mod 588158 | none | |
| «Use Apps» in the composer `+` menu | Missing | channel-attach | PollCreator.tsx:88-108 (Upload a File, Create Poll) | |
| Activities in voice | Missing | see section 2 | discordApps.ts:132-133 | |

---

#### Safe fix candidates

None of these touch ExoLauncher/Services/DiscordClient/Voice. Items marked **needs host** need a small host change outside Voice/.

1. **Audio device menu on mute/deafen right-click** (`audio-device-context`). Files: Sidebar.tsx:227-234 and voice.tsx:237-240. Add a context menu with:
   - Input Device and Output Device submenus (`host.discordAudio({input|output})`, already returns lists)
   - Input Volume and Output Volume sliders (`ui/Slider`)
   - «Push to Talk» and «Noise Suppression» checkboxes (`host.discordClientSettings`)
   - «Deafen»
   - «Voice Settings» (`requestDiscordSettings('voice')`)

   Reuse UserMenu's `exo-dc-ctx` menu styling.
2. **Noise Suppression button in VoicePanel** (voice.tsx:393-416). Toggles `noiseSuppression` through `host.discordClientSettings`, as Discord's RTC panel does.
3. **Camera right-click** (`video-device-context`) on CameraButton (voice.tsx:306-315). List cameras with `navigator.mediaDevices.enumerateDevices()`, set `setDiscordPrefs({cameraId})` as VoicePage.tsx:306 does, and add «Video Settings».
4. **Own call menu items** in CallPersonItems (voice.tsx:112-119): «Voice & Video Settings» (`requestDiscordSettings('voice')`), and «Show My Own Camera» as a local preference like GoLivePicker.tsx:96-111 that VoiceTile (voice.tsx:283-285) honors.
5. **«Show Non-Video Participants»** and **«Focus This Person»** as UI state in VoiceRoom (voice.tsx:153-213). Use a tile click or a person-menu item to focus, and a filter for non-video tiles.
6. **Self «Move to»** in UserMenu.tsx (for `me && at.call`). Build the list from `list.channels` (voice, `canConnect`, not the current channel) and call `host.discordVoice('join', {guildId, channelId})`. Discord's mod 252726 needs only Connect for this.
7. **Done (7bbe38b).** **Friend row person menu.** Add `data-dc-user`, `data-dc-name` and `data-dc-username` to the row at FriendsPage.tsx:84 so UserMenuHost opens on right-click.
8. **friend-row «Start Voice Call» / «Start Video Call».** In FriendMenu (FriendsPage.tsx:125-139), open the DM (`host.discordFriend('dm')`), then `host.discordVoice('join',{guildId:null,channelId})` and `host.discordRing`. For video, follow with `startCamera()` (ui/src/lib/goLive.ts:253).
9. **Done (6937c04).** **Add Friend / Remove Friend in the person menu** (UserMenu.tsx:149-160). Use `host.discordFriend('add'|'remove')` with the Remove confirm already in FriendsPage.tsx:115-119. The relationship is already in profile extras (DiscordCache `ProfileOn` `relationship`).
10. **Friends page parity** in FriendsPage.tsx:
    - Hide empty tabs.
    - Open on Add Friend when there are no relationships.
    - Split Pending into «Received — N» / «Sent — N».
    - Use Discord's empty-state lines, the success line («Success! Your friend request to **x** was sent.») and the placeholder.
11. **«Hide Names» for voice channels** in ChannelMenu (Sidebar.tsx:467-517). Keep it in localStorage and have VoiceChannel (Sidebar.tsx:420-432) skip VoiceMembers.
12. **«Invite to Voice» in VoiceHead** (voice.tsx:216-230). Use the existing `host.discordChannel(channel.id,'invite')` with clipboard copy, as in Sidebar.tsx:501-504.
13. **Soundboard UI** (Soundboard.tsx):
    - a search field
    - a preview button per sound (local `playHere`)
    - disable while `voice.deafened` («Soundboard disabled when deafened»)
    - label «Open Soundboard»
    - right-click «Copy Sound ID» in Developer Mode
14. **Incoming call** (voice.tsx:419-427). Show «Incoming Call...» under the name, and make Esc decline as Discord does.
15. **Not-joined voice view** (voice.tsx:188-193, 208). Use Discord's «No one is currently in voice» / «{a} is currently in voice» lines, and a disabled «Channel Locked» when `canConnect === false`.
16. **Label fixes:**
    - ChatHeader.tsx:43 → «Start Voice Call»
    - Sidebar.tsx:235 → «User Settings»
    - ChatHeader.tsx:269 → «Show Member List» / «Hide Member List»
    - GoLivePicker.tsx:144,175 → «Show My Screen Share» checkbox
    - GoLivePicker.tsx:291-292 → «Screen Resolution» / «Frame Rate»
    - voice.tsx:367 → «Mute»
17. **Set Status from the user panel** (Sidebar.tsx:220). Open a small account menu with Online / Idle / DND / Invisible and Discord's subtexts, using the existing `host.discordSetStatus`. Durations need host (item 22).
18. **(needs host) Voice channel user limit.** Send `userLimit` from `Channels()` (DiscordCache.cs:578-626) and draw the "NN / NN" badge in Sidebar.tsx VoiceChannel.
19. **(needs host) Server mute and deaf vs self mute.** Send `serverMute`/`serverDeaf` separately (DiscordCache.cs:902 already reads them; the DTO at 624-626 drops them). Report self `mute`/`deaf`/`suppress` in `VoiceStatus` (DiscordSession.Voice.cs:35-40). The UI can then show «Server Muted» / «Muted by you» / «Suppressed» icons and disable the buttons and soundboard.
20. **(needs host) Per-person «Mute Soundboard».** Add `soundboardMuted` to `SetAudioUser` (DiscordSession.Settings.cs:101) and `AudioPatch` (DiscordUserSettings.cs:156-180, already writes field 4). Filter `VOICE_CHANNEL_EFFECT_SEND` by it (DiscordCache.cs:329), and add a MenuCheck in voice.tsx:133.
21. **(needs host) Per-person «Ring» / «Stop Ringing».** Add a `recipients` parameter to `DiscordRest.RingAsync` and `StopRingingAsync` (DiscordRest.cs:175-180) and to `DiscordRingAsync` (WebHostBridge.Discord.cs:276). Add a menu item in UserMenu.tsx for DM call members who are not connected.
22. **(needs host) Status durations and custom status.** Write proto `status` with `statusExpiresAtMs` and `customStatus`, as web.js mod 827827 does, through DiscordUserSettings.cs instead of the legacy PATCH (DiscordRest.cs:209-210).
23. **Done (6937c04) for Block, Unblock, Ignore and Unignore; friend nicknames are not.** **(needs host) Block / Unblock / Ignore and friend nicknames.** Add relationship type 2 PUT, the nickname PATCH, and reading `nickname` at DiscordCache.cs:185. Add menu items and the Discord confirm text.
24. **(needs host) Member list owner crown and booster icon.** Add `owner` and `premiumSince` to `MemberList()` items (DiscordCache.cs:1149-1158).
25. **(needs host) Add Friend error mapping by Discord code.** Do it in WebHostBridge.Discord.cs:298-299 so the page shows Discord's wording instead of `Discord: <message>`.
26. **(needs host) Soundboard.** Either fetch every server's sounds for Nitro, as the comment at WebHostBridge.DiscordMedia.cs:120 already claims, or fix the comment.

#### Proposals (large or risky)

- **RTC connection popout** (ping, packet loss, hostname, connection states). This needs voice-gateway round-trip time and loss stats from `Voice/VoiceConnection.cs`, which belongs to the audio-engine session. Coordinate with them, then add `discord.voice` fields and a popout on «Voice Connected».
- **Stage channels.** Speakers and audience, Request to Speak, Invite to Speak, Move to Audience, End Stage, and a suppressed state. This needs host voice-state `suppress`/`request_to_speak_timestamp` and stage-instance REST.
- **Activities and the App Launcher.** Embedded app iframes in voice, plus «Search Apps & Commands». This is a security and design question for a WebView and it touches the RPC contract.
- **Pop Out windows** for the call, a stream or a tile. This needs a second hosted WebView window, and the DWM rules in AGENTS.md apply.
- **Watching several streams at once,** with per-stream «Stop Watching — {username}». This needs host StreamConnection fan-out (DiscordSession.Video.cs).
- **Changing quality or window while live** («Stream Quality», «Change Windows»), and Discord's Stream Mode presets.
- **Nitro gating parity for Go Live.** Exo lets any account pick 1440p, Source or 60 fps (GoLiveCapture.cs:20-30), while Discord gates these behind Nitro or server boost (web.js mod 327649). Decide whether to match or keep the deviation.
- **Camera background and blur** («Change Video Background») and «Preview Camera» before going on camera. This is heavy GPU or ML work in the page encoder pipeline (ui/src/lib/goLive.ts:28-177).
- **Per-person «Disable Video»** that actually stops receiving video, which needs media sink wants in Voice/.
- **Soundboard parity:** Favorites, Frequently Used, Top Sounds, entrance sounds, and upload/edit sound in Server Settings. Favorites use the frecency proto, as DiscordFavoriteGifs.cs does.
- **Stream Attenuation** (sidechain) belongs to the Voice/ mixer.
- **Switch Accounts.** This means multi-account token storage in native code, under the secrets hard stop. Needs a design decision.
- **Message Requests, Friend Suggestions, spam requests, and Blocked/Ignored lists in Settings.** These need new host relationship and channel filtering plus REST calls.
- **Member list Recent Activity cards** (`member-list-settings-menu`).
- **Voice channel status** («Set Channel Status»), using a new host REST call and the VOICE_CHANNEL_STATUS_UPDATE event.
- **E2EE verification codes** («View Verification Code») would come from DaveSession in Voice/.

## Area: settings (user, server and channel)

The reference comes from Discord's own code, not from memory. Discord's user settings are now a declarative tree:
`(0,E.WI)` section > `E.i4` sidebar item > `E.t_` panel > `E.zZ` category > settings (`zD` switch, `Qx` radio, `Hn` dropdown,
`sN` slider, `Tf` button row, `E2` custom component). The root is `vY=(0,E.Hr)({buildLayout:()=>[I_,vO,EI,ou,p3,hX,vz]})`
in D1 @573655. All 482 nodes were parsed. Server settings come from
`generateSections` (`function IY`) in D2 @575761, and channel settings from the `sections` list in D3 @57986.

**Discord bundle files** (`@n` is the character offset; node names are Discord's `h.X.*` ids):
- D1 = `lazy/73f98035cbc74893.js`: user settings tree
- D2 = `lazy/14d638c12241018a.js`: server settings
- D3 = `lazy/12da014be7bb454e.js`: channel settings
- D4 = `lazy/046406f7a5feb3e2.js`: server Notification Settings modal
- D5 = `lazy/11c3e58c7aa81faa.js` module 237600: guild-context menu
- W = `assets/web.d41b6bfa9f8fcb8b.js`

**Exo paths:** `discord/` means `ui/src/components/discord/` and `lib/` means `ui/src/lib/`. Host paths are given in full.
I checked every Exo claim by reading the code. I did not run the app.

How I set the status: Discord renamed the old sections. "My Account" is now Account. "Content & Social" is now
Messaging Permissions. Authorized Apps and Connections are now Connected Apps. Devices is a nested panel under Account.
Chat and Streamer Mode moved into Appearance, Keybinds into System, and Language into Language & Time. "What's New" is
no longer in the tree. Exo already follows the new tree, so these renames are not gaps.

### Settings

#### User settings: sidebar

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Sidebar: Profiles (hoisted, no title) | Match | D1 `PROFILE_SECTION` @488689 | discord/DiscordSettings.tsx:23 | |
| Sidebar: General (title hidden): Account, Data & Privacy, Messaging Permissions, Notifications, Clips | Different | D1 `USER_SECTION` @572196 (`hideTitle:!0`) | discord/DiscordSettings.tsx:24-31 | Clips is missing (it is a desktop capture feature). |
| Sidebar: Billing section (Nitro, Server Boost, Subscriptions, Gift Inventory, Billing) | Missing | D1 `BILLING_SECTION` @351400 | discord/DiscordSettings.tsx:20-21 (comment) | The collectibles/Shop session owns this. Listed for completeness only. |
| Sidebar: Experience (Voice & Video, Appearance, Accessibility, Party Mode, System, Language & Time) | Different | D1 `APP_SECTION` @208758 | discord/DiscordSettings.tsx:32-41 | Party Mode (`POGGERMODE`) is missing. Discord gates it behind an experiment. |
| Sidebar: Games & Apps (Registered Games, Activity Privacy, Game Overlay, Connected Apps) | Different | D1 `GAMES_AND_APPS_SECTION` @449324 | discord/DiscordSettings.tsx:42-48 | Registered Games and Game Overlay are left out on purpose (desktop-app features). |
| Sidebar: Other (title hidden): Developer, Log Out | Match | D1 `UTILITY_SECTION` @573512 | discord/DiscordSettings.tsx:49-54 | |
| Legacy names ("My Account", "Content & Social", "Devices", "Connections", "Authorized Apps", "Chat", "Keybinds", "Streamer Mode", "Language", "What's New") | Match | D1 root @573655 | discord/DiscordSettings.tsx:20-55 | All folded into the new tree as described above. |
| Settings search box | Missing | D1 has 120 `useSearchTerms`; key bPKiId "No results for '{query}'" | discord/DiscordSettings.tsx:87-104 (nav only) | |

#### User settings: Profiles

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Tabs | Fixed | D1 `PROFILE_SETTING` @485317: "Main Profile", "Per-server Profiles" | discord/settings/AccountPages.tsx:221-224: "User Profile", "Server Profiles" | Fixed (9814cda): "Main Profile" / "Per-server Profiles". Wrong labels. |
| Display Name | Match | D1 `Io` @480548 (`It`) | discord/settings/AccountPages.tsx:358-360 | |
| Pronouns | Match | D1 `Io` @480548 (`T4`) | discord/settings/AccountPages.tsx:361-363 | |
| Avatar: Change Avatar, Remove Avatar | Match | D1 `Io` (`TD`, "Avatar") | discord/settings/AccountPages.tsx:364-371 | |
| Avatar Decoration, Nameplate, Profile Effect, Profile Frame, Display Name Style | Missing | D1 `Io` (`Tj`, `Tk`, `TY`, `TW`); d8294482a39d24ee.js @45101 | none | The collectibles session owns these. |
| Banner Color vs Profile Banner + Profile Theme | Different | D1 `Io`: `E ? [TF banner, T5 theme] : Is` (Banner Color @479805) | discord/settings/AccountPages.tsx:372-385 | Discord shows Banner Color only without Nitro, and Profile Banner + Profile Theme only with Nitro. Exo always shows both Profile Banner and Banner Color rows, and has no Profile Theme. |
| Bio | Fixed | D1 `Io`: sectionTitle "Bio" | discord/settings/AccountPages.tsx:386-389: "About Me" | Fixed (9814cda). Wrong label. The host error at ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:121 also says "About Me". |
| Profile Widgets ("Add Widgets") | Missing | D1 `Ir` @~479900 | none | |
| Server Tag (primary guild) | Missing | D1 @478274 ("Choose a server tag to rep next to your name") | none | |
| Legacy Username Badge toggle | Missing | D1 `Ii` @479442 | none | |
| Badges | Missing | D1 `f7` @478783 | none | |
| Per-server: server picker + Server Nickname | Match | D1 `fa` @467185; "Server Nickname" @462591 | discord/settings/AccountPages.tsx:233-279 | |
| Per-server: avatar, banner, pronouns, bio, preview | Missing | D1 `fa`/`fs` | discord/settings/AccountPages.tsx:232 (comment says Nitro) | Nitro features. |
| Unsaved-changes bar (Reset / Save Changes) | Match | D1 `p6` pending changes | discord/settings/AccountPages.tsx:305-312 | |

#### User settings: Account

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Panel layout | Different | D1 `ACCOUNT_PANEL` @516710: `[Account Info, Password & Security, Account Standing, Family Center, Removal]` | discord/settings/AccountPages.tsx:41-53, 65-67, 77, 93-113 | Exo adds a banner card, a Display Name row and a Status menu. Discord's Account panel has none of these. They are extras, not errors. |
| Username: Edit | Different | D1 `ACCOUNT_INFO_USERNAME_SETTING` @496649 (in-app modal 667792) | discord/settings/AccountPages.tsx:68-70 | Exo opens discord.com/settings/account instead. |
| Email: Edit/Add (masked) | Different | D1 `ACCOUNT_INFO_EMAIL_SETTING` @493772 (modal 97060, Reveal) | discord/settings/AccountPages.tsx:71-72; ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:211-217 | Exo links out and has no Reveal. |
| Phone Number: Edit/Add | Different | D1 `ACCOUNT_INFO_PHONE_SETTING` @494991 | discord/settings/AccountPages.tsx:73-74 | Exo links out. |
| "Unverified Email" notice with Resend Verification Email | Missing | D1 `ACCOUNT_INFO_CATEGORY` @497208 (`useInlineNotice`) | none | `DiscordAccount.verified` is fetched (lib/discordPrefs.ts:95) but never used here. |
| Age group verify/edit rows | Missing | D1 `ACCOUNT_INFO_AGE_GROUP_*` (`I8`) | none | |
| Password: Edit | Different | D1 `ACCOUNT_CHANGE_PASSWORD_SETTING` @497949 (Change Password modal) | discord/settings/AccountPages.tsx:79 | Exo links out. |
| Multi-Factor Authentication (Security Keys, Authenticator App, SMS, Backup Codes) | Different | D1 `ACCOUNT_MFA_NESTED_PANEL` @511921 | discord/settings/AccountPages.tsx:80 | Exo shows Enabled/Set up and links out. |
| Logged-in Devices: current, others, Log Out, Log Out All Known Devices | Match | D1 `ACCOUNT_SESSIONS_NESTED_PANEL` @502946 | discord/settings/AccountPages.tsx:126-174; ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:60-66 | |
| Account Standing / Account Status | Missing | D1 `ACCOUNT_STANDING_CATEGORY` @513106 | none | |
| Family Center | Missing | D1 `ACCOUNT_FAMILY_CENTER_CATEGORY` @514054 | none | |
| Disable Account / Delete Account | Different | D1 `ACCOUNT_DISABLE_SETTING` @516315, `ACCOUNT_DELETE_SETTING` @516094 | discord/settings/AccountPages.tsx:85-88 | Exo links out. Discord does it in-app with a password. |

#### User settings: Data & Privacy

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Use data to make Discord work (disclaimer) | Match | D1 `DATA_USAGE_DISCLAIMER_SETTING` | discord/settings/PrivacyPages.tsx:132 | |
| Use data to improve Discord, with "Turn off use data to improve Discord?" confirm | Match | D1 `DATA_USAGE_STATISTICS_SETTING` @529690 | discord/settings/PrivacyPages.tsx:133-143 | |
| **Use data to personalize my Discord experience**, with "Turn off personalization?" confirm | Fixed | lazy/89b015ef4b0ca0e0.js module 641216 (`DATA_USAGE_PERSONALIZATION_SETTING`) | discord/settings/PrivacyPages.tsx:122 reads `personalization` but never shows it | Fixed (8f765c0). The host already reads and writes this consent (ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:50-59, 149-152). |
| Personalize Sponsored Content (my activity / third-party data) | Match | D1 `DATA_USAGE_QUESTS_SETTING` @528647, `_3P` @527897 | discord/settings/PrivacyPages.tsx:144-147 | |
| Allow my voice to be recorded in Clips | Missing | D1 `CLIPS_ALLOW_VOICE_RECORDING_SETTING` @524500 | none | |
| Request my data | Different | D1 `DATA_HARVEST_REQUEST_SETTING` @525311 (in-app request) | discord/settings/PrivacyPages.tsx:148 | Exo links to the web page. |
| Profile Privacy: Share my full profile with | Different | D1 `PROFILE_PRIVACY_SETTING` @531937 | discord/settings/PrivacyPages.tsx:150-153 | Exo is missing the per-option descriptions and the follow-up "apply to existing servers" modal. |
| Share when I update my profile | Match | D1 `NOTIFY_FRIENDS_ON_PROFILE_UPDATE_SETTING` @530550 | discord/settings/PrivacyPages.tsx:154-155 | |
| Voice End-to-end Encryption (persistent verification codes, verified keys) | Missing | D1 `VOICE_SECURITY_CATEGORY` @537808 | none | |

#### User settings: Messaging Permissions

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Content Filters: Mature Sexual Media and Graphic Media (friends' DMs / others' DMs / servers) | Match | D1 `CONTENT_FILTERS_SETTING` @546334 | discord/settings/PrivacyPages.tsx:8-34, 65-67 | Teen defaults are handled. |
| Show alerts in direct messages (safety alerts) | Missing | D1 `DM_SAFETY_ALERTS_SETTING` @547136 | none | Behind a predicate. |
| Age-restricted commands in DMs; age-restricted servers on iOS | Match | D1 @542244, @542633 | discord/settings/PrivacyPages.tsx:68-71 | |
| Automatically filter suspected spam messages | Match | D1 `DM_SPAM_SETTING` @549778 | discord/settings/PrivacyPages.tsx:36-41, 73-77 | |
| DM Permissions: server selector ("All servers" or one server) | Missing | D1 `PERMISSIONS_GUILD_SELECTOR` @558457 | discord/settings/PrivacyPages.tsx:78-91 (default only) | Per-server DM and message-request settings are unreachable in Exo. |
| Direct Messages, with the "Server Privacy" apply-to-all ask | Match | D1 `PERMISSIONS_DMS_SETTING` @555718 | discord/settings/PrivacyPages.tsx:53-61, 79-89 | No/Yes semantics match. Discord titles it "Direct Messages and Friend Requests" when its flag is on. |
| Message requests | Match | D1 `PERMISSIONS_MESSAGE_REQUESTS_SETTING` @557054 | discord/settings/PrivacyPages.tsx:81-82 | |
| Friend Request Permissions (Everyone / Friend of friends / Server members) and Show personalized messages | Match | D1 `FRIEND_REQUESTS_FIELDSET` @551685, `_NOTES_SETTING` @551816 | discord/settings/PrivacyPages.tsx:92-99 | |
| Messaging in Connected Games: Allow friends from games to DM | Match | D1 `ALLOW_GAME_FRIEND_DMS_SETTING` @539690 | discord/settings/PrivacyPages.tsx:100-103 | |
| Show Direct Messages in games; link to Authorized Apps | Missing | D1 `IN_GAME_DMS_SETTING` @539951 | none | |
| Ignore & Block (blocked and ignored user lists) | Missing | D1 `BLOCKED_USERS` @554368, `IGNORED_USERS` @554637 | none | The host doesn't expose blocked relationships (lib/discord.ts:343 only knows friend/incoming/outgoing). |

#### User settings: Notifications

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Enable Desktop Notifications; Enable Taskbar Flashing | Match | D1 `DESKTOP_NOTIFICATIONS` @565467, `TASK_BAR_FLASHING` @569568 | discord/settings/AppPages.tsx:19-22 | Kept per device. Discord's subtitle points to the per-server dialog, which Exo lacks (see the notification dialogs section). |
| Quiet while playing | Different | none | discord/settings/AppPages.tsx:23-30 | Exo-only setting. |
| Notify me when... (streaming, anniversary, friends online, profile updates, reactions) | Match | D1 `NOTIFICATION_SELECTION_FIELD_SET` @569420, `REACTION_NOTIFICATIONS` @568150 | discord/settings/AppPages.tsx:32-39 | |
| "A server has an upcoming event" | Missing | D1 `UPCOMING_SERVER_EVENT_NOTIFICATIONS` @569042 | none | Behind an experiment. |
| Sounds: New Message, current-channel message, Incoming Ring, Disable All Notification Sounds | Match | D1 `NOTIFICATION_SOUNDS_LIST` @571344 | discord/settings/AppPages.tsx:57-78 | |
| Badges: Enable Unread Message Badge | Match | D1 `ENABLE_UNREAD_MESSAGE_BADGE` @563963 | discord/settings/AppPages.tsx:41-44 | |
| Email: per-category emails; Unsubscribe from all marketing emails | Missing | D1 `NOTIFICATIONS_EMAIL_CATEGORY` @565137, @564784 | none | |
| Advanced: Allow playback and usage of /tts command | Match | D1 `TEXT_TO_SPEECH_COMMAND` @562804 | discord/settings/AppPages.tsx:45-49 | |
| Advanced: Mobile Notification Delay; Speak all messages out loud | Missing | D1 `MOBILE_NOTIFICATION_DELAY` @562494, `TEXT_TO_SPEECH_NOTIFICATIONS` @563044 | none | |
| Clips page | Missing | D1 `CLIPS_PANEL` @523605 | none | Desktop capture feature. |

#### User settings: Voice & Video (controls listed only; another session owns this page)

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Microphone / Speaker device | Match | D1 `VOICE_MICROPHONE_INPUT_SELECT` @204761, `VOICE_SPEAKER_OUTPUT_SELECT` @205084 | discord/settings/VoicePage.tsx:54-72 | |
| Microphone Volume / Speaker Volume | Different | D1 @205476, @205797 (slider 0-100 over a mapped curve) | discord/settings/VoicePage.tsx:70, 72 | Exo's sliders run 0-200%. |
| Mic Test ("Let's Check" loopback) | Different | D1 `VOICE_MICROPHONE_TEST_SETTING` @206451 | discord/settings/VoicePage.tsx:89-90, 116-217 | Exo records a Voice Check with before/after playback and shows a live meter. |
| Input Profile (Voice Isolation / Studio / Custom) | Missing | D1 `VOICE_INPUT_PROFILE_SETTING` @191881 | none | The host stores `inputProfile` (ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:236, 249), but the page doesn't show it. |
| Input Sensitivity (automatic or slider) | Match | D1 `VOICE_INPUT_SENSITIVITY_FIELD_SET` @184469 | discord/settings/VoicePage.tsx:220-244 | |
| Noise Suppression (Krisp / Standard / None) | Different | D1 `VOICE_NOISE_SUPPRESSION_SETTING` @186898 | discord/settings/VoicePage.tsx:91-92 | Exo has a single on/off switch. |
| Echo Cancellation | Missing | D1 `VOICE_ECHO_CANCELLATION_SETTING` @182547 | none | The host has `echoCancellation` (ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:276), but the page doesn't show it. |
| Push to Talk, PTT keybind, PTT release delay | Match | D1 @188410, @190051, @190820 | discord/settings/VoicePage.tsx:96-105 | |
| Advanced accordion: Automatic Gain Control | Match | D1 @177333 | discord/settings/VoicePage.tsx:94-95 | Exo shows it at the top level. |
| Advanced accordion: Advanced Voice Activity, Bypass System Audio Input Processing, device suggestions, No Audio Detected Warning, Switch Voice Channel Warning, Audio Subsystem, Quality Of Service | Missing | D1 `INPUT_PROFILE_VOICE_ADVANCED_ACCORDION` @182226 | none | |
| Global Attenuation (strength, When I speak, When others speak) | Match | D1 `VOICE_GLOBAL_ATTENUATION_FIELD_SET` @178767 | discord/settings/VoicePage.tsx:262-272 | |
| Camera: preview, choice, Always preview video, background | Different | D1 `CAMERA_CATEGORY` @170650 | discord/settings/VoicePage.tsx:275-312 | Exo has only the preview and the choice. |
| Streaming: Show Stream Previews, Stream Attenuation, and the rest | Missing | D1 `STREAMING_CATEGORY` @204639 | none | |
| Sounds list | Different | D1 `VOICE_AND_VIDEO_SOUNDS_LIST` @201169 (`aX`: 20 sounds) | discord/settings/VoicePage.tsx:18-22 | Exo lists PTT Activate/Deactivate, which Discord doesn't list. Exo lacks User Moved (the host already has `user_moved`, ExoLauncher/Services/DiscordClient/DiscordSounds.cs:24), Stream Started/Stopped, Viewer Join/Leave, the Activity sounds and Invited to Speak. |
| Soundboard: Soundboard Volume, Soundmoji Volume, Entrance Sounds | Different | D1 `SOUNDBOARD_CATEGORY` @198100 | discord/settings/VoicePage.tsx:73-74 | Exo has Mute Soundboards and Volume. |
| Reset all Voice & Video settings | Missing | D1 `VOICE_AND_VIDEO_RESET_ALL_SETTINGS` @175723 | none | |

#### User settings: Appearance

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Theme, Sync theme, profile themes, App Icon | Missing | D1 `APPEARANCE_THEME_CATEGORY` @129737, `APPEARANCE_IN_APP_ICON_CATEGORY` @121629 | none | Intentional: Exo uses its own look. |
| Messages: show media from links / uploads; embeds; reactions | Match | D1 `CHAT_INLINE_FIELD_SET` @123055, @123162, @123302 | discord/settings/AppPages.tsx:88-92 | All honored in chat (discord/Message.tsx:45-47, discord/Attachments.tsx:204, discord/Reactions.tsx:75). |
| **Show spoiler content: "On servers I moderate"** | Fixed | D1 `CHAT_SPOILERS_SHOW_SPOILERS` @123911; logic in lazy/5a4ab6813e0e0256.js module 863439 (IF_MODERATOR = can MANAGE_MESSAGES) | discord/settings/AppPages.tsx:93-94; discord/markdown.tsx:52; discord/Attachments.tsx:85 | Fixed (75f0b35). Exo offers the option but chat only checks `ALWAYS`, so "On servers I moderate" behaves like "On click". |
| Show user avatars (compact mode) | Match | D1 `APPEARANCE_DISPLAY_COMPACT_AVATARS` @123477 | discord/settings/AppPages.tsx:95-97 | |
| Open threads in split view; Show Favorites server | Missing | D1 `CHAT_THREADS_SPLIT_VIEW` @124293, `CHAT_FAVORITES_TOGGLE` @122481 | none | |
| Chat Box: convert emoticons; stickers in autocomplete | Match | D1 @119436, @120102 | discord/settings/AppPages.tsx:99-103 | Both honored (discord/Composer.tsx:27, 218). |
| Chat Box: "Preview emoji, mentions, and markdown syntax as you type"; "Show games in autocomplete results"; chat bar buttons; expression picker format | Missing | D1 `CHAT_TEXT_BOX_PREVIEWS` @119828, `CHAT_GAME_MENTIONS_AUTOCOMPLETE` @119685, `CHAT_BAR_ADVANCED_ACCORDION` @119143 | none | The host already knows `useRichChatInput` [6,2] and `includeGameMentionsInAutocomplete` [6,39] (ExoLauncher/Services/DiscordClient/DiscordUserSettings.cs:41, 66), but Exo's composer uses neither. |
| Search: "By default, searching in DMs..." | Missing | D1 `MESSAGE_SEARCH_DEFAULT_DM_SEARCH_BEHAVIOR` @124641 | none | |
| Streamer Mode: Enable + four "If Streamer Mode is enabled..." options | Match | D1 `STREAMING_STREAMER_MODE` @125588, `STREAMER_MODE_OPTIONS_LIST` @127864 | discord/settings/AppPages.tsx:104-113; lib/discordPrefs.ts:321-326 | |
| Streamer Mode: auto-enable with OBS/XSplit; Hide Discord window from capture; Hide overlay widgets | Missing | D1 @125317, @127494 | none | |
| **Streamer Mode "Hide invite links" in Server Settings > Invites** | Different | D2 `pX` @516723 (`hide: hideInstantInvites`) | discord/settingsModeration.tsx:38 | Exo shows every invite code even with Streamer Mode on. It only masks the invite toast (discord/Sidebar.tsx:178). |

#### User settings: Accessibility

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Text size in chat (12-24 stops); Always underline links | Match | D1 `APPEARANCE_FONT_SCALING` @110020 (FONT_SIZES [12..24]), `UNDERLINE_LINKS` @110415 | discord/settings/AppPages.tsx:118, 137-140 | |
| Display Name Styles toggle | Missing | D1 `DISPLAY_NAME_STYLES` @109647 | none | |
| UI Density (Compact / Default / Spacious) | Missing | D1 `APPEARANCE_UI_DENSITY` @110756 | none | |
| Chat Message Display; Space Between Message Groups; Zoom level | Match | D1 @111214, @111649, @112177 (ZOOM_SCALES 50-200) | discord/settings/AppPages.tsx:141-149 | |
| Saturation; apply to custom colors; Role Colors | Match | D1 `SATURATION` @105781, @102819, `ROLE_STYLE` @105049 | discord/settings/AppPages.tsx:150-157 | |
| High Contrast Mode; Sync contrast; HDR; Official Messages; Show on/off indicators | Missing | D1 @103555, @106203, @103939, @104551, @103312 | none | |
| Reduced Motion: Enable; Play GIFs; Play animated emoji | Match | D1 `REDUCED_MOTION` @108378, @107699, @107491 | discord/settings/AppPages.tsx:158-162 | |
| Reduced Motion: Play sticker animations | Different | D1 `ANIMATE_STICKERS` @107924 (`useDisabled: eD("animateStickers")`) | discord/settings/AppPages.tsx:163-164 | Exo leaves it enabled while Reduced Motion is on. |
| Reduced Motion: Sync with computer setting; Minimum notification duration | Missing | D1 `SYNC_REDUCED_MOTION` @108662, `TOAST_DURATION` @108953 | none | |
| Text-to-Speech rate; Show image descriptions | Match | D1 `TTS_PLAYBACK_RATE` @101703, @101011 | discord/settings/AppPages.tsx:166-170 | Both honored (lib/discordPrefs.ts:389-394; discord/Attachments.tsx:95). |
| Use the legacy chat input | Missing | D1 `ENABLE_LEGACY_CHAT_INPUT` @100822 | none | |

#### User settings: System, Language & Time, Games & Apps, Other

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| System: General (open on startup, minimized, tray, hardware acceleration); Game Mode; System Helper | Missing | D1 `SYSTEM_GENERAL_CATEGORY` @166628, `SYSTEM_GAME_MODE_CATEGORY` @167766 | none | Desktop-app settings. Exo's own settings cover this ground. |
| System: Custom Keybinds (Action, Record Keybind, Delete, Add a Keybind) | Different | D1 `CUSTOM_KEYBINDS_SETTING` @158084; action table in W (`[j.hCu.PUSH_TO_TALK_PRIORITY]` ...) | discord/settings/KeybindsPage.tsx:7-15, 93-137 | Exo has 7 actions. It lacks PTT (Priority), Toggle Camera, Toggle Go Live, Soundboard, Navigate Back/Forward, Priority Speaker and Overlay. |
| System: Default Keybinds list | Missing | D1 `DEFAULT_KEYBINDS_SETTING` @159567 | none | |
| Language & Time: Language | Different | D1 module 434404 `LANGUAGE_SELECT_SETTING` ("Choose the language you want Discord to display.") | discord/settings/AppPages.tsx:184-192 | Exo saves only the account locale and says so, because Exo's UI isn't translated. |
| Language & Time: Time format (Auto / 12-hour / 24-hour) | Match | module 434404 `TIME_FORMAT_SETTING` | discord/settings/AppPages.tsx:193-194 | Honored in chat (discord/Message.tsx:155). |
| Registered Games; Game Overlay | Missing | D1 `REGISTERED_GAMES_PANEL` @449064, `OVERLAY_PANEL` @441172 | none | Desktop features. |
| Activity Privacy: Share my activity / when I come online / when I start streaming | Match | lazy/89b015ef4b0ca0e0.js module 311059; D1 @388507, @388850 | discord/settings/PrivacyPages.tsx:173-178 | |
| Activity Privacy: Automatically share my activity when joining a server | Match | D1 `ACTIVITY_PRIVACY_PER_GUILD_DEFAULT_SETTING` @389628 (enum values 1/2/3 checked in W @11156415) | discord/settings/PrivacyPages.tsx:162-165, 179-183 | Exo lacks Discord's follow-up "apply to existing servers" modal. |
| Activity Privacy: per-server list | Missing | D1 `ACTIVITY_PRIVACY_PER_GUILD_SETTING` @395947 | none | |
| Activity Privacy: Who Can Join My Games | Match | D1 @396395, @396930 | discord/settings/PrivacyPages.tsx:184-189 | |
| Connected Apps: Connections | Different | D1 `CONNECTIONS_ADD_CONNECTIONS_SETTING` @411780, `CONNECTIONS_CONNECTED_ACCOUNTS_SETTING` @412151 ("Display on profile", "Display {platform} as your status", "Disconnect") | discord/settings/PrivacyPages.tsx:212-220 | Exo's list is read-only and "Add" opens the web. |
| **Connected Apps: Authorized Apps / Deauthorize** | Fixed | D1 `AUTHORIZED_APPS_LIST_SETTING` @397629; confirm "Deauthorize Application" in lazy/3bf0b1c5e9aefe9c.js @4660 | discord/settings/PrivacyPages.tsx:201-208, 225-229 | Fixed (8f765c0): "Deauthorize Application" asks first. Exo deauthorizes on one click with no confirm, and has no search. |
| Developer: Developer Mode | Match | D1 `DEVELOPER_MODE` @572662 | discord/settings/AppPages.tsx:201-211 | Honored by Copy ID items (discord/Sidebar.tsx:154, discord/Message.tsx:518). |
| Developer: Application Test Mode | Missing | D1 `APPLICATION_TEST_MODE` @572373 | none | |
| Log Out | Different | D1 `LOGOUT_SIDEBAR_ITEM` @573192 (confirm modal) | discord/settings/AppPages.tsx:214-227; ExoLauncher/Services/DiscordClient/DiscordSession.cs:124-139 | Exo uses a page instead of a modal. The effect is the same: server-side logout, then the token is forgotten. |

#### Server settings

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Nav: [server name] Server Profile, Server Tag, Server Theme, Engagement, Boost Perks / Expression / People: Members, Roles, Invites, Access / Apps: Integrations, App Directory / Moderation: Safety Setup, Audit Log, Bans, AutoMod / Community... / Server Template, Delete Server | Different | D2 `IY` @575761 | discord/ServerSettings.tsx:28-47 | Exo has 12 of about 30 sections. The rows below cover the rest. |
| Server Profile: Name, Icon (Change / Remove) | Match | D2 `Iw` @561628 | discord/ServerSettings.tsx:206-217 | |
| Server Profile: Banner | Different | D2 `Iw` (guild-profile custom banner); the channel-list banner sits in Boost Perks (`E5` @434989) | discord/ServerSettings.tsx:218-226 | Exo puts the boost-level BANNER image under Server Profile. |
| Server Profile: Description | Different | D2 `Iw` (`It`, guild profile, any server) | discord/ServerSettings.tsx:227-232 | Exo shows it only for Community servers and saves `guild.description`. |
| Server Profile: Traits, Games, Private Profile, live preview | Missing | D2 `Iw` (`ID`, `IA`, `IU`) | none | |
| Server Tag; Server Theme | Missing | D2 @564992, @565240 | none | |
| Engagement: System Messages Channel + welcome / sticker / boost / tips switches | Match | D2 `pz` @514409 | discord/ServerSettings.tsx:238-265 | Label differences: Discord says "...someone boosts this server." (lowercase) and "This is the channel we send system event messages to." |
| Engagement: server-product and voice-hangout system message switches | Missing | D2 `pz` | none | |
| Engagement: Inactive Channel / Inactive Timeout | Match | D2 `pz` | discord/ServerSettings.tsx:266-274 | Exo writes "Inactive Time-out". |
| Engagement: Default Notification Settings | Match | D2 `pz` (`pm`) | discord/ServerSettings.tsx:275-282 | Exo lacks the "We highly recommend... only @mentions for a Community Server" line. |
| Engagement: Server Widget (Enable, Invite Channel, Server ID, JSON API) | Missing | D2 `pz` (`pH`) | none | |
| Engagement: Activity Feed, Channel Summaries, Server Hub tab | Missing | D2 `pz` | none | Gated features. |
| Boost Perks: Show Boost progress bar, Server Banner Background, Server Invite Background, Custom Invite Link | Missing | D2 `E5` @434989 | none | |
| Emoji: upload, rename, delete, uploader, slots | Match | D2 @566672 | discord/settingsExpressions.tsx:26-83 | |
| Stickers: upload, edit, delete, slots | Match | D2 @566860 | discord/settingsExpressions.tsx:97-186 | |
| Soundboard; Official Messages | Missing | D2 @567095, @567336 | none | |
| Members: open the Members page; table, search, sort, prune, row actions | Match | D2 @573576 (onClick jumps to the Members page when available) | discord/ServerSettings.tsx:69; discord/Sidebar.tsx:54; discord/serverPages.tsx:337-456 | Exo lacks the Signals column and bulk select. Sort labels should read "Member Since (Newest first)" and similar (keys V7zCwB, l2Zaet, xMA6RG, bUKkZx). |
| Roles list: Default Permissions card, search, Create Role, drag to reorder, member counts | Match | D2 @566402; strings @~405000 | discord/settingsRoles.tsx:52-135 | |
| Role editor: Display (name, color, hoist, mentionable) | Match | D2 role edit | discord/settingsRoles.tsx:197-240 | |
| Role editor: Role Style (Solid / Gradient / Holographic), Role Icon, Links tab | Missing | keys 9wVJRB, XpWmJz, QTKppe, DFSvTt | none | |
| Role editor: Permissions (search, Clear permissions, groups) | Match | e81ceb7908021a02.js @8907 | discord/settingsRoles.tsx:255-284; lib/discordPermissions.ts:148-156 | Exo omits the "Stage Channel Permissions" group (Request to Speak), which Discord shows on Community servers. |
| Role editor: Manage Members (search, Add Members, remove) | Match | key LR+Ptf | discord/settingsRoles.tsx:287-377 | |
| Role actions: Duplicate Role, View Server As Role, Copy Role ID | Missing | module 316710 (`guild-settings-role-context`) | none | |
| Role actions: Delete Role confirm | Fixed | module 316710: "Are you sure you want to delete the {name} role? This action cannot be undone.", button "Delete" | discord/settingsRoles.tsx:246-250 | Fixed (9814cda). Exo's button says "Okay" and its text says "the role {name}". |
| Invites: list (Inviter, Invite Code, Uses, Expires) and revoke | Match | D2 `pX` @516723 | discord/settingsModeration.tsx:12-51 | |
| Invites: Pause Invites; create link | Missing | key Uwsjn6 | none | |
| Access: Invite Only / Apply to Join / Discoverable, Server Rules, Age-Restricted Server | Missing | D2 @567973, `AV` @541087 | none | |
| Integrations: Webhooks list + Delete Webhook | Different | D2 @568225 ("Create Webhook", "Copy Webhook URL", edit) | discord/settingsModeration.tsx:58-74, 95-130 | Exo can only delete. |
| Integrations: Bots and Apps (Remove) | Different | D2 @568225 ("Manage" for command permissions) | discord/settingsModeration.tsx:76-92 | Exo can only remove. |
| Integrations: Channels Followed | Missing | key OrV60r | none | |
| App Directory | Missing | D2 @568546 | none | |
| Safety Setup (non-Community): Verification Level, Sensitive content filters | Match | D2 @~46912, @~47957 | discord/ServerSettings.tsx:287-313 | Community restrictions are mirrored. |
| **Safety Setup: Require 2FA for moderator actions** | Different | D2 @~50616 (the owner can toggle it once they have 2FA) | discord/ServerSettings.tsx:314-316 | The switch is always disabled, so even the owner can't change it. |
| Safety Setup (non-Community): Activity Alerts, Safety Notifications Channel, Show Members In Channel List, Restrict member prune to admins | Missing | D2 @~44108-50600 | none | |
| Safety Setup (Community): Raid Protection and CAPTCHA, DM and Spam Protection, AutoMod, Permissions cards | Missing | D2 `uU` @284192, `uL` | none | |
| AutoMod: rule kinds, Set Up, Edit, Delete, enable switch, actions, exemptions | Different | D2 `GUILD_AUTOMOD` @569521; labels @55820-81600 | discord/settingsAutomod.tsx:13-24, 34-246; discord/ServerSettings.tsx:42 | It works, but the labels differ. Discord uses "Send alert", "Timeout member", "Block member interactions", "Insults & Slurs" and "Allow certain roles or channels (optional)". Placement also differs: Discord puts AutoMod inside Community Safety Setup. |
| Audit Log: filter by user and action, expand entries, load more | Match | D2 @569862 | discord/settingsModeration.tsx:179-258 | |
| Bans: search, ban detail with reason, Revoke Ban | Match | D2 @570069 | discord/settingsModeration.tsx:263-322 | Discord also shows Bans with View Audit Log. Exo requires Ban Members (discord/ServerSettings.tsx:44; ExoLauncher/Services/WebHostBridge.DiscordAdmin.cs:272). |
| Community Overview / Enable Community, Onboarding, Server Insights, Welcome Screen, Server Web Page, Server Products, Promo Page | Missing | D2 @570261, @570835, @571476, @572221, @572643 | none | |
| Server Template; Delete Server | Missing | D2 @567662, @573932 | none | |
| Unsaved changes hold the page (save bar shakes) | Match | D2 `tE.A` save notice | discord/admin.tsx:87-133 | |

#### Channel settings

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Sections: Overview, Permissions, Invites, Integrations, Delete Channel/Category/Thread/Post | Different | D3 @57986 | discord/ChannelSettings.tsx:43, 57-67 | Exo lacks Invites and Integrations. Categories can't be edited: no category context menu (discord/Sidebar.tsx:388-397). |
| Overview: Channel Name, Channel Topic, Slowmode | Match | D3 @20000-57900 strings | discord/ChannelSettings.tsx:24-25, 110-122 | |
| Overview: Content Visibility (Default / Spoiler Channel / Age-Restricted) | Different | D3 ("Content Visibility", "Spoiler Channel") | discord/ChannelSettings.tsx:123-125 | Exo has only the Age-Restricted switch. |
| Overview: Announcement Channel, Hide After Inactivity, Channel Summaries, Save/Copy embedded media | Missing | D3 | none | |
| Overview (voice): Bitrate, User Limit | Match | D3 | discord/ChannelSettings.tsx:126-137 | |
| Overview (voice): Video Quality, Region Override, Channel App | Missing | D3 | none | |
| Overview (forum): Post Guidelines, Tags, Default Reaction, Default Layout, Sort Order, Tag Matching | Missing | D3 | none | |
| Permissions: advanced roles/members list, tri-state, add role/member, remove | Match | D3 @0-20000 strings | discord/ChannelSettings.tsx:146-267; lib/discordPermissions.ts:158-171 | |
| Permissions: Private Channel switch; synced-with-category notice + Sync Now | Missing | D3 ("Private Channel", "Sync Now") | none | |
| Delete Channel confirm | Match | D3 @57986 `DELETE` | discord/ChannelSettings.tsx:78-82 | |

#### Notification and server-privacy dialogs

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Server Notification Settings modal: Mute server + duration; All / Only @mentions / Nothing; Community Activity Alerts; Suppress @everyone/@here; Suppress Role @mentions; Suppress Highlights; Mute New Events; Mobile Push; Notification Overrides | Missing | D4 (whole file) | none (discord/Sidebar.tsx:179-193 header menu) | Exo has no per-server notification level at all. |
| guild-context: Notification Settings submenu, Mute/Unmute Server, Hide Muted Channels | Missing | D5 (`ec`; mods 507238, 153594) | discord/Sidebar.tsx:185-190 (only Show All Channels) | |
| Channel bell: Mute for 15m/1h/3h/8h/24h/until turned back on; Unmute; levels | Match | lazy/037809489270256b.js @467 | discord/ChatHeader.tsx:199-246 | |
| Channel bell: default level label | Different | lazy/037809489270256b.js @467 ("Use Category Default" or "Use Server Default"; none for voice channels) | discord/ChatHeader.tsx:231-234 ("Use Default") | |
| Server Privacy Settings (Direct Messages and Message requests for one server; Activity joining) | Missing | module 382573 (guild-context `privacy`) | none | |

#### Events dialog (events.tsx)

| Page / control | Status | Discord | Exo | Notes |
|---|---|---|---|---|
| Events list: Interested, Copy Link, menu (Add to Calendar, Start/End, Edit, Cancel, Copy ID) | Match | keys DlcqlU, 9o+VKx, k5pvjo, cK1GGY, qaYzPA, Rgy2dU, B9sJLX | discord/events.tsx:14-147 | |
| Create/Edit Event: location, Event Topic, start, description | Match | keys DC+Qm8, 0HbEQ6, dnUzb6 | discord/events.tsx:150-233 | |
| Create/Edit Event: End Date/Time, Event Frequency, Cover Image, Stage Channel | Missing | keys enxcAl, 59TVxL, Ly121e, EErMzA | none | Exo keeps an existing end time but can't set one. |

#### Safe fix candidates

Each needs no new host RPC unless it is marked **needs host**.

1. **Done (8f765c0).** **Data & Privacy: add "Use data to personalize my Discord experience".**
   - File: discord/settings/PrivacyPages.tsx:120-149. Place it after "Use data to improve Discord".
   - Subtitle: "Allows Discord to use information, such as who you talk to and what games you play, to personalize Discord for you."
   - Turning it off asks "Turn off personalization?" with the body "By turning this off you lose access to things like personalized recommendations and suggestions. You won't be able to recover them in the future!". Buttons: "No, take me back!" / "Yes, I'm sure".
   - Save with `host.discordAccount('setConsent', {grant|revoke: ['personalization']})`. The host already allows it (ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:55-59, 149-152).
   - Discord: lazy/89b015ef4b0ca0e0.js module 641216.

2. **Done (8f765c0).** **Deauthorize needs a confirm.**
   - Files: discord/settings/PrivacyPages.tsx:201-208, 227. Use `Confirm` (discord/admin.tsx:136).
   - Title "Deauthorize Application", body "This action will remove the link between your Discord account and {applicationName}", action "Deauthorize".
   - Discord: lazy/3bf0b1c5e9aefe9c.js @4660.

3. **Streamer Mode should hide invite codes on Server Settings > Invites.**
   - File: discord/settingsModeration.tsx:38. When `useStreamerHides().invites` is true, mask the code cell.
   - Discord passes `hide: hideInstantInvites` to its invite table (D2 `pX` @516723).

4. **Done (75f0b35).** **Honor "Show spoiler content: On servers I moderate".**
   - Files: discord/markdown.tsx:50-52 and discord/Attachments.tsx:85.
   - Treat `IF_MODERATOR` as "shown" when the user can Manage Messages in the channel. `DiscordChannel.canManageMessages` already exists (lib/discord.ts:38); pass it through the markdown context and to `Media`.
   - Discord: lazy/5a4ab6813e0e0256.js module 863439 (`case IF_MODERATOR: return canManageMessages`).

5. **Per-server DM and message-request permissions.**
   - Add the server selector to Messaging Permissions (discord/settings/PrivacyPages.tsx:78-91), or add a "Privacy Settings" dialog for a server.
   - A server's DMs are on when the server is not in `restrictedGuildIds`. Message requests are on when the server is in neither `restrictedGuildIds` nor `messageRequestRestrictedGuildIds`, and are disabled while DMs are off. Save with `changeAccountSettings`.
   - Both fields are already in `AccountSettings` (lib/discordPrefs.ts:54, 59) and the host's `Known` list (ExoLauncher/Services/DiscordClient/DiscordUserSettings.cs:79, 83).
   - Discord: D1 `PERMISSIONS_GUILD_SELECTOR` @558457 and `PERMISSIONS_DMS_SETTING` @555718; module 382573.
   - Per-server Activity joining **needs host**: `activity_restricted_guild_ids` is not in `Known`.

6. **Done (9814cda) for the action and list names; the exempt label is not.** **AutoMod labels.**
   - File: discord/settingsAutomod.tsx.
   - Line 22: "Block message", "Send alert", "Timeout member", "Block member interactions".
   - Line 24: "Insults & Slurs".
   - Lines 132 and 135: "Allow certain roles or channels (optional)".
   - Discord: D2 @55820-81600.

7. **Done (9814cda).** **Delete Role confirm wording.**
   - File: discord/settingsRoles.tsx:247-249.
   - Action "Delete"; text "Are you sure you want to delete the {name} role? This action cannot be undone."
   - Discord: module 316710.

8. **Done (9814cda).** **Profiles copy.**
   - File: discord/settings/AccountPages.tsx.
   - Lines 222-223: tabs "Main Profile" and "Per-server Profiles".
   - Lines 387-388: "About Me" becomes "Bio". The matching host error string at ExoLauncher/Services/WebHostBridge.DiscordSettings.cs:121 is host copy only.
   - Discord: D1 @485317, `Io` @480548.

9. **Banner rows by Nitro.**
   - File: discord/settings/AccountPages.tsx:372-385.
   - Without Nitro, show Banner Color only. With Nitro, show Profile Banner (Discord's `Io` shows Banner Color only when `!canUsePremiumProfileCustomization`).
   - Profile Theme (`theme_colors`) **needs host**.

10. **Done (9814cda).** **Engagement copy.**
    - File: discord/ServerSettings.tsx.
    - Line 242: "Send a message when someone boosts this server."
    - Line 255: "This is the channel we send system event messages to."
    - Line 271: "Inactive Timeout".
    - Discord: D2 `pz` @514409.

11. **Done (9814cda).** **Member sort labels.**
    - File: discord/serverPages.tsx:279-282.
    - Use "Member Since (Newest first)", "Member Since (Oldest first)", "Joined Discord (Newest first)" and "Joined Discord (Oldest first)".
    - Discord: lazy/51d6a3a7d7cf91ad.js @853.

12. **Disable "Play sticker animations" under Reduced Motion.**
    - File: discord/settings/AppPages.tsx:163. Add `disabled={reduced}`, as the GIF and emoji rows already do.
    - Discord: D1 `ANIMATE_STICKERS` @107924.

13. **Channel Settings > Invites tab.**
    - File: discord/ChannelSettings.tsx. List `admin('invites', {guildId})` filtered to this channel, with revoke (reuse `Invites` from discord/settingsModeration.tsx).
    - This works for Manage Server holders only, because the host checks Manage Server (ExoLauncher/Services/WebHostBridge.DiscordAdmin.cs:252). Discord also shows it with Manage Channels through `channels/{id}/invites`, so full parity **needs host**.

14. **Channel bell default label: "Use Category Default" / "Use Server Default".**
    - File: discord/ChatHeader.tsx:231-234. Also show no level options for voice channels.
    - **Needs host**: a `parentId` field on `DiscordChannel` (lib/discord.ts:19-60), unless the parent is inferred from list order.

15. **Channel Overview extra fields.** **Needs host (small).**
    - Fields: `video_quality_mode` (Auto / 720p), `rtc_region`, `default_auto_archive_duration`, announcement type 0↔5, and the spoiler flag.
    - Host: extend `AdminSaveChannelAsync` (ExoLauncher/Services/WebHostBridge.DiscordAdmin.cs:337-353) and the `ChannelSettings` DTO.
    - UI: discord/ChannelSettings.tsx:91-141.

16. **Boost Perks: Show Boost progress bar and Server Invite Background.** **Needs host (small).**
    - Add `premium_progress_bar_enabled` and `splash` to the `saveGuild` allowlist (ExoLauncher/Services/WebHostBridge.DiscordAdmin.cs:72-86).
    - UI: a new Boost Perks section in discord/ServerSettings.tsx.

17. **Require 2FA for moderator actions.** **Needs host.**
    - Add owner-only `POST guilds/{id}/mfa`.
    - Until then, discord/ServerSettings.tsx:315 shows a switch that can never change. Showing "On" or "Off" as plain text would be honest.

18. **Server Notification Settings dialog and Mute Server.** **Needs host.**
    - Add a new action on `PATCH users/@me/guilds/{id}/settings` with `message_notifications`, `suppress_everyone`, `suppress_roles`, `notify_highlights`, `mute_scheduled_events`, `mobile_push`, and `muted` + `mute_config`.
    - The endpoint helpers already exist at ExoLauncher/Services/DiscordClient/DiscordRest.cs:249-263, 405-423.
    - Entry point: discord/Sidebar.tsx:179-193. Discord: D4.

19. **Webhooks: Create Webhook, rename/channel/avatar, Copy Webhook URL.** **Needs host.**
    - Host: next to `WebhooksAsync` (ExoLauncher/Services/DiscordClient/DiscordRest.Admin.cs:121-125). UI: discord/settingsModeration.tsx:58-74.

20. **Connections: Display on profile, Display {platform} as your status, Disconnect.** **Needs host.**
    - `PATCH`/`DELETE users/@me/connections/{type}/{id}`. UI: discord/settings/PrivacyPages.tsx:212-220.

#### Proposals (large/risky)

- **Settings search.** Discord gives about 120 settings search terms. Exo would need a keyword index over its own rows and a filtered view in discord/DiscordSettings.tsx. This is UI-only, but it touches every page.
- **Server notification settings, done properly.** Build the full D4 modal with a Notification Overrides list (channel and category pickers, per-override mute and level) and the guild-context submenu. That needs host actions plus a guild-settings DTO (current flags), and touches the menus owned by the context-menu session.
- **Community suite.** Enable Community, Community Overview, Onboarding, Welcome Screen, Server Insights, Access (join modes, rules, discovery), Server Web Page, and Community Safety Setup cards (raid/CAPTCHA, DM/spam, risky permissions). This is many new endpoints. Mistakes change who can join or see a server, so each piece needs its own confirmation flow.
- **Account security in-app.** Password, email and phone changes, MFA (security keys, TOTP, SMS, backup codes), and disable/delete account. These handle credentials and MFA codes. The current link-out to discord.com/settings/account is the safer choice; I recommend keeping it.
- **Server Profile v2.** The guild profile API (traits, games, private profile, custom banner, preview), Server Tag, Server Theme, Boost Perks (vanity URL), Server Widget, Server Template and Delete Server (owner, MFA). Several are destructive or owner-only.
- **Roles parity.** Role Style (gradient/holographic), Role Icon, Links (linked roles), Duplicate Role and View Server As Role. View As Role needs a client-side permission simulation across the whole room.
- **Expressions and moderation additions.** Soundboard management (upload, edit, delete), Pause Invites (incident actions), Channels Followed, app command permissions under Integrations > Manage, and Blocked/Ignored lists (relationship type 2 isn't exposed by the host yet).
- **Forum channel settings.** Tags, default reaction, layout, sort order, post guidelines, plus Edit Category (category context menu and settings).
- **Parity fixture.** Commit a trimmed export of Discord's settings node tree (node names, titles and order) as a test fixture. Then a script can diff it against `GROUPS` and page labels, and future bundle drops show renames automatically.
- **Out of scope here:** Billing (Nitro, Server Boost, Subscriptions, Gift Inventory, Billing), collectibles (decorations, effects, nameplates, frames, Display Name Style) and the Voice & Video internals (Input Profile, Echo Cancellation, the 0-100 volume mapping, the sounds list). They are listed above for the owning sessions.
