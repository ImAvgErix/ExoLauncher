# Exo Launcher 3.11.0

_October 20, 2026_

This update cleans up what a second tester found: the Store page, game pages and Properties, Discord, accounts, and the polish everywhere.

### Library
- **Game pages without Captures:** your clips and snips stay in Captures, not on each game's page. _Checked in code only._
- **A calmer game page.** A longer Play with Remove and Properties beside it; Open folder, Verify files and the cover tools live in Properties now, and Upscalers and Shaders are one row each that opens its System page, where Update and Compile live. Allowing upscaler changes for a game with anti-cheat moved to its Properties. _Verified: on screen._
- **Properties fits.** A wide panel with its sections on the left, like Settings, with nothing to scroll. _Verified: on screen._
- **Game art lifts on hover** instead of drawing a ring around it, and games in Continue aren't repeated in All games. _Verified: on screen._
- **Properties fit the store:** Riot, Epic and other games show only what their store has, never Steam's overlay, cloud saves or launch options, and a section with nothing in it is left out. _Verified: on screen._
- **Every DLC.** Properties lists every DLC a Steam game has, outfits and upgrades included, marked Owned or Not owned; a switch shows only where Steam has one. _Checked in code only._
- **Content packs you can remove:** packs Steam keeps only in a game's downloads, such as Call of Duty's Multiplayer and Zombies (over 200 GB together), show in Properties with a switch, like Marvel Rivals' HD pack. _Checked in code only._
- **Steam overlay off.** It costs frames, so it is off by default for your Steam games; turn it on for one game in Properties or for all in Settings > Game stores. Exo changes it while Steam is closed, never while you play or download. Exo writes the switch where Steam reads it (it used to write a copy Steam ignores, so games still got the overlay). _Checked in code only._
- **Steam EULAs accepted for you:** each installed game's EULA is accepted the way Steam's Accept button saves it, before launch if one is new, so no game stops at a EULA dialog or a blank Steam window. _Checked in code only._
- **Roblox's logo** on its game page, as every other store's. _Verified: on screen._

### Store
- **Game pages fill any window,** from a half-screen window to a wide monitor, with nothing to scroll and spare room going to the trailer and the text, not to gaps. _Verified: on screen._
- **Cards and your PC.** About, Details and System requirements are cards; requirements read short, mark what your PC meets with its own part beside it, and say where you stand at the top. _Verified: on screen._
- **Whole hero art** across the top of the page. _Checked in code only._
- **Game pages open cleanly:** the card grows into the page's window and shrinks back into it on close, as a card opens in the App Store, and a trailer shows its picture until it really plays. _Checked in code only._
- **Sharp banner art:** a game without Steam's wide banner shows its full-size header instead of a small capsule stretched across the banner. _Checked in code only._
- **Other stores' prices come back:** Exo asks CheapShark only when a page opens, not for every card you point at, which had it refusing Exo, and gives it a ten-minute break if it ever says to slow down. _Checked in code only._
- **Graphics asked for by memory.** A game that asks only for video memory ("1 GB VRAM") is checked against your card's memory; it showed no check mark. _Checked in code only._

### Discord
- **Collectibles, as Discord shows them:** avatar decorations, nameplates, display name styles (in Discord's own fonts and effects), profile effects and profile colors show on profile cards, full profiles and the DM profile panel, and in the member list, DMs, Friends, call tiles and messages. They move only while Exo is in front (in lists and messages while pointed at, and with Performance mode only while pointed at) and stay still with Reduce motion. _Verified: on screen._
- **Discord's Shop:** Shop sits under Friends, as in Discord, with Discord's catalog by category, Avatar Decorations, Profile Effects, Nameplates and Your Collection, each item shown on your own profile card, picture or name with the price you pay and the Nitro price. Use and Remove put on and take off decorations, nameplates and profile effects you own, here and in Discord settings > Profiles. _Verified: on screen._
- **Buying in Exo:** Buy checks out with a card already saved on your Discord account, showing Discord's exact price with tax before you confirm; Exo never sees card numbers, and a payment your bank must confirm (or PayPal and other methods) finishes in Discord's own shop, as does an account with no saved card. _Checked in code only._
- **Profiles show what you wear:** Discord settings > Profiles has your real profile card beside the fields, with your decoration, effect, colors and unsaved changes, plus a counter in About Me and a proper banner color swatch; Account and the panel at the bottom show your collectibles too. _Verified: on screen._
- **Role channels show at once and stay:** Exo asks Discord for your own roles in each server as soon as it connects, so channels only your roles open (moderator channels and the like) show right away and don't drop out after a reconnect. _Checked in code only._
- **Raw mic, no Windows processing:** Exo takes the mic as it is, without Windows' or the driver's enhancements or anything an Equalizer APO chain adds, and Windows' echo cancellation, gain control and noise suppression stay off; the Echo Cancellation switch is gone. _Checked in code only._
- **The noise gate hears speech:** with noise suppression on, it opens for sound that suppression keeps (speech, down to -65 dBFS) and stays shut for what it cuts (noise), so a quiet voice gets through and leftover noise never does. _Checked in code only._
- **Clip Guard:** a limiter keeps your voice under -1 dBFS when gain control lifts it or you shout, adding no delay. _Checked in code only._
- **Noise suppression at full strength:** it removes noise all the way, as DeepFilterNet is built to, with its post-filter off; about 5% of one core and 20 ms. _Checked in code only._
- **A quiet mic gets through:** with noise suppression on, the automatic noise gate opens for a voice down to -70 dBFS instead of -55, which held a quiet mic shut. _Checked in code only._
- **Voice settings, plain switches:** Noise Suppression, the noise gate (Input Sensitivity), Echo Cancellation and Automatic Gain Control, each yours to turn on or off, with Voice Check to hear yourself before and after. No profiles, Voice Focus or Voice Tune. A mic that cleans itself, such as NVIDIA Broadcast's or AMD's, gets no second pass from Exo. _Checked in code only._
- **AMD Noise Suppression's mic recognized:** pick AMD Software's "AMD Streaming Audio Device" as your mic and Exo's noise suppression steps aside for AMD's, so your voice isn't cleaned twice. _Checked in code only._
- **Mutes are kept:** muting a person or a stream now saves to your Discord account; a mute without a volume change used to fail to save. _Checked in code only._
- **Typing stays out of calls:** noise suppression takes noise down 30 dB (36 in a noisy room) instead of 18, enough to remove a keyboard, without the post-filter that muffled voices. Voice Tune no longer raises Microphone Volume past 100%, since gain control already lifts a quiet voice. _Checked in code only._
- **Chats stay at the newest message** when you come back to Discord or pictures load in. _Checked in code only._
- **Background noise stays out:** the voice gate opens for sound that lasts at least 4 ms, so steady noise can't open it, and still adds no delay. _Checked in code only._
- **Sharp streams and cameras:** Exo now tells Discord what arrives and how much it can take, as Discord's own app does, so a stream rises to the quality it is sent at instead of staying at its starting bitrate (a 1440p stream came in at 1.5 Mbit/s and looked blocky). _Checked in code only._
- **Watching a stream:** Hide Members lets the stream take the room, and Full Screen (or a double-click) puts it on the whole screen; Esc leaves. Stream Volume and Mute Stream sit beside them, kept on your account as Discord keeps them. _Checked in code only._
- **Soundboard volume and mute:** Discord settings > Voice has Mute Soundboards and a Soundboard Volume slider for the sounds others play in a call. _Checked in code only._
- **Voice channel chat** gets Discord's short header with Close Chat, and Show or Hide Chat in the call. _Checked in code only._
- **The voice bar** names the call as channel / server wherever you are; click the name to open the call. _Checked in code only._
- **The picture viewer** is the picture alone; right-click to copy, save or open, videos included. _Checked in code only._
- **Spotify Jam links** in chat show an invite card with Join, which joins in Exo's Music room. _Checked in code only._
- **Server emoji** you can use send as emoji, never as a link; ones you can't are locked in the picker. _Checked in code only._
- **Sticker favorites,** synced with your Discord account, with right-click Favorite. _Checked in code only._
- **Instant profile panel** in DMs, and the bar above the DM doesn't repeat the person while it's open. _Checked in code only._
- **Who's in voice:** pointing at a server shows who is in its voice channels and who is streaming. _Checked in code only._
- **Lock icons** on private channels, as in Discord. _Checked in code only._
- **One field style** for every search, with the text centered. _Checked in code only._
- **Lighter calls:** noise suppression reuses one engine instead of leaving a thread running after every call and Voice Check, received sound allocates nothing per packet, the microphone is cut into frames without per-sample work, and app.log says each minute how much CPU the call takes. _Checked in code only._
- **Your voice reaches the call sooner, and sounds like you:** with a game in front, Exo no longer slows itself down, lowers its priority or pages noise suppression out of memory during a call, and the microphone never waits on the rest of the voice's processing. Voice Focus is now a plain gate, low cut and limiter that adds no delay (it held every word back 20 ms). Noise suppression turns noise down by at most 24 dB, with its post-filter off; unlimited, it took parts of your voice with the noise and muffled it. Voice Check's "after" is what the other person hears, Opus included, and app.log says each minute how old your voice is when it leaves Exo and where that time went. _Checked in code only._
- **Unread dots clear when you're caught up.** Reaching the bottom reads a channel to its newest message, as Discord does, so a deleted last message, a jump that lands at the bottom or a channel too short to scroll no longer leaves the dot lit. Forums read when you open them and light up for new posts. Mark As Read (a channel, a server, the Inbox) and Mark Unread change the dots at once. Channels you never opened count as read up to when you joined, and servers using Channels & Roles light up only for channels in your list. _Checked in code only._
- **Friend, Ignore and Block in a person's menu.** Right-click someone or use their profile's More for Start a Call, Add Friend (Accept Friend Request, Remove Friend), Ignore and Block, with Discord's questions. The profile card has Discord's Add Friend button beside More, and people you ignore no longer notify you. _Checked in code only._
- **Channel menu:** Mute Channel for 15 minutes up to until you turn it back on, Notification Settings, Mark As Read only when there is something to read, and Invite People says the link was copied. _Checked in code only._
- **Server menu:** Show All Channels in community servers, and no Leave Server for the server's owner. _Checked in code only._
- **Messages:** Add Reaction opens the message menu, Discord links open in the room at the message, Pin and Unpin ask first (Shift skips), the pins list has Unpin, Esc marks the channel read, and typing reads "A and B are typing…". _Checked in code only._
- **Friends:** right-click a friend or request for the person menu. _Checked in code only._
- **Discord settings:** "Use data to personalize my Discord experience", Deauthorize asks first, "On servers I moderate" shows spoilers where you can manage messages, and AutoMod, Roles, Profiles, Members and Engagement use Discord's words. _Checked in code only._
- **Submenus stay open on the way.** In a person's menu, crossing another row on the way into Roles, Move to or Timeout no longer swaps the open submenu out from under the pointer. _Checked in code only._
- **Mod-only channels show at once and stay.** Exo took the first member Discord listed for a server as you, which could be someone else, so role-only channels appeared late or vanished after a reconnect; it now picks you by id and asks for your roles once after connecting. _Verified: on screen._
- **Friend requests from strangers.** Accepting a request from someone you share no server with asks first, as Discord does, then goes through; before, Discord refused it silently. _Verified: on screen._
- **"You accepted X's friend request."** The line Discord puts at the top of a new friend's DM shows as Discord's, not as a blank message from you. _Verified: on screen._
- **Server right-click menu.** Mark As Read, Invite to Server, Mute Server for a while, Notification Settings with Suppress @everyone and @here and Suppress All Role @mentions, Leave Server, Copy Server ID. _Verified: on screen._
- **Unread DMs on the rail.** Each DM with unread messages sits under Home with its count, as in Discord. _Verified: on screen._
- **DM menus.** A DM's right-click adds Mark As Read, Close DM, Mute @user and Copy Channel ID; a group DM has Mark As Read, Mute Conversation, Leave Group and Copy Channel ID. _Verified: on screen._
- **Failed messages stay.** A message Discord didn't take stays in the chat in red with why, Retry and Delete. _Checked in code only._
- **Blocked and ignored messages fold.** "3 blocked messages — Show messages", as Discord groups them. _Checked in code only._
- **Discord's tab has its own icon.** A chat icon in the top bar, so it no longer matches Friends inside Discord. _Verified: on screen._
- **Profile effects put on and come off** from the Shop and Profiles, as Discord's own Profiles saves them. _Verified: on screen._
- **Bio box** in Profiles no longer clips its first line under the rounded corner. _Verified: on screen._
- **Go Live at the right bit rate, and at 120 fps.** Each size and frame rate streams at H.264's usual best (8 Mbit/s at 1080p 30, 12 at 1080p 60, 24 at 1440p 60, 50 at 4K 60), and 120 fps is offered for screens that fast, up to 1440p; Source on a 4K screen streams 4K. _Verified: on screen._
- **Opening some DMs no longer breaks the room.** When Discord refused someone's profile, Exo passed its refusal on as the profile and the DM's side panel crashed; it now shows their name and picture. _Checked in code only._
- **GIF pictures hold still with Reduced Motion.** GIFs sent as pictures (not just GIF videos) stay on their first frame until you point at them when Reduced Motion or Performance mode is on, or Play GIFs is off. _Checked in code only._
- **Unread DMs on the rail always show their count,** as Discord's do. _Checked in code only._
- **Add a card and pay in Exo.** With no card on your Discord account, or to use another, the Shop's checkout adds one: your billing address, then the card in Stripe's own field (Discord's payment service), saved to your Discord account. A payment your bank wants to confirm shows its check over Exo, then completes, instead of sending you to Discord's website. Exo never sees the card number. _Checked in code only._
- **The mic closes between words again.** With noise suppression on, the automatic gate also follows what comes out between your words, so a steady sound suppression keeps (music or a game in the mic, a voice processed before Exo) no longer holds it open; it had stayed open almost all the time. _Verified: on screen._

### System
- **Best for this screen means all of it:** on NVIDIA it also sets No scaling on the GPU and Override the scaling set by games, and says In use only when everything matches. _Verified: on screen._
- **Compile times right after Scan** in System > Shaders. _Checked in code only._
- **Up to date without asking twice:** Upscalers reads your games again after Update all. _Checked in code only._
- **Every Starlight X shows everything,** on any firmware; a setting the mouse didn't report says why. _Checked in code only._
- **Driver installs report what happened.** After NVIDIA's installer finished, Exo read the version from the driver it had loaded at start and called a good install failed; it now reads the driver Windows records, so the install says Done and System shows the new version without a restart. _Verified: on screen._
- **Lucid's sounds and a roomier headphone picker.** With Lucid's notifications on, Switch sound and Game sound sliders set its cue volumes (0 is off); the headphone search opens as its own panel with taller rows. _Checked in code only._
- **Start Lucid with Exo stays on.** Saving settings copied each one by name and missed Lucid's, so the switch turned itself back off; every setting now survives a save (a test checks them all). _Checked in code only._
- **Driver updates clean up after themselves.** The unpacked driver (about 3 GB) is removed however an install ends, including when NVIDIA's installer stops or Windows permission is declined; leftovers from before are removed too. _Checked in code only._
- **Safer links.** A Steam link can open the store, a game's page, install or run a game, but never pass launch options or client commands. _Checked in code only._

### Accounts and setup
- **Equalizer APO's device picker opens in front** of Exo when you choose Lucid's devices, instead of behind it. _Checked in code only._
- **Lucid Voice stays off.** Exo Voice already cleans up your mic, and a second chain before it adds delay in calls, so Exo turns Lucid Voice off and keeps it off however it gets switched on, Lucid installed later included. A Voice Tune made through Lucid Voice is let go of then, since its gate was set for Lucid's louder sound. Lucid's headphone EQ is untouched. _Checked in code only._
- **Spotify, however it was installed:** installer, winget, Chocolatey, Scoop or a moved folder. The Microsoft Store edition offers Switch to Spotify's app, keeping your account and library. _Checked in code only._
- **No start-with-Windows switches.** Exo turns store clients, Discord and Spotify off at startup by itself, once, the way Task Manager does, and starts them when needed. _Verified: on screen._
- **Steam Switch** works on one click, shows each step, and brings up Steam's sign-in when an account needs it. _Checked in code only._
- **Riot Client closes** with its own quit for Add account and Switch; Vanguard and games are never touched. _Checked in code only._

### Look and feel
- **Snip without a flash, and Esc cancels:** starting a snip no longer flashes black over your screens, and a snip started from Exo takes the keyboard, so Esc cancels and Enter takes the whole screen. _Checked in code only._
- **Rooms stay as you left them:** coming back to Music or Discord no longer reloads the page, replays its slide-in or jumps to the top; the open chat keeps its messages and only fetches what is new. The Store keeps its scroll too. _Checked in code only._
- **Songs without the mood row:** the genre and mood chips over your Liked Songs are gone, so the list starts right under its title. _Checked in code only._
- **True black everywhere:** menus, panels and sheets are AMOLED black set apart by one thin line instead of a gray, see-through fill. Properties' section list sits inside its panel, as in Settings, and Devices matches. _Verified: on screen._
- **Black liquid glass:** buttons, fields, chips, the top bar's capsules, Discord's composer and search, and Exo Mouse's controls are one solid polished-black material with a light rim along the top, plainly visible on black. Fields sit recessed, a press sinks, and Play, Get on Steam and Confirm stay cream. _Verified: on screen._
- **Setup is Exo's own window:** black and borderless, with the Exo mark, one Install button, a thin progress bar and Launch Exo; if setup fails, the reason shows on its last screen. Setup, the uninstaller, shortcuts and Apps & features all show the current Exo mark, sharp at every size, and in-app updates stay silent. _Verified: on screen._
- **Rooms come back loaded:** their arrival animation plays only the first time. _Checked in code only._
- **No flicker:** Settings opens with one float-in, and pages never stretch mid-transition. _Checked in code only._
- **No light rim** on the rounded corners of game art. _Checked in code only._
- **One look for every field,** words on true center. _Checked in code only._
- **Captures favorites:** the star turns at once and a Favorites chip shows every starred capture. _Checked in code only._
- **Clicks never get lost:** a click while a page is still moving lands on what you pointed at, opens it and stays open, and a page can never freeze mid-move. _Verified: on screen._
- **Downloads live on the cards:** the top-bar download pill and the floating download list are gone; each game card shows its own progress ring and Update tag. _Checked in code only._
- **Settings panels are evenly inset.** Exo's and Discord's floating settings keep 12px all round, and the boxes inside follow the panel's corners. _Checked in code only._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.11.0/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
