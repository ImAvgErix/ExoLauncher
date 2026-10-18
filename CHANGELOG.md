# Changelog

## 3.9.0 — October 17, 2026

This update makes Exo's own Discord client look and work like Discord, with end-to-end encrypted voice calls, noise suppression, friends, notifications, files, GIFs, emoji and reactions.

### Discord
- **Laid out like Discord.** Servers down the far left with their names on hover, Direct messages on top and Add a Server at the bottom; a server's channels with folding categories, channel icons, unread marks and who is in each voice channel; your DMs with a search and Friends; you at the bottom with mute, deafen, settings and your status. The conversation has Discord's header (call, pinned messages, member list, search, Inbox), the member list down the right grouped by role in each role's color, names in their role color, date lines, Discord's red NEW line, "Today at" times, and the hover bar (react, reply, edit, More), also on right-click. In Exo's darker palette and Inter. _Verified: on screen._
- **Voice calls.** Click a voice channel to join it, or Call in a DM (it rings the other person); an incoming call shows a Join bar and a Windows notification. Calls are end-to-end encrypted with Discord's own DAVE library and connect in under a second; the sound goes straight between Windows and Discord's voice server. Video and screen share stay in the Discord app for now. Joining, the encryption and leaving _Verified: on screen._ Hearing and being heard _Checked in code only._
- **Noise suppression in calls.** DeepFilterNet 3 cleans keyboards, fans and room noise out of your microphone on this PC before anything is sent, faster than real time. It downloads once, checked against its pinned fingerprint, the first time the client starts; turn it off in Settings > Music and Discord. _Checked in code only._
- **Friends and status.** Friends shows who is online and what they're doing, all your friends and pending requests with Accept and Ignore, and Add Friend by username; a profile card opens from any name or avatar with Message on it. Pick Online, Idle, Do Not Disturb or Invisible from your name. _Verified: on screen._
- **Everything you do in a chat.** Send, reply (the quote jumps to the original), edit, delete, react, pin, copy text or a message link; attach files with +, paste a screenshot or drop files (up to 25 MB, 10 at a time); Discord's GIF picker; stickers; every server's emoji, with ones Discord would only allow with Nitro sent as their picture; :name: and @name suggestions; search; pinned messages; pictures open full size. Forum channels list their posts, and a post opens like a channel. Join a server with an invite and leave one from its menu. _Verified: on screen._
- **Notifications and the unread badge.** A DM or a mention while you're not looking at that conversation shows as a Windows notification; clicking it opens the conversation. Muted servers and channels stay quiet. Your unread count shows on the Discord tab and Exo's taskbar button. _Checked in code only._

## 3.8.0 — October 17, 2026

This update adds Exo's own Discord client as a preview, NVIDIA's newest driver installed clean and driver only, NVIDIA Control Panel's display settings in GPU > Display, SDR that looks right while HDR is on, and a new opening that has everything of Exo's own ready before the library shows.

### Faster
- **A new opening.** Exo opens on black, its mark settles in at full size and its three bars sweep in one after another, then it holds while Exo loads Store, Devices, Settings, the GPU sheet with every tab and Exo Shaders' page, and the first screen of covers, and sinks back as the library comes up. It always plays in full, once, at your screen's own frame rate; no text or flicker before it, and no loading line in the top bar. Nothing of Exo's own builds on first open: the GPU sheet opens in a frame. Discord and music still start with their rooms. _Verified: on screen._
- **Exo Shaders is half the size.** Its engine and command-line tools share one copy of .NET instead of two: about 119 MB on disk from 234 MB. _Verified: end-to-end._
- **Exo Shaders is ready when you are.** It starts with Exo again, like Exo Capture and Exo Mouse, so its page opens at once and it sees every game you play. _Verified: on screen._

### Fixes
- **Exo's menus show over Discord and music apps.** The Capture menu (and every other menu) opened over Discord's or a music app's window was hidden behind it; that window now gives way under any open menu. _Verified: on screen._
- **Store pages open whole.** Games on screen in the Store are read ahead in the background, a few at a time, with their art and other stores, so a page opens with its tags, description and buttons already there. _Verified: on screen._

### Changes
- **The NVIDIA driver, driver only.** GPU > Profile shows your driver and NVIDIA's newest Game Ready driver for your card. Update (or Reinstall) downloads it from NVIDIA, checks NVIDIA's signature, and clean-installs the driver alone: no NVIDIA app, telemetry, ShadowPlay, PhysX or HDMI/DisplayPort audio. Exo puts your GPU profile and desktop color back afterwards and waits while a game runs. Your display settings (scaling, override, output color) stay as they were. _Verified: on screen._
- **Display has NVIDIA Control Panel's settings.** Output color is Default (the driver picks) or NVIDIA settings with format, depth and range; Scaling is Aspect ratio, Full-screen, No scaling or Integer scaling, done on the GPU or the display, with Override the scaling set by games; brightness, contrast, gamma, digital vibrance and hue are sliders (moving one hands color to NVIDIA, Restore hands it back); RTX Video Super Resolution (Off, Auto, Quality 1-4) and RTX Video HDR where the card has them. Each change to the picture still waits 15 seconds for you to keep it. The GPU sheet is larger and Display is in two columns, so nothing scrolls. _Verified: on screen._
- **Exo's own Discord client (preview, off by default).** Turn it on in Settings > Music and Discord and the Discord room becomes Exo's own client: your servers and DMs down the left, unread and mention marks, channels in Discord's order, and messages with formatting, emoji, images, videos, link previews and stickers. Send, reply, edit and delete your own messages; Exo marks what you read and shows who is typing. Sign in by scanning a QR code with Discord's phone app, so Exo never sees your password; the sign-in stays on this PC, encrypted for your Windows account, and Sign out ends it. Only channels your roles let you see are shown, and nothing is saved. Calls, video and screen share stay in the Discord app for now. Discord's terms don't allow other clients, so it's at your own risk. _Checked in code only._
- **SDR looks right in HDR.** While a screen is in HDR, Exo gives it a color profile that draws the desktop, overlays and SDR windows with gamma 2.2 like an SDR screen, so blacks stay black instead of gray; HDR content above SDR white is untouched. The screen's own profile comes back when HDR goes off. On by default in GPU > HDR. _Checked in code only._
- **One kind of control.** Every setting with choices is a dropdown, however many it has; on and off is a switch, an amount is a slider. _Verified: on screen._
- **Everything on one line.** Settings' section headings and their buttons line up with the rows under them, and every row is the same height. _Verified: on screen._
- **About lights up** in Settings' list when you reach the bottom. _Verified: on screen._
- **Exo Shaders' info window opens whole,** never on a spinner first. _Verified: on screen._
- **No scrollbars anywhere,** including the GPU and Captures sheets, where one flashed while switching tabs. Everything still scrolls. _Checked in code only._
- **Exo Shaders' cache and about** are behind an "i" button in its toolbar, in one window; the gray line at the bottom of the page is gone. _Verified: on screen._
- **"Color"** everywhere, in US spelling. _Checked in code only._
- **Time played is just the time.** No "From tracker.gg" or other source note under it. _Checked in code only._

## 3.7.0 — October 17, 2026

This update adds the GPU room with Exo's own NVIDIA profile for your card, HDR only for games that really use it, a lighter Exo that starts nothing it doesn't need, and Inter as Exo's typeface.

### GPU
- **One GPU button.** Library's Upscalers button is now GPU, with Profile, Display, HDR, Upscalers and Shaders in one place. Settings > Graphics is gone. _Verified: on screen._
- **Exo's profile for your card.** Use Exo's profile writes the best NVIDIA settings for your card's series (GTX 10 and 16, RTX 20 to 50, and workstation cards by generation) for the lowest latency and most performance: maximum performance power, low latency on, High performance texture filtering, the newest DLSS files for every game, and the best DLSS model for your card (Recommended on RTX 40 and 50, K on RTX 20 and 30, frame generation C on all). Pick G-SYNC or No G-SYNC; with G-SYNC the frame cap follows your refresh rate, even when it changes. Frame generation switches stay yours. Undo puts your old settings back. _Checked in code only._
- **Import a Profile Inspector file** from the Profile tab, as before. _Checked in code only._
- **Display shows only what your screen takes.** Color format, depth and range offered only where the screen supports them, the best one picked for you, and every change kept only if you confirm within 15 seconds. NVIDIA color settings (brightness, contrast, gamma, digital vibrance and hue) sit beside them. _Verified: on screen._
- **Exo Shaders on one page,** with its shader cache and queue in their own dialogs. _Verified: end-to-end._

### HDR
- **HDR only for games that use it.** Exo checks your screen first: how bright it really gets and whether Windows HDR is calibrated. Then it turns HDR on only while a game that supports HDR runs (from PCGamingWiki), and puts your desktop back after. Each game page has Auto, Always or Never. _Checked in code only._

### Capture
- **Snips open the editor** straight away, and you can delete captures from the grid. _Checked in code only._
- **Game, Discord voice and microphone are always kept on their own tracks,** with the mix as the first track, so any player plays it. _Checked in code only._
- **Smarter encoder choice** on NVIDIA, AMD and Intel cards, and HDR snips stay HDR. _Checked in code only._

### Lighter
- **Nothing starts with Exo just to be ready.** Steam is no longer started to check what you own: games Exo confirmed before stay yours, and Steam starts when you play, install, update, verify or switch accounts. Discord starts when you open its room, Music when you open Music, and Exo Shaders when you open its page or a driver update leaves games to rebuild. A Discord or music app you already have open is still picked up at once. On this PC, with Steam closed, Exo and what it starts went from about 1.4 GB of memory at rest to about 0.6 GB. Discord, Music and Exo Shaders _Verified: on screen._ Steam staying closed _Checked in code only._
- **Idle music lets go.** A player that has slept for 15 minutes with nothing playing is closed (the web player, or a music app Exo started; never one you opened), and opens again with Music. Exo Shaders stops once it has nothing to do and its page is closed. _Checked in code only._
- **Trailers stop off screen.** A Store trailer stops and lets go of its video when you leave the Store or minimize Exo, and starts again when you come back. _Checked in code only._
- **Smaller and quieter.** The installer is about a quarter smaller (64.6 MB from 87.6 MB), Exo uses about 30 MB less memory sitting on Library, and it does about a quarter of the background work while idle: one check for games started outside Exo instead of three every five seconds. _Verified: end-to-end._

### Look
- **Inter everywhere.** Exo, Exo Mouse and Exo Capture's windows use Inter in place of Geist. _Verified: on screen._

### Changes
- **tracker.gg is only for Riot games.** Rainbow Six Siege and Destiny 2 hours come from Steam. _Checked in code only._
- **Optimize is always on.** The Optimize rows are gone; the fixes they applied stay applied. _Verified: end-to-end._

## 3.6.0 — October 17, 2026

This update adds Exo Capture for instant replay, recording and snips, your game's NVIDIA graphics settings, more than one account per store, and hours only from where they are really kept.

### Capture
- **Exo Capture.** The Capture button in the top bar saves an instant replay, starts or stops a recording, or takes a snip: drag a rectangle, then copy or save it from the card that appears. Hotkeys do the same. Instant replay runs only while a game is running, by default, and holds the last minutes in memory until you save. _Verified: on screen._
- **Captures sheet.** Every replay, recording and snip, by game, with a player, trim, favorites, Send to Discord (copies the file for you to paste), and a storage limit that never deletes favorites. Trim _Checked in code only._ The rest _Verified: on screen._
- **Settings > Capture**, beside Graphics: frame rate (60 fps, or 120 where your screen runs that fast), quality, length of the replay, which sounds to keep (game, Discord voice, microphone, each on its own track), the folder and hotkeys. Video is encoded on the graphics card (HEVC, or AV1 where the card has it; HDR games stay HDR). HDR and AV1 _Checked in code only._
- **Replay follows the game however you start it.** A game started from Steam, a desktop shortcut or anywhere else turns replay on, not only games started from Exo. _Verified: on screen._
- **No outline in your clips.** Exo asks Windows to capture without the yellow border, where Windows allows it. _Checked in code only._
- **Light on games.** Exo Capture reads Windows' capture frames directly instead of copying each one, takes no more frames than the clip keeps, and gives the game's frames first claim on the graphics card; the game keeps its own fast path to the screen. In Hell is Us at 1080p on an RTX 3070, with the card at full load, replay at 60 fps (the default) cost about 3% of average frame rate (213 to 207), and at 120 fps about 5%, down from 7%; Exo Capture used under 1% of the processor and about 150 MB of memory. _Verified: end-to-end._
- **Games Windows cannot capture.** A game in exclusive full screen gives Windows capture no picture; after five seconds without one, Exo Capture takes the frames from inside the game with OBS Studio's own signed game capture hook, injected the way OBS's anti-cheat compatible mode does. Never for Riot games, while FACEIT or ESEA runs, or when another app like OBS already captures that game. The hook itself _Verified: end-to-end._ The switch to it _Checked in code only._

### Graphics
- **Your game's NVIDIA profile on its page.** A game's page shows the settings the NVIDIA driver holds for it (low latency, frame rate limit, V-Sync, texture filtering, DLSS overrides and more), each with Driver default to put NVIDIA's value back. Settings > Graphics holds the same for every game. _Verified: on screen._
- **DLSS rows only where they apply.** A DLSS row shows only on a game that ships that DLSS file. _Verified: on screen._
- **Import a Profile Inspector file.** Import profile reads a `.nip` file and lists what it would change before anything is written; Undo import puts back what it replaced. _Checked in code only._
- **AMD and Intel** cards show what their driver reports, read-only. _Checked in code only._

### Accounts
- **Who you are signed in as.** Settings > Game stores shows the Steam, Epic, GOG and Amazon accounts on this PC. _Verified: on screen._
- **Switch Steam accounts in one click.** Pick another account Steam remembers and Exo restarts Steam signed in as it. _Checked in code only._
- **More than one Epic account.** Add an account, switch to it, or bring in one Epic already knows. _Checked in code only._
- **Hours from all your accounts.** Turn on "Count hours from all my accounts" and a game you play on two accounts, like Rocket League on an alt, shows the hours from both. Rocket League's Epic hours _Verified: on screen._ Adding up two accounts _Checked in code only._

### Hours
- **Only real hours.** Hours come from the store or the game itself, never from Exo's own timing; a game with no real source shows none, rather than a guess. Hours can now also go down when the store says so, so a stale figure no longer sticks. _Verified: on screen._
- **More sources.** Steam and GOG first, then GOG Galaxy 2.0's database for games on platforms you connected to it, then tracker.gg, now for Rainbow Six Siege and Destiny 2 too, and Xbox with an OpenXBL key you paste. Each figure says where it came from. _Checked in code only._
- **Battle.net last played.** Battle.net games show when you last played them. _Checked in code only._

### Fixes
- **Instant replay survives alt-tab.** Minimizing a game no longer throws away the replay it was holding. _Verified: on screen._
- **The capture storage limit only removes Exo's own captures,** and sends them to the Recycle Bin. With the save folder set to Videos, it could permanently delete your other videos. _Checked in code only._
- **Saving an edited snip works for full-size screenshots.** Pictures over about 750 KB never saved. _Checked in code only._
- **The capture quality slider shows where it is set,** and has a focus ring. _Verified: on screen._
- **Exo Mouse runs on its own address,** so the Devices add-on can only reach the mouse functions Exo gives it, never the rest of Exo. _Verified: on screen._
- **Mouse profiles are never wiped by a failed read,** and a game profile the mouse only partly takes says so. _Checked in code only._
- **Accounts stay the account you chose.** After an Epic or GOG switch or sign-out, the next sign-in no longer brings back the old account. Signing out of GOG no longer falls back to Heroic's account. Amazon shows only the signed-in account's games. Exo no longer spends Epic's sign-in tokens itself, which could sign you out of Legendary or the Epic Games Launcher. _Checked in code only._
- **Hours are counted once.** A game on two stores no longer counts the same tracker.gg or GOG Galaxy figure twice after a background refresh. Hours from a mistyped Riot ID go away when you correct it. Steam hours stay when Steam is closed. GOG games get their last played date from GOG Galaxy. _Checked in code only._
- **Stop leaves your other programs alone.** Stopping a game only ends processes from the game's own folder, never a browser or Discord it opened. EA's anti-cheat launcher and GameGuard are never ended. _Checked in code only._
- **Upscaler and Discord changes are safe against interruptions.** A DLSS backup is verified before the game's file is replaced, and linked FSR files are put back together if one fails. DLSS changes wait while the game's store is updating it. Discord can no longer be left without its app files. _Checked in code only._
- **Smaller fixes.** Queued badges show on cards owned on two stores, Store shows ™ titles as in your library, Ctrl+K no longer lists hidden extras, the Store home no longer hangs after a network drop, Riot banner art loads, and a broken download-queue file no longer stops Exo from starting. _Checked in code only._

## 3.5.5 — October 17, 2026

This update shows Fortnite as one game.

### Library
- **Fortnite is one card.** The new Epic Games Launcher lists Fortnite's modes, like LEGO Fortnite Odyssey and Save the World, as their own installs with no name, so each showed as an extra card titled with a code. They now fold into Fortnite's card, and their hours count toward it. The same goes for any Epic game whose modes install this way. _Checked in code only._

## 3.5.4 — October 17, 2026

This update counts hours for Minecraft and Roblox, keeps Fortnite's hours moving, finds more of your owned Steam and Epic games, and lets Update all in Upscalers include anti-cheat games at your risk.

### Library
- **Every owned Steam game shows up.** On a Steam install that has seen many store pages, Exo asked Steam about only the first 20,000 apps it knew of and gave up after four seconds, so owned games past that point never reached "owned, not installed". Exo now asks about every app in batches, keeps the answers it got if Steam slows down, and keeps games it verified before until Steam answers for them. With a Steam Web API key, an owned app Steam has no details for on this PC is shown once the Steam store says it is a game. _Checked in code only._
- **Games that share a name are both kept.** Two games with the same title in one store, like Prey (2006) and Prey (2017), no longer collapse into one. _Checked in code only._
- **Every owned Epic game shows up.** Exo now also lists Epic games another store delivers, such as ones activated on the EA app; Install opens Epic Games Launcher, which sends you there. Deluxe and other editions are no longer dropped as add-ons, and a large Epic library has five minutes to list on the first run instead of 90 seconds. _Checked in code only._
- **Hours for Minecraft and Roblox.** Exo now sees Minecraft (Java and Bedrock) and Roblox while they run, even when you start them outside Exo, and counts their hours. Roblox's earlier sessions come from its own logs and Minecraft Java's from each singleplayer world's stats, once. Roblox _Verified: on screen._ Minecraft _Checked in code only._
- **Fortnite's hours keep going up.** For Epic games Exo dropped its own session time whenever Epic reported any hours, so hours froze when Epic's figure did. Exo now adds the time it saw since Epic's last figure, without counting anything twice. _Checked in code only._
- **Hours no longer creep up on their own.** A background refresh could add Exo's minutes again to totals that already held them, for Riot and other non-Steam games. _Checked in code only._
- **Real covers for games no store sells.** osu! and other local games looked up their art on Steam by title, found nothing (or a different game) and fell back to their icon. With a SteamGridDB key saved, Exo now finds them on SteamGridDB by their exact title. _Checked in code only._
- **Update all can include anti-cheat games.** In Upscalers, with anti-cheat games waiting, Update all first names each game and its anti-cheat and says the ban risk plainly, then updates them too at your risk, or only the others. Riot games are never changed. _Checked in code only._

### Devices
- **Exo's own dropdowns.** Every dropdown in Devices opens Exo's menu instead of Windows' list. _Checked in code only._
- **Each fact once.** Under the mouse: battery, signal, a fixed polling rate and firmware; sensitivity and a polling rate you can change stay in their panels only. _Verified: on screen._

### Updates
- **Updates are not blocked by programs Exo started.** A store launcher Exo opened kept Exo's folder in use after Exo closed, which could stop the next update from swapping it. _Checked in code only._

## 3.5.3 — October 17, 2026

This update redesigns Devices so every mouse's settings fit on one screen, in order, and fixes linking games to a profile.

### Devices
- **Every setting on one screen, in order.** Settings now sit in sections (Sensor, Buttons, Device, Profiles), whole and in the fewest even columns that fit without scrolling, so a mouse with many settings, like a Razer Viper V3 Pro, no longer spreads them across the room. Every setting a mouse can change still shows, its own extras included; the mouse picture takes the room that is left. _Verified: on screen._
- **More at a glance.** Under the mouse: battery, signal (or how it is connected when it reports no signal), polling rate, sensitivity with its stage, and firmware. _Verified: on screen._
- **Games show up when linking a profile.** Choosing Games on a profile opened the list inside the profile's card, so a short card showed no games at all; it now opens over the whole room. _Verified: on screen._
- **Razer lift-off mode stays chosen.** Choosing Asymmetric on a Razer mouse could fail without a word when the mouse held lift-off and landing values Razer's own software never writes; Exo now starts from the nearest valid pair, so the choice sticks. _Checked in code only._

## 3.5.2 — October 17, 2026

This update brings Razer mice into Devices.

### Devices
- **Razer mice show up.** A Razer mouse such as the Viper V3 Pro never appeared in Devices: Razer keeps its settings on the part of the mouse Windows reserves for moving the pointer, and Exo stayed away from it. Exo now reaches that part for the mouse's settings only, the way OpenMouse Bridge and Chrome do, while the pointer stays Windows' own and keyboards are never touched. _Checked in code only._

## 3.5.1 — October 17, 2026

This update closes the holes a security review of Exo found, keeps Exo further away from anti-cheat, and makes text sizes and hover effects match across the app.

### Security and privacy
- **Spotify can only be reached by Exo.** Exo used to talk to the Spotify it hosts over a debug port on your PC, which another program running as you could find and use to drive your signed-in Spotify. Exo now talks to it over a private connection only Exo holds; there is no port to find. _Verified: on screen._
- **The mouse page stays in its lane.** Exo Mouse, which runs inside Devices, can reach only its own mouse, profile and game-list calls. A message it sends straight to Exo is now refused instead of reaching the rest of Exo. _Checked in code only._
- **Logs hide secrets that carry no label.** Exo's log already hid tokens and passwords written next to a label; it now also hides sign-in tokens written on their own, while download checksums, links and file paths stay readable for support. _Checked in code only._
- **Every attack from the review is kept as a test.** Dangerous links, requests aimed at your PC or network, paths that try to escape a folder, tampered updates and corrupt files are all refused, and each is now a test that runs on every change. _Checked in code only._

### Games
- **More anti-cheat Exo never closes.** When Exo has to stop a game, it now leaves FACEIT, PunkBuster, XIGNCODE3, GameGuard, ACE, EA's anti-cheat, Riot's Vanguard tray and every game's own BattlEye or Easy Anti-Cheat launcher running, in the whole game process tree, not only the top process. A close it refuses is no longer counted as done. _Checked in code only._
- **A broken upscaler list can't get in the way.** If the list of newer DLSS, FSR and XeSS versions arrives damaged, Exo keeps the versions it ships with. _Checked in code only._

### Look and feel
- **One set of text sizes.** Settings, game pages, Library shelves, first run, Upscalers and the Activate key popover now use Exo's three text sizes, so nothing stands a size apart. _Verified: on screen._
- **Hovers never fade.** The music track list and the command menu's Clear search now light up with Exo's gray hover instead of turning see-through, like every other control. _Verified: on screen._
- **DLSS preset names fit.** Ray Reconstruction presets D and F read on one line. _Verified: on screen._

## 3.5.0 — October 16, 2026

This update makes Music Exo's own player for Spotify, Apple Music and YouTube Music, with Jam, Friend activity, lyrics and your whole song library; ships Exo Shaders inside Exo; adds a Library > Upscalers sheet with DLSS presets; rebuilds Devices around your mouse; speeds up the Store and Discord; gives memory back from Steam, Epic and other store apps sitting behind Exo; and moves every room the same way. Equicord with Exo's plugin set, FakeNitro included, is now always on for Discord.

### Music
- **One Exo player for Spotify, Apple Music and YouTube Music.** The Music room is Exo's own player for all three: your library down the left, Home, search, playlists, albums, artists and podcasts, a queue you can jump around in, like, shuffle and repeat, in Exo's black look. Each service still plays the music itself, hidden behind Exo and drawing nothing while it does, so your account, downloads and audio quality are the service's own. The Apple Music app for Windows is no longer needed: Apple's own web player does the playing. _Verified: on screen._
- **Every song you saved, one click away.** Songs sits under Home: Spotify's Liked Songs, Apple Music's library songs, YouTube Music's library songs, in one list that scrolls smoothly through thousands. Sort by date added, title, artist or album, narrow it with Filter, and play from any song; it plays on in the order you see. _Verified: on screen._
- **Jam in Exo.** Start a Spotify Jam from the player strip, copy its invite link, see who joined, remove someone, and leave or end it. _Checked in code only._
- **Friend activity.** See what the people you follow on Spotify are playing, with a green Now for anyone listening right now. _Verified: on screen._
- **Lyrics that follow the song.** Now playing shows the song large with its lyrics; on Spotify and Apple Music the sung line lights up and stays in the middle, and clicking a line jumps there. _Verified: on screen._
- **More of your library.** Spotify's own filters (Playlists, Podcasts, Albums, Artists) and sort orders, folders that open in place, Home that starts with Recently played, podcasts with their episodes, and search that finds podcasts. Each song's menu adds it to the queue, likes it, adds it to a playlist or takes it out of yours. _Checked in code only._
- **Covers that are right.** A playlist without its own picture shows its four-cover mosaic, YouTube Music's mixes and built-in playlists show theirs, and anything without a picture shows a quiet note instead of an empty square. _Verified: on screen._
- **Signing in looks like Exo.** Signed out, the Music room shows the service's mark and one Sign in button. You sign in on the service's own page or window; Exo never sees your password. _Checked in code only._
- **A steadier, faster Music room.** A page stays put until the next one has loaded, covers keep their picture until the new one is ready, pages you have opened come back at once, and the mini player keeps its cover when you skip. An idle, paused player wakes the moment your pointer reaches the mini player or the Music tab, so the first click lands at once, and the volume stays live while music is paused. _Verified: on screen._

### Discord
- **Discord's settings answer at once.** Changing text size, zoom or density in Discord took seconds and could swell Discord past 5 GB: Exo's reduced-motion rules gave every element a transition on everything. Exo now only shortens animations, in Discord, Spotify, Cider and YouTube Music. _Verified: on screen._
- **Equicord is always on, FakeNitro included.** Discord in Exo always runs Equicord with Exo's plugin set: no Quests, Nitro or Shop clutter, no tracking, lighter drawing, and FakeNitro (any emoji and sticker, sent as a link where Discord would refuse it, and source-quality streams). Discord's terms forbid client mods and FakeNitro; turn FakeNitro or any plugin off in Equicord's own plugin list if you'd rather not. Settings no longer has Optimize rows: Exo optimizes every hosted app by itself. _Checked in code only._
- **Discord in Exo's ink, at its own size.** Discord's text and icons use Exo's cream and gray; its font, size and zoom are Discord's own, set in Discord's settings. The Wave buttons in the DM list are hidden. _Checked in code only._
- **Discord starts faster and stays out of the way.** Discord keeps its compiled shaders and script cache between starts, no longer takes the foreground when it starts behind Exo or a game, no longer writes voice debug logs, and its unread count reaches the Discord tab and Exo's taskbar button the moment it changes. _Checked in code only._

### Store
- **Game pages that fit and open fast.** A page opens in about half a second and never scrolls: trailers and screenshots on the left, buying, the description, details and System requirements on the right, Steam Deck and tags under the logo. Get on Steam shows at once and the price fills in; other stores follow a moment later. A game you point at is already loading when you click it. _Verified: on screen._
- **Prices in your currency, more on every card.** Steam prices in your Windows region's currency, review scores, Steam Deck ratings and top tags. On sale is Steam's own regional specials list. _Verified: on screen._
- **A calmer Store.** Shelves rise in once, the banner fades between games, Refresh shows a band of light while it works, On wishlist keeps a green check, and buttons never jump. Store links open in the store's own app when it is installed. The Store tab counts wishlisted games on sale that you don't own. _Verified: on screen._

### Library and games
- **Library > Upscalers.** Every installed game with older DLSS, FSR or XeSS than Exo has, as posters with their version jumps, and Update all. Exo reads DLSS Swapper's public list of builds once a day and uses a build only after it passes Exo's own checks (the vendor's signature, exact version, size and checksum); Restore puts the game's own files back. A running game waits until it closes, an anti-cheat game changes only once you allow it on its page, and Riot games never appear. _Verified: on screen._
- **DLSS presets.** On an NVIDIA PC, set the driver's forced preset for DLSS Super Resolution, Ray Reconstruction and Frame Generation, with what each model is good at and the best one for your card marked, and turn on NVIDIA's own DLSS overlay (Windows asks for administrator permission). _Verified: on screen._
- **Exo Shaders ships with Exo.** Library > Shaders is Exo Shaders, Exo's shader precompiler (a fork of SCSKiller, GPL-3.0-or-later, source at github.com/ImAvgErix/exo-shaders), now inside the installer. Each game shows one word of state; the queue, cache and About are one list each. _Verified: on screen._
- **Refresh the library.** A round button next to + reads every game again: sizes, every store's games, time played, last launched, Riot hours, achievements and art. A shine crosses the cards while it works. _Verified: on screen._
- **A cleaner game page.** The store sits centred above the cover, Time played, Last launched, Size and Achievements sit in one row, and buttons never make the page jump. Double-click an installed game to play it, and Ctrl+1 to Ctrl+5 open the rooms. _Verified: on screen._
- **Riot and Xbox.** Riot games installed anywhere are found, show Update when Riot needs to patch them and exact sizes; Teamfight Tactics is in the Library. Xbox games use their real names, Microsoft Store art and sizes. _Checked in code only._
- **Achievements and last launched.** Achievements show on the game page (Steam's own summary first, so they come in fast), and a game started from any store app moves up in Last launched. _Checked in code only._

### Devices
- **Devices is built around your mouse.** A large picture of your mouse with its battery (and voltage or charge state), signal and its strength, polling rate and firmware, each shown once; its settings in compact cards beside it. A change shows a band of light on the mouse while it is written and a check when it lands. _Verified: on screen._
- **More of the Finalmouse Starlight X.** Each main switch has its own release point, lift-off has a custom distance from 1.0 to 1.7 mm, the receiver light adds TMR-DS and a custom color, and Tournament scroll is Bhop mode, its new name. _Verified: on screen._
- **Quieter behind a game.** With Devices hidden, Exo Mouse reads only the battery, every five minutes. A mouse that reconnects mid-game gets the game's profile back. _Checked in code only._

### Speed, memory and feel
- **Store apps give their memory back.** While Steam, the Epic Games Launcher, the EA app, Ubisoft Connect, GOG Galaxy or Battle.net sits hidden behind Exo, Exo hands the memory their built-in browser holds back to Windows. On a test PC Steam's helpers fell from about 280 MB to under 80 MB. Nothing is closed: games, downloads, the overlay, Steam Input, cloud saves and achievements work as before. _Checked in code only._
- **Lighter behind your games.** Spotify runs without hardware acceleration behind Exo (about 160 MB lighter) with its logging and notifications off, Exo loads 1.4 MB less script at startup, and Exo's background checks do the same work with a fraction of the CPU. _Checked in code only._
- **One motion and one look, everywhere.** Views rise in once, work shows a soft band of light, done draws a check, every selected pill glides to what you picked, and Reduce motion turns it all off. No button turns see-through on hover. Settings says less, and Text size reaches every room. _Verified: on screen._
- **Exo never gets stuck behind itself.** Exo started without the foreground could think it was in the background and stay throttled; it now checks Windows every few seconds. _Checked in code only._

## 3.4.0 — October 15, 2026

This update adds a Store, brings Exo Mouse to the Devices room, hosts SCSKiller's own app for shaders, gives downloads pause, play-time holds and download hours, reads Epic's new launcher, removes the Exo account, keeps Discord and Equicord current with a better plugin set, finishes the black themes in every music app, fixes the mini player volume, and rebuilds the upscaler swapper around official, verified files. It never changes files in games with anti-cheat unless you allow it for that game; Riot games are never changed.
- **No more Exo account.** Sign-in, sign-up, handles and badges are gone, along with Settings > Exo account and its Download and Delete account buttons. Setup is two steps, your game stores then Music and Discord, and Settings is six sections. Your library, playtime, settings and keys never depended on the account and work exactly as before. Steam, Epic, GOG, Discord and your music app keep their own sign-ins.
- **Your old sign-in is cleaned off this PC.** The first time this version starts, it deletes the sign-in an earlier Exo saved (`auth.bin`) and its cache of account responses. It does this on your PC only and sends nothing to the account service. If you made an account before, the [privacy policy](PRIVACY.md) says what the service still holds and how to delete it.
- **Exo Mouse in the Devices room.** Devices now shows Exo's own mouse settings, in Exo's look. The top line is your mouse: name, brand, connection and signal, and battery. Below it, only what your mouse has: DPI and DPI stages, polling rate, lift-off distance, motion sync, angle snapping, ripple control, click debounce, power mode, onboard profile, button remapping, lighting and the sleep timer. A setting your mouse fixes shows as its value. Every change is written to the mouse and read back. With more than one mouse plugged in, a pill switches between them.
- **No other app needed.** Exo Mouse talks to your mouse directly and works with every mouse OpenMouse supports, dozens of brands. It needs no OpenMouse app, no vendor software and no driver.
- **Built on OpenMouse, open source.** Exo Mouse is built on OpenMouse's open-source protocol library (AGPL) and is its own open-source program under the same license, at [github.com/ImAvgErix/exo-mouse](https://github.com/ImAvgErix/exo-mouse). Exo Launcher keeps its own license.
- **The Finalmouse Starlight X works.** OpenMouse's library could not read a Starlight X at all: it ignored how the receiver reports DPI and polling rate. Exo Mouse fixes that and adds the Starlight X's own controls: mechanical or TMR analog main switches per side, actuation from 0.01 to 0.40 mm, rapid trigger from 150 to 250 µm, release point and its onboard profiles (only the ones turned on), plus the receiver light, tournament scroll and passthrough window. Polling shows as 1000 Hz with no choice, since PerfectPolling fixes it; lift-off offers the 1 mm and 2 mm presets, and a custom lift-off set in xpanel shows as its value. Exo reads the mouse no more often than its driver asks, every 10 seconds.
- **Your mouse, pictured.** Devices shows your mouse's own render beside its settings (from OpenMouse's image library, for the models it has), with its name, link and battery under it.
- **Your mouse, looked after.** When its battery drops to 20% Exo says so once (at most every 6 hours) and shows a small battery chip by the gear that opens Devices, until you charge it. A signal that stays weak gets one line saying to move the receiver. Devices shows the signal as bars and a word. A game-linked profile says when it comes on and when your settings go back. All of this works with Devices never opened: the room starts quietly a few seconds after Exo.
- **Mouse profiles with names.** Save current stores everything Exo can set on your mouse under a name, as many as you like. Apply puts one back, the one that matches your mouse right now says Active, and Rename and Delete are beside it. Desktop is built in and always the default: your mouse goes back to it whenever no linked game is running, and Update saves your current settings into it. Link any other profile to games, picked by their covers, and it comes on when one starts, with or without the Devices room open. A switch made while a wireless mouse sleeps lands as soon as it wakes. Switching takes a fraction of a second. Profiles are saved in Exo's own data folder, and ones saved by an earlier build move there by themselves.
- **The OpenMouse web panel and Exo's own bridge are gone.** Exo no longer loads the web panel or answers on OpenMouse Bridge's port, the game page no longer shows a mouse profile line, and Settings > Devices is gone. If you had switched to Exo's bridge, the first start of this version writes the profiles Exo kept back into the OpenMouse Bridge app's own settings. It does not start the app or put it back on Windows startup; Exo Mouse does not need it.
- **Discord stays optimized after it updates itself.** When Discord installed an update, Equicord carried itself into the new version, and Exo took that for an Equicord you had installed yourself: it stopped applying its settings, Settings said Equicord was installed outside Exo, and Undo could not take it out. Exo now recognizes its own Equicord in the new version and puts its own loader back at the next start, and Undo returns Discord to stock either way. If Equicord's file goes missing, Discord starts as Discord ships it while Exo downloads it again, instead of not starting at all.
- **Run lets Discord update first.** If Discord has an update waiting, Optimize Discord > Run lets it install before Equicord goes in, and says so. If Discord installed an update while it was open, Run's restart opens the new version and optimizes that one.
- **Equicord's October 8 build.** A first install of Equicord is now the October 8 build, checked against its SHA-256. Equicord keeps itself up to date after that, and Quests stay hidden in it.
- **A safer, lighter plugin set.** Optimize Discord no longer turns on FakeNitro, which sent Nitro emoji and stickers as links and raised stream quality without Nitro, against Discord's terms. It also no longer makes Discord think you have Nitro, which hid upsells but offered Nitro-only choices that then failed; the Nitro and Shop tabs, gift buttons and the Nitro offer count stay hidden. Plugins that sent Discord extra requests for unknown mentions and missing replies, loaded full-size images in chat, or got around YouTube's blocks are off.
- **Discord acts like Discord where it matters.** Friends see what you play and when you type again (Discord's own Activity privacy settings decide that), Krisp noise suppression works in calls, friend and message request counts show, button tooltips and the device menus beside mute and deafen are back, and channel names, image grids and search are Discord's own. A call timer shows in the voice panel, and Equicord no longer opens a list of its new plugins after it updates. Plugins you changed in Equicord's own settings keep your choice.
- **The newest DLSS, FSR and XeSS, checked byte for byte.** Upscaler updates now bring DLSS 310.9.1 (Super Resolution, Ray Reconstruction and Frame Generation), FSR from AMD's FidelityFX SDK 2.3.0 (upscaler 4.1.1, frame generation 4.0.1), FSR 3.1.4, and XeSS SDK 3.0.2 (frame generation 1.3.1, XeLL 1.3.2). Every file comes straight from NVIDIA's, AMD's or Intel's own GitHub repository and is used only if its SHA-256 matches the value built into Exo and it carries the vendor's signature. Exo no longer reads a third-party catalog or asks GitHub what is newest each time.
- **A simple upscaler list on the game page.** Each upscaler the game uses, such as DLSS Super Resolution, DLSS Frame Generation, FSR or XeSS, gets one row: the version in green when it is the latest (310.9.1), or the old version still in the game in red when it is outdated, and Update and Restore sit above the list. A game that ships the same upscaler in two folders, or XeSS for both DirectX 11 and 12, still shows one row, red if any copy is outdated. No folders or notes. Update brings every file to the newest and never puts an older file over a newer one, and FSR and XeSS files that load each other move together. Restore puts the game's own files back and checks them against the saved copies byte for byte. When a game update replaces a file Exo changed, the new file becomes the original, and if Steam's file check puts the originals back, Update simply shows again. 32-bit and other non-x64 files are left alone.
- **Anti-cheat games change only if you allow it, one game at a time.** A game with Easy Anti-Cheat, BattlEye, Ricochet, EA Javelin, Anti-Cheat Expert or another anti-cheat Exo knows, found by its store, its title or its files anywhere in the install, says which one and changes nothing. Allow changes asks you to confirm that swapping DLLs in a game with anti-cheat can get your account banned, that the risk is entirely yours and so is playing online with changed files; Exo keeps the originals and Restore puts them back. Stop allowing ends it and offers to restore. Exo checks the game again before every change and refuses if the anti-cheat is different or the game is running, and Update all never reaches a game you have not allowed. Riot games and games with Riot Vanguard are never changed, with no way to allow it.
- **Upscaler status opens at once.** The sheet answers from what Exo found last time without going online or re-reading large files, and checks the folder again in the background.
- **DLSS presets.** On a PC with an NVIDIA graphics card, the sheet points to DLSS override in the NVIDIA app, which is where a game's DLSS preset is chosen. Exo only replaces files.
- **The mini player shows your music's real volume.** On a fresh start the volume in the top bar could sit at zero with the muted icon while your song played, until you touched it. It now shows the app's own level as soon as the app answers, keeps asking until it does, and only shows muted when the app is muted. A level you pick while Spotify or YouTube Music is still starting is applied once it is ready.
- **Optimized music apps are black all the way through.** Spotify's chips, search field, buttons and the cards in the now-playing panel no longer turn into invisible boxes, and its menus, pop-ups, dialogs and tooltips are no longer gray: raised surfaces are a near-black with a thin edge, hover is a faint step up. A promo Exo hides takes its empty slot with it. YouTube Music's menus, Settings dialog, chips, Explore buttons and the top search result follow the same look, the Premium-only Download item is gone from its menus, and the artist photo fades to black under the name again. In Apple Music, menus and dialogs stand off the page, the dark fades over artwork stay, and headers that float over a scrolling page stay solid.
- **Shaders, with SCSKiller's own app inside Exo.** A Shaders button beside Sort opens [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller), the free shader precompiler, inside Exo at a large size, with Exo's title and close button and without the app's own title bar or window buttons. Everything SCSKiller does is its own: compiling, recordings, Before and after, official and community recordings, its cache tools, settings, updates and its Patreon builds. Exo starts it hidden, and when Exo closes it goes back to its tray and keeps compiling. Without it, Shaders offers Get SCSKiller, which opens its download page. Exo runs no part of SCSKiller itself, and removes the copy an earlier Exo downloaded.
- **A game's page says whether it is compiled.** Its Shaders line reads SCSKiller's own records: Compiled when the app compiled the game for the graphics driver installed now (as soon as a compile ends), Driver changed or Game updated when it should compile again, Not supported when SCSKiller can't, and otherwise one button that opens Shaders.
- **One look for actions.** Undo in Settings and Stop allowing in Upscalers are buttons like the ones beside them, choice buttons use the same text size as other buttons, the Optimize descriptions are one line each, and the Settings section list follows the section you are reading.
- **The new Epic Games Launcher.** Epic's new launcher (2026, installed per user) keeps its installed games in a new list instead of the old manifests, so games it installed went missing from Exo. Exo now reads that list too, titles each game from the launcher's own catalog, finds the new launcher to start games and to install, update and uninstall through it, and notices when it installs or removes a game. The old launcher keeps working as before.
- **A Store in Exo.** A new Store tab beside Library leads with the biggest games the way Steam's and Epic's stores do: a banner of the biggest launches of the last two months and the most pre-ordered games coming (each says Out now or when it comes), then New and trending, Most anticipated, Top sellers, On sale and Most wishlisted, ranked by Steam's own sales and pre-orders, and Epic's free games with when each one ends. Upcoming games show their release date. Search finds any game as you type. Games you already own say In library, and their Store page has a button to your copy.
- **Store game pages.** A big page with the game's hero art and logo, its trailers playing right on the page in Steam's sharpest stream (1080p AV1 where the PC can play it), full-size screenshots, its description, reviews, release date, developer, publisher and genres, and its minimum and recommended PC requirements side by side. Get on Steam (or Wishlist on Steam before release) sits at the top, with the same game on Epic, GOG or Ubisoft when that official store sells it, and its price on each. No key resellers are listed.
- **Wishlist with sale alerts.** Wishlist a game in the Store and Exo checks its price now and then, and tells you once when it goes on sale.
- **GG.deals in the Store.** A GG.deals button on a game's Store page opens that game on GG.deals, for anyone who buys keys. Library pages stay as they were.
- **Download controls.** Epic, GOG and Amazon downloads that Exo runs can be paused from the Downloads panel. In the new Settings > Downloads, downloads pause while you play and pick up when you stop (on by default), can be kept to hours you choose such as overnight, and games update by themselves while your PC is idle with no game running. The install folder moved there too.
- **Queued downloads start.** Pressing Install on a second game while one downloads puts it in line, but when its turn came Exo checked a Steam list that leaves out games you own and haven't installed, so every queued Steam game was cancelled with "no longer available to the active store account". Queued games now start in order, still checked against Steam's current list of what you own.
- **Big downloads no longer "time out".** Exo stopped waiting on a Steam install after two hours (an update after 90 minutes) even while Steam was still downloading, and the game page said "Exo did not answer in time" after ten. Exo now waits as long as Steam keeps downloading, and only gives up if Steam never starts or makes no progress for two hours.
- **Paused Steam downloads resume.** After a restart, Resume refused every paused Steam game while any other game had a partial download in Steam's folder, which a cancelled download leaves behind. Steam keeps its own download queue, so Resume now always goes through.
- **Downloads pick up again after a restart.** Downloads that were running or waiting in line when Exo closed now start again by themselves, in the same order, once the library loads. Each still passes the same checks as pressing Resume (same store account, still owned, not already installed); one that fails stays paused with its reason, and the rest carry on instead of all pausing with it.
- **The Downloads panel reads cleanly.** A paused game's row says Paused instead of a long sentence that piled up over the game titles, and Cancel in the panel asks Sure? first, since rows move when a download starts.
- **Download progress keeps moving.** A download that started from the queue could stay on Waiting… in Exo while Steam was well into it, because one refresh left Exo's progress display thinking an update was already on its way. It now follows every update.
- **Exo says when Steam can't reach its servers.** When Steam's download log shows only connection failures for the last two minutes, the download's line says Steam can't reach its download servers instead of looking stuck.
- **Tidier download rows and game tools.** A download's status and size share one line in the Downloads panel, and a game's tools (Open folder, Verify files, Remove, Replace cover, Refresh art) fit on one row.
- **A game in line says Queued.** Its button on the game page reads Queued instead of Install, and pressing it twice takes it out of the line.
- **One stray click no longer cancels a download.** The button that shows a download's progress also cancels it. The first press now reads Press again to cancel, and goes back after a few seconds.
- **Install starts again after an old paused request.** A download left paused from an earlier session blocked Install on that game with no way to clear it from the game page. Pressing Install now replaces it.
- **Steam says why a download stopped.** When Steam can't reach its download servers or runs out of disk space, the progress line says so instead of sitting at 0%.

## 3.3.1 — September 26, 2026

This update puts one Optimize button on every app Exo hosts, keeps Quests out of Discord, sharpens and steadies library covers, lets you drag the whole top bar, links the mini player to your music app's own volume, and adds a Devices room for your mouse.
- **Library covers stay sharp and stay put.** Grid covers are drawn from full-quality art at twice the card size (most Steam covers now show at full size), fast scrolling no longer tears or mixes covers, and covers no longer vanish after Exo loses focus or sits minimized: a cover that failed to load retries on its own when it comes back into view.
- **Covers stay put when you come back from another room.** A cover that finished loading while Discord, Music, Devices or Settings was open was thrown away, so the card came back blank until it loaded again. It now shows the moment you return, and a cover that could not load tries again when you come back to the Library.
- **Settings opens at once.** Settings now shows what it knew last time right away and checks for changes behind it: your Exo account no longer waits on two trips to the account service, and the Music and Discord, Optimize and Devices rows are ready on the first open instead of saying Checking. Opening the Devices room no longer holds up Exo for up to three seconds while the OpenMouse panel loads, and a game's upscaler check answers from what Exo found last time instead of searching the whole game folder again after every start.
- **Covers hold still at the end of the library.** With reduced motion on in Exo or in Windows, small scrolls up and down near the bottom of the library made the covers jump a row, swap places for a moment or blink out. Reduced motion now turns animations off instead of making every change on the page a very short one, which is what moved the rows out of step.
- **One Steam request per click.** While Steam held an update in its queue, Exo asked Steam again every 12 seconds, starting Steam with the game's install link each time, for up to two minutes per click. Update and Install now ask once, and once more 20 seconds later only if Steam has not picked the request up.
- **Optimize each app with one button.** Settings > Music and Discord has an Optimize row for every app you have installed: Discord, Spotify, Apple Music, Cider and YouTube Music. Each row says what it changes. Run applies it now and shows what it did, such as "Optimized · Equicord 2026-09-26, 42 plugins", and Undo puts the app back the way it ships. If the app has to restart to pick up the change, Exo says so while it restarts it, and it never restarts an app during a call or while music is playing; it asks you to try again later instead. Exo applies the optimization again each time the app updates itself. This replaces the Clean up Discord switch. If that switch was on, Discord stays optimized.
- **Quests really are gone from an optimized Discord.** Equicord's Quest plugin reset its own settings the first time it started, which brought Quests and the Quest button back. Exo now sets up the plugin so its settings stay put. It also hides Quest badges and promos, sponsored Quest banners, Super Reaction animations and Super React as the default, and the Discover and app download buttons in the server list. Plugins you change in Equicord's own settings keep your choice when Exo applies the optimization again. The first Equicord download is now the September 26 build, checked against its SHA-256.
- **Spotify, optimized.** On top of the black theme, Exo hides home-feed ads, merch and concert promos, the What's New button and install-app prompts. Canvas videos stop decoding, so each shows a still frame, and Spotify's own preferences for hiding home-page takeovers and promoted songs are turned on. If an update changes Spotify's files in a way Exo does not recognize, Exo leaves Spotify as it ships.
- **Optimized apps draw less, all the time.** Optimize Discord now also adds an Exo performance theme to Equicord, listed after your own themes: animations finish at once, transitions are instant and glass blur is off, so hover, press and selection still show without the fade. Loading spinners keep turning, the speaking ring in voice stays, and spoilers stay blurred. Optimize Spotify, Cider and YouTube Music do the same inside each app. Turn the theme off in Equicord's Themes tab and it stays off; Undo removes it and nothing else. Discord's own GIF, emoji and sticker animation settings are synced to your Discord account, so Exo leaves them to you.
- **Discord steps back while you play.** Exo already lowers the priority of Discord and your music app while Exo is behind something and they are silent. A Discord room left open behind a game launched from Exo now yields too. A call or a song keeps normal priority, so voice and music never crackle.
- **Spotify's window buttons stay gone.** Exo hides Spotify's own minimize, maximize and close buttons inside the room, but a Spotify that started quickly, reloaded its page or took long to start could show them again. Exo now keeps hiding them for as long as Spotify runs, and the What's New, Friend Activity and profile buttons sit at the right edge where the window buttons were.
- **Apple Music, Cider and YouTube Music.** Optimize Apple Music is the black background it already had, and you can now turn it off. It now also reaches the parts that stayed gray: the window's glass backdrop behind the app, the sidebar, the lighter content panel and cards, menus, pop-ups and dialogs, and pages opened later. Text and buttons keep Apple's colors, and panel edges become a thin dark line. Optimize Cider turns off Cider's blurred glass panels and smooth scrolling while Exo hosts it. Optimize YouTube Music hides the Premium upsells and stops drawing the video while the room is hidden, and the song keeps playing.
- **The whole top bar moves the window.** While a song showed, the mini player and all the space around it refused to drag, and the bar could stay undraggable after an update check until you resized the window. Now every part of the bar that is not a button, tab or slider drags, including the pill's empty middle and the song's time. Double-click it to maximize or restore, drag it to a screen edge to snap, and hover the maximize button for Windows' snap layouts.
- **One volume for your music.** The mini player's volume is now Spotify's and YouTube Music's own volume: move either slider and the other follows within a second. Exo used to turn the app down in the Windows volume mixer instead, which stacked with the app's own slider; that mixer level goes back to 100% once, with the app's volume lowered to match so nothing gets louder. Apple Music and Cider have no volume Exo can reach, so they keep the mixer level, and the mini player now follows changes made in the Windows mixer.
- **The mini player keeps up with your music.** It now reads the song straight from YouTube Music and Spotify instead of from Windows, so it shows whenever a song is loaded, playing or paused, stays put while the app sleeps in the background, and never picks up a browser or another app playing sound. Its time counts second by second in step with the app, and play, pause, skip and seek always reach the app it shows. Apple Music and Cider only ever show their own song.
- **Devices, a new room after Music.** The room is the OpenMouse panel itself, in Exo's black, for DPI, polling rate, buttons and lighting, with OpenMouse's announcement strip hidden. Exo is the panel's bridge: it answers the panel on OpenMouse Bridge's own port while Exo is open, so there is nothing to download and no extra app in your tray. Every mouse the OpenMouse panel supports works, because the panel's own drivers talk to the mouse; Exo only carries what they send. Exo accepts only OpenMouse's own site, so no other web page can reach your mouse through it.
- **Mouse profiles for your whole library.** Every installed game in your library is on OpenMouse's game list next to OpenMouse's own games, so any of them can have a profile, and it switches when the game comes to the front. Exo never lists launchers or anti-cheat, and leaves Riot games to OpenMouse, which already lists VALORANT and League. When you have profiles, Exo keeps the OpenMouse panel loaded in the background, so they switch within seconds even while Exo is minimized. Profiles switch only while Exo is open. A game's sheet now shows the mouse profile it uses.
- **Use Exo instead of the OpenMouse Bridge app.** If the OpenMouse Bridge app is on your PC, Exo leaves it in charge until you choose Use Exo's bridge in Settings > Devices. Exo then closes the app, takes it out of Windows startup and brings over your profiles; the app's own files are not changed. Undo takes you back: Exo starts the app and puts it back in Windows startup. Profiles you change while Exo's bridge is in use stay in Exo.
- **Mouse battery.** Settings > Devices shows each mouse's battery as the OpenMouse panel reads it, and Exo tells you when a mouse drops to 20% or less, once every six hours per mouse, while Exo is open.
- **Covers for your games in OpenMouse.** Your installed games show their covers on the OpenMouse Games page inside Exo, the same art as your library.
- **MambaSnake M5 Ultra works in the OpenMouse panel inside Exo.** OpenMouse listed it without controls; in Exo it gets the DPI, polling rate, button and lighting settings of the Attack Shark R5 Ultra it is built on.

## 3.3.0 — September 25, 2026

This update rebuilds setup and Settings, shows your whole Steam library without a key, and leaves Discord exactly as Discord ships it unless you ask Exo to clean it up. Epic and GOG sign-in completes, keys you paste save, and Exo does less while it sits behind your game.

- **Setup in three short steps.** Your game stores, Music and Discord, and an optional Exo account, in one column with no keys to paste. The account step is skipped when the service can't sign anyone in, and its form sits on the page instead of in a box inside a box.
- **Settings, rebuilt.** A section list on the left edge and one roomy column: General, Music and Discord, Game stores, Optional keys, Exo account and About. Settings that did nothing are gone, every row says what it does, and keys you paste (Steam, itch.io, SteamGridDB) now actually save. An Exo account the service can't reach offers Retry instead of a red error. The memory reading is gone from About.
- **Your whole Steam library shows up without a Web API key.** Exo used to know which Steam games you own only if you pasted a Steam Web API key, so for most people installed games read "Ownership unverified" and games you own but have not installed never appeared. Exo now asks the Steam client you are signed in to which games are yours, so installed games are owned and uninstalled ones appear ready to install. What Steam confirms is remembered, so they stay while Steam is closed. With a key, libraries over 400 games are no longer cut short.
- **Steam starts itself when it needs to.** If Steam is installed with a remembered sign-in but is not running, Exo starts it quietly once, waits for it to answer, and fills in what you own. The library never waits on it. Without a remembered sign-in Exo leaves Steam closed rather than opening its login window on every start.
- **Owned shelves show games only.** Bonus content, soundtracks, SDKs, dev kits, test servers and tools stay off the Owned, not installed shelf, including ones remembered from before.
- **Epic and GOG sign-in completes.** The sign-in window no longer runs an ad blocker or blocks Epic's and GOG's sign-in providers, so Google, Xbox, PlayStation, Steam and the others work.
- **Song saving and trophy pop-ups are gone.** The mini player no longer has Save and Remove buttons, and Exo no longer saves songs to YouTube Music, Spotify or Apple Music (through Cider). The Spotify connection and the Spotify client ID and Cider API token settings are removed, and the keys saved for them are deleted on the next start. Exo also stops showing a pop-up when you unlock an achievement, and the notification settings that went with it are gone. Playback, seek, volume and opening the Music room are unchanged. Exo still reads achievement counts, now once after a game closes rather than every few seconds while you play. The launch and install-complete sounds are gone too.
- **Discord is left as Discord ships it.** Exo used to put Equicord into Discord's files for everyone, and Discord's own updater could not build a new version from the changed files, which left Discord unable to start. Discord is now stock unless you turn on Clean up Discord in Settings, and what an earlier Exo changed is undone the next time Exo starts Discord. Off puts Discord's own files back. On, Exo checks before every Discord start and after any start that opens nothing: if a Discord update never finished, Exo puts Discord's own files back, moves the unfinished version aside, and starts Discord through its own updater so the update runs again. Equicord goes back in on the next start. An Equicord you installed yourself is left alone.
- **The Friends room is called Discord**, in the top bar and the command palette, because that is what it holds.
- **Messages say what actually happened.** Signing in to Steam or Riot opens that app and says to finish there, instead of reporting a connection that has not happened yet. A failed Epic or GOG install or update names the game and the reason instead of printing the tool's raw output. Cider's button says Get, because Exo can only open its download page, and the message after it says to finish in your browser. Errors are plain sentences throughout.
- **Library and window fixes.** Activating a key hands it to the store's own redeem window and confirms the game when the store does, even after the popover has closed. Success messages are no longer red. The library toolbar no longer covers the first shelf. The mini player fits inside the top bar's pill. Minimize minimizes the window instead of hiding it.
- **Discord stays quiet behind music.** Discord starts hidden with Exo, and a Discord that had never been on screen kept drawing while hidden: with Spotify playing, its graphics process woke about 45 times a second to animate the song in Discord's own panel. Exo now shows it once for a second through a single pixel over Exo's black background, which is enough for it to notice it is hidden; its hidden cost drops by about half. Discord and a playing Spotify behind a closed room also run their drawing processes in Windows efficiency mode, never their audio, network or DRM, and opening the room restores full speed at once. While Exo is minimized or hidden, its own memory in use drops from about 260 MB to about 40 MB.
- **Scrolling the library costs less.** Library cards used to decode each saved poster at full size (usually 600 by 900) to draw it about 155 pixels wide. Cards now draw a smaller copy that Exo makes from that same file, sized for the largest card your screen can show at its scaling. The copy is made again whenever the cover changes, including a cover you picked yourself. The game sheet still shows the full image. Scrolling a 199-game library top to bottom and back now takes about half the page's CPU time, about 30% less CPU across Exo, and a third less page memory.
- **Less work while nothing changes.** A library refresh that ends with the same games is no longer sent to the page again. Download progress redraws four times a second instead of on every line a store tool prints. Game sheets reuse the upscaler check while those files are unchanged instead of re-hashing hundreds of megabytes each time, and cover files are not re-read to answer the same question twice.
- **Works better with a keyboard and a screen reader.** Focus comes back to a button that turned itself off while it worked, and stays inside the key popover, the game sheet and the command palette. Download buttons stop renaming themselves every second, so a screen reader is not interrupted by each percent. Library cards announce that they open a sheet, switches have a larger hit area, the command palette's gray text is readable, and a screen reader can confirm deleting your account with a click, since it cannot hold.
- **Exo no longer shares your status or game activity.** While you were signed in, Exo kept a connection open to the Exo account service and sent it whether you were online and which game you were playing. About a minute after your library changed, it also uploaded your total hours, game counts, up to ten of your most-played games with their hours and achievement counts, and a random id for this PC. Exo sends none of that now. The account only signs you in and out, exports your data and deletes the account, and the id file is deleted on the next start. Whatever an earlier version sent is in your account export and is removed if you delete the account. The privacy statement is rewritten to match what Exo actually does.
- **Smaller app.** Code for retired features, unused styles and host messages nothing called are removed, along with a Discord start-up setting Exo wrote that Discord never reads. An Exo account no longer syncs preferences, which needed a handle Exo no longer offers; your settings stay on this PC. Exo's own page now loads scripts only from Exo and allows nothing it does not show.

Requires Windows 11 x64.

## 3.2.7 — September 23, 2026

This update makes Exo start about three times faster, use less memory, and keep Discord and your music app working through their own updates. It also removes two rooms and fixes a bug where Exo could move or close its own window.
- **Discover and Devices are gone.** Exo is Library, Friends and Music. The store catalog, store search, product pages, the wishlist, the GG.deals key and the mouse profile studio are removed along with their background work and cached files; old profile and catalog files are cleaned up on the next start. Buy links on a library game still open the vendor's store page.
- **Hold to delete.** Deleting your account is a hold, not a click: the button fills over a second while you keep it pressed and fires only when the fill completes; letting go early cancels.
- **Segments slide.** The Settings segmented controls move one thumb between choices instead of repainting each option.
- **Settings fits the window again.** Every panel is visible at once with nothing scrolling, in its own box or as a page. The store list you can add from sits in two columns instead of one tall stack, the rows are a little tighter, and the memory reading is gone from About.
- **The mini player finds a player that starts quiet.** Exo used to look for the player's Windows media session for one minute after launching it, then stop. Opening Music without playing anything meant the mini player, its transport and its volume stayed empty for the rest of the session. It now keeps looking for as long as the player is open, and the log records one line per change instead of one every two seconds.
- **A player that is stuck asking you something says so.** When a music or chat client never opens its window, Exo used to leave the room reading only that the client did not open, while the prompt the client was waiting on stayed hidden because Exo hides boot windows on sight. Any such prompt is now shown again and named in the room, so a client blocked on a runtime update can actually be answered.
- **Exo no longer moves or closes itself with a hosted app.** Windows reuses process numbers, and when the program that started Exo had exited, its number could be handed to a Cider or Discord helper. Exo then counted itself as part of that app: it moved its own window into the Music room until it slid off screen, and closing the app could close Exo mid-shutdown. A process now only counts as an app's child if it started after its parent, and Exo never counts itself.
- **Starting Exo no longer steals focus.** Discord's hidden window used to keep the keyboard focus after Exo hid it, so the game or app you were in stopped taking keys. Focus now goes back to where it was.
- **The mini player finds Spotify again.** Spotify only reports what it is playing to Windows while its system media controls setting is on; Exo turns that on before it starts Spotify.
- **Embedded apps survive their own updates.** Discord moving to a new version folder, a client restarting itself to update, and Cider or Apple Music updating through the Store are followed: the new window is adopted instead of the room going blank, nothing is closed mid-update, and a prompt the app is waiting on is shown in the room instead of being hidden. Equicord is patched in atomically and downloads in the background, so a slow download no longer holds Discord's start.
- **Apple Music's black theme applies on this kind of PC.** With UAC off every app runs as administrator; Exo now only refuses when Exo and Apple Music differ, and it loads Apple Music's XAML tooling from a private copy because the Store package does not let other programs load it in place.
- **Less memory.** A playing Spotify hidden behind another room drops its interface's memory (about 300 MB) while the music keeps going from its own audio process. Two leftover copies of the ad blocker that older builds put on Exo's own page are removed (about 170 MB). The main stylesheet is 169 KB instead of 538 KB and the animation library is gone.
- **Less background work.** Exo watches which window is in front through a Windows event instead of checking every 3 seconds, and it no longer runs a system-wide keyboard hook unless a hosted app has focus. The daily cover "quality upgrade" no longer downloads roughly 100 images it could never use, a one-store refresh no longer re-warms every cover, Steam manifests are parsed once instead of with a new pattern per field, and settings, achievements and playtime only write when something changed. Trophy banners appear again on unlock.
- **Library opens three times faster.** Exo used to re-check every saved cover before answering the first library request (1.3 seconds on a 108-game library). The saved library now answers at once and covers refresh right after, and which posters are letterboxed banners is remembered between starts instead of decoded again. Library is usable about half a second after the window appears, down from about 1.6. Upscaler files are warmed only for pinned games at startup.
- **Leaner interface.** The component library is replaced by small native buttons, switches and menus: 213 KB less JavaScript and another 65 KB less CSS. The Sort and Add game menus keep their keyboard support.
- **No more polling for game state.** Running and stopped games, and the upscaler catalog, arrive as events from Exo instead of the interface asking every 2 seconds. Time in games Exo did not start is counted up to the last moment the game was seen.
- **Riot says what went wrong.** A game that needs an update says so instead of showing the client's raw error, and a launch the Riot Client is slow to answer is no longer reported as refused when the game is already starting.
- **Smoother UI.** Scrolling runs on the compositor, cards no longer rebuild on every library refresh or remount when they change rows, the command palette takes mouse clicks and always lists its actions, and messages like "Install complete" stay on screen.
- **A hosted app that comes up empty gets one clean start.** If Discord, Spotify, Apple Music or Cider is running but never produces its own window, Exo used to keep waking that same wedged process and give up, leaving the room empty until you noticed and restarted the app yourself. It now closes that tree once and starts the app fresh, which is what recovers a client whose runtime was replaced underneath it. An app you already had open yourself is never restarted; it stays yours.

Requires Windows 11 x64.

## 3.2.6 — September 16, 2026

This update keeps the mini player from moving the window when you use it.

- **The player is not a drag handle.** While a track is showing, the mini player and the space around it stay clickable — skip, seek, volume, and the track. Other blank parts of the top bar still move the window. With no song, that empty centre drags again.

Requires Windows 11 x64.

## 3.2.5 — September 15, 2026

This update installs hosted apps during setup, checks for Exo updates on the boot mark, and stops the mini player from jumping to full volume.

- **Install Discord, Spotify, Apple Music and Cider during setup.** Permissions lists each app. You can install more than one even if Exo plays a different service. Extra players stay on disk until you switch to them.
- **Updates before the library.** If a newer Exo is on GitHub, the logo screen offers it. Update downloads and installs there; the silent installer closes this build and opens the new one. Not now still opens the app. Settings can still check later.
- **Mini player volume stays where you left it.** The control no longer reports 100% when it has not read the playing session, and it no longer writes that 100% onto a quieter song when you click it.

Requires Windows 11 x64.

## 3.2.4 — September 15, 2026

This update makes game actions more reliable, reduces repeated background work, and makes more of your existing settings easy to reach.

- **Settings on one page.** Every setting is visible at once in three columns — no rail, no dropdowns — with your account leading the page. General gains a Default install folder picker. Developer-only IGDB paste fields are hidden from the shipped build. Signing in runs along the top band instead of down it; Export and Delete account sit in General as one Account data row; Connected apps shows Discord's state with Open or Install; below 1400px wide the panels fall into two columns and the page scrolls. Green means green: the shared "good" color is the design-system green rather than gray.
- **Library.** The sort and Add game menus open again (the Pinned header was sitting on top of them). Pinned cards and grid cards are now the same width, and the grid settles at eleven ~160px columns on a 1920px window instead of twelve narrow ones. Cards answer the pointer with a hairline and a touch more light, keyboard focus shows a ring, and owned-but-not-installed art is dimmed rather than fully grayed, coming back to color under the pointer. Shelf headings carry their counts, the sort control says what it is, and the game details sheet hugs its content instead of sitting in a fixed box. Card titles are one quiet line that unfolds under the pointer; a running game's frame glows; the title whose sheet is open keeps a mark. Owned, not installed gets store chips to narrow it and always stays alphabetical. Dimming and hover light are compositor washes, not live filters, the grid keeps two rows warm past the viewport, and the first artwork warm covers pinned titles plus two grid rows. The game sheet names the store beside the title, shows its tools as one row instead of behind Manage, puts status and errors under the action pill, writes stat labels in sentence case, and says "None found" for upscalers instead of a paragraph. DESIGN.md is rewritten around five principles (immersive, clean, modern, AMOLED black, consistent) with the real token values.
- **Title bar.** The mini player is a black hairline pill like every other control, its transport buttons light with a wash instead of a gray fill, shuffle marks itself with the cream fill instead of pink, and the device-profile dot is the shared green.
- **Save a song to every library you've connected.** The mini player's Save and Remove now work for Spotify and, through Cider, Apple Music, alongside YouTube Music. Connect Spotify once in Keys & IDs with the client ID of your own Spotify app (the sign-in opens in your browser and comes back on a loopback address); paste Cider's API token from Cider → Settings → Connectivity. A save goes to the playing service first, then to Spotify as well when it is connected. Cider can add but not remove; Apple Music's own app gives Exo no way in, so play through Cider to save there.
- **Fewer bars, fewer stalls.** No scrollbar chrome anywhere in Exo; wheel, touch and keys still scroll every pane. Exo no longer empties the working set of its own shell, Discord or the music client while it is in the background, which is what made those rooms feel slow to come back to. Low memory priority while backgrounded stays.
- **Idle clients freeze.** Thirty seconds after you leave Friends or Music with nothing playing and no live audio (Exo watches the client's own speaker and microphone sessions, so a voice call keeps Discord awake), the whole client is suspended and its memory released; suspended, nothing can pull the pages back. Opening the room, pressing play, skip, or touching the volume wakes it first, so changing songs from the mini player does not wait on a cold start. A frozen Discord cannot ring or update its badge until then. Boot windows and splash screens never reach the screen. The music app now starts the first time Music is opened instead of at startup; Discord still pre-launches for Friends.
- **Dead code gone.** Seventy-nine unreferenced methods, constants and types removed from the host, unused private members, values and parameters cleaned up under the compiler's own analyzers, an unused font package dropped from the web app. An .editorconfig now flags unused code as a warning in the IDE.
- **Motion.** The game sheet rises in over 200ms and its scrim fades instead of appearing; sort, add and profile menus settle from their trigger corner instead of popping; a room coming back on screen fades in over 120ms; segment pills ease between states. Four duplicate easing tokens folded into the two strong curves the app actually uses. Reduced motion keeps the fades and drops the movement.
- **Profiles wait for a sleeping mouse.** Applying a profile while the mouse is asleep no longer leaves a red "not connected" line: Exo says the profile applies when it wakes, then lands it the moment the wake probe hears the mouse. Apple Music's own caption buttons are cut out of its hosted window, so they neither draw nor take clicks inside the room.
- **Less background scanning.** Steam's config and cache writes no longer trigger a full library rescan every minute; only install manifests do.
- **Mini player.** The volume slider always works: it follows the app's audio session by process as well as by window, and a level set before a song starts lands the moment sound begins. Remove is offered for songs already in your current music library, the volume percentage and the shuffle button are gone, and the progress bar and time now track the song correctly: positions are measured from the track start, a client that reports no timestamp no longer freezes the bar, and resuming from pause no longer jumps ahead by the paused time.
- **Upscaler safety.** Replacing files in an anti-cheat game now needs an explicit acknowledgement, and Riot games are never modified.
- **One label voice.** No more tracked capitals above content anywhere in Exo chrome; the command palette keeps the app visible behind it.
- **Leaner.** 91 KB of stylesheet for retired screens is gone (`npm run css:prune` reports any that returns). A title whose artwork cannot be fetched now waits six hours before the background warm tries again instead of retrying every cycle. The cache-repair row that only resets the Discover catalog is named that.
- **Friends is the real Discord app.** The room now runs your installed Discord desktop client inside Exo's window — not a website in a browser frame. Everything Discord does on its own works unchanged: voice, video, screen sharing, stream quality, and updates. If Discord isn't installed yet, Friends offers to install it with one confirmation.
- **Apps start and close with Exo.** They start silently: every window a client shows while it boots is hidden the instant it appears, so nothing lands over your screen until you open its room. Discord and your chosen music app now start in the background as soon as the library is usable, so Friends and Music open instantly, and every session Exo hosts — including one you had open before — closes when Exo closes. The "Load Friends and Music at startup" switch is gone because it is no longer a choice.
- **Switching music services is clean.** The previous app is closed off the UI thread and the next one only launches once the old process is really gone, so Spotify, Apple Music and Cider no longer relaunch into a dying instance or report "did not open a window" after a switch. Cider's player is recognised while a track title is in its window caption.
- **Discord is black.** Exo's Equicord stylesheet now targets the tokens Discord's current dark theme actually paints with, so the server list, channel list, chat, members and dialogs sit on void black like the rest of Exo.
- **Listen without keeping the whole player hot.** Cider, Spotify, Apple Music and YouTube keep their decoder, audio and DRM while a song plays from the mini player; the hidden GPU, crashpad and leftover utilities are released. Discord in a call keeps its compositor (screenshare still needs it). Skip, pause and opening Music stay instant on every provider. A YouTube page that is still playing is never emptied.
- **Closed means closed.** Quitting Discord or your music app from inside the app no longer gets it relaunched by a window resize; the room waits for Open again. A client that only hid to its tray is closed with the session instead of lingering.
- **YouTube Music only.** The mini player no longer adopts another app's WebView2 media session while YouTube Music is idle, and a hidden YouTube page that is still playing is never suspended.
- **Safer installs.** Discord and Spotify installers are verified with Windows trust policy over the whole file, and Equicord's first download is a pinned release asset checked for size and hash.
- **Lighter foreground checks.** Hosted process groups are cached for two seconds instead of walking the process table on every focus change and playback tick.
- **Apple Music theme diagnostics.** When the black theme cannot attach, app.log names the check that refused it instead of a bare error code.
- **Hosted desktop clients lose their separate window chrome, stay inside their rooms, route Alt+F4 to Exo, and show no notification-area icons of their own.** Equicord in Discord is always on: the client is loaded through Equicord's standard loader with a curated set of declutter, performance and privacy plugins; MessageLogger stays off; Equicord keeps itself updated. Settings now reports the combined working set and private memory of Exo, its WebView runtime, and hosted clients.
- **One honest memory total.** Exo's web surfaces share one WebView2 runtime. Official desktop clients retain their own processes; Settings includes those hosted processes in the combined working-set and private-byte totals without counting a process twice. Working-set trimming does not reduce private allocations by the same amount.
- **Music is the real app.** Spotify and Apple Music now run their official desktop apps inside the Music room, hosted exactly like Discord in Friends — signed in once, they keep everything the app can do, including Spotify Jam. YouTube Music stays on the web player. Cider is a fourth choice under Music service — Cider 4 (Store MSIX) and earlier installs are both detected. If the app isn't installed, Music offers to install it with one confirmation. Switching music providers closes the previous player instead of leaving its app running.
- **Black music surfaces.** YouTube Music, Spotify and Apple Music flatten to true black by default: YouTube through its embedded page, Spotify through a small reversible stylesheet patch applied to whichever archive entries carry its theme variables (hash-checked backup restores the stock archive byte-for-byte), and Apple Music through a theme attach on Exo-started sessions. Artwork and provider colors are never filtered.
- **A mini player that speaks Windows.** The mini player uses the selected provider's Windows media session and exposes the controls that session supports. Volume uses per-app audio sessions. Apple Music's mini player was verified live; Spotify did not publish a Windows media session during testing, so its mini-player integration remains unresolved. Its own player controls remain available.
- **Provider extensions.** Music loads pinned, hash-verified browser extensions — SponsorBlock and Better Lyrics for YouTube Music — into the shared embedded profile automatically. Nothing updates itself.
- **Stays out of your game's way.** Every 5 seconds Exo empties the working set of hidden Discord and music pages, and while Exo is not the window in front it does the same for all of its processes, marks their memory low priority so Windows reclaims Exo before your game, and puts its own process in efficiency mode. Music keeps playing throughout. Everything is restored the moment Exo is in front again.
- **Top bar fix.** With the mini player showing, room labels were clipped, the profile pill lost its label and the player showed no title or artwork. One stray rule sized every titlebar button like the logo; fixed.
- **Hosted rooms only host the client.** A browser or helper opened through a link inside Discord, Spotify, Apple Music or Cider is never mistaken for the client and pulled into the room.
- **Small things.** The game sheet dims the room behind it. Every clickable control shows a pointer cursor. Polling rate in Devices is a segment like the other settings. Wheel scrolling is instant instead of smoothed. Hover motion in Discover is quicker.
- **Only what ships.** Code for retired features is deleted rather than kept behind flags: the Exo roster and Profile screens and their social APIs, the Discord Social SDK, the native MusicKit and Spotify SDK players, keyboard, headset and gamepad device support, and the old native room models. The React-to-host surface shrinks from 144 methods to 89. Friends, Music, Devices, achievements and account sign-in behave exactly as before.
- **Keyboard gets a real focus ring.** Tabbing through Exo now shows a 2px cream ring on the focused control; pointer clicks never show it. Text can be selected, and scrolled panes show a thin scrollbar instead of hiding it.
- **Small windows fit.** Music service choices in Settings wrap to two rows instead of clipping, and the Devices toolbar keeps Rescan on screen.
- **Cider installs from its own site.** The Get Cider action opens Cider's official downloads page, where the current release lives; the Store and winget listings can carry an older build.
- **Music save results stay until you dismiss them.** Failures, partial saves and skipped items no longer vanish after three seconds; only a clean success fades on its own.
- **Clearer Apple Music theme status.** When the black theme cannot attach, the room says what to do — reopen Exo without administrator rights — instead of showing an error code.
- **Quieter when hidden.** While Exo is behind another window, page animations, the mini-player clock, device battery reads and the running-game poll pause; they resume the moment Exo is in front. Audio keeps playing.
- **Hosted apps keep their side windows in check.** A dialog or shell window a hosted app opens on its own — like Cider's update prompt — now appears inside the room where it can be answered, instead of surfacing on another monitor.
- **Generated Steam capsules no longer pass for cover art.** For games that never shipped a portrait cover, Steam pads the landscape banner into a poster-shaped file over a flat gradient, and cards were rendering that sliver. Covers that are mostly filler are now recognised and dropped so the next source — SteamGridDB, IGDB or the plated icon — gets a turn.
- **The library holds still.** Opening a game sheet no longer shifts the grid: the room's hidden scrollbar was leaving a gutter that vanished when the sheet locked scrolling, reflowing every card a pixel. The pane now truly owns no scrollbar, so opening and closing details changes nothing behind them.
- **Hosted music apps no longer carry punched-out caption holes.** The corner cut-outs that erased the close cluster from embedded Spotify, Cider and Apple Music windows are gone — the duplicate controls are removed through each client's own interface instead.
- **Devices reads top to bottom.** The selected profile is one quiet line — "On the mouse" or "Saved settings" with Use beside it — instead of a second card repeating the picker's name. The "1 device detected" filler is gone; save state only speaks while it is doing something. Sleep and status notes are plain text, not alert boxes.
- **Settings rows tell you what a key is for.** The Spotify client ID and Cider API token say they save songs to your library — not needed to play. The Spotify library connect row aligns its controls with the shared right edge like every other row, and the Discord status line only appears when Discord needs opening or installing.

- **Clearer game actions.** Install, update, verify, and uninstall actions report what the store or helper actually did. Uninstall checks the exact installation before continuing, and opening a store client is no longer presented as a completed operation.
- **Smoother background updates.** Library scans, artwork, supported mouse profiles, and music share or limit repeated work so they interfere less with the rest of the app.
- **More settings within reach.** Adjust text size and notification sounds, manage supported account privacy and sessions when signed in, and find cache repair and library filters in Settings.
- **Better game and download controls.** Game-source selection and paused transfers behave more consistently. Catalog errors and ownership checks give clearer results instead of showing a misleading success or empty state.
- **Music saves when you choose.** Saving music to your library is an explicit action, with feedback when saving is unavailable or fails.
- **More dependable device profiles.** Fixed device locking and Desktop-profile setup for supported mice, with clearer feedback when a setting cannot be applied.
- **Clearer upscaler setup.** When anti-cheat is detected, setup shows an acknowledgment that is required before replacing upscaler files.
- **itch.io browsing fixes.** Discover handles itch.io results and links more reliably.
- **No double window chrome in hosted apps.** Spotify and Cider no longer draw their own minimize, maximize and close buttons inside their rooms — Exo's controls are the only ones. Cider's strip is reclaimed through its own custom CSS field, so the space collapses instead of sitting empty, and the slice is removed again when the hosted session ends. Spotify's cluster is hidden through the client's own window bridge on sessions Exo launches — nothing is written to Spotify's files, so opening it on its own always keeps its controls. Discord's title bar already sat outside the room's frame.
- **Mini player.** The volume slider always works: it follows the app's audio session by process as well as by window, and a level set before a song starts lands the moment sound begins. The shuffle button is gone — shuffle belongs to the player that is actually playing. The rest of the transport is unchanged.

**Updating keeps your settings and sign-ins.**

Requires Windows 11 x64.

[Getting started and help](https://github.com/ImAvgErix/ExoLauncher/blob/main/docs/HELP.md) · [Support on Ko-fi](https://ko-fi.com/uhherix)

### Source maintenance

- Patched the backend development image dependency and removed unused UI code. The Geist Mono font is used through CSS and is explicitly retained in dependency checks.
- Repaired presence pacing and sensitive-URL log redaction in backend source. Hosted backend deployment is separate from this desktop release.

## 3.2.2 — September 7, 2026

itch.io joins Discover, Discord notifications get a quieter look, and everyday window, music, and mouse controls work more reliably.

- **itch.io in Discover.** Browse an itch.io shelf without an API key, find its loaded games in search, and open the full itch.io search when you want more. Game cards take you to the creator's page. The itch.io API key field is back in Settings for owned-library access.
- **Quieter Discord notifications.** The Friends button can show a neutral notification count. Turn off the Discord notification counter in Settings to hide it. Removed the light server marker that bled along Discord's left edge.
- **Better taskbar behavior.** Windows minimize and restore actions no longer send Exo unexpectedly into the tray.
- **More mouse controls.** Added Attack Shark X8 Pro support through Attack Shark's official local helper, with checks that settings were applied. The helper must be installed on the PC using the mouse; physical X8 Pro testing is still pending. Improved automatic game and desktop profile switching for supported mice.
- **Music and connected apps.** More reliable music controls, volume, and reconnecting, plus configurable uBlock Origin ad blocking for connected apps.
- **Store and game-page fixes.** Improved launch handoffs, client window behavior, transfer recovery, and the presentation of game details.
- **Support Exo on Ko-fi.** Find the new support link in Settings.

**Updating keeps your settings and sign-ins.**

Requires Windows 11 x64.

[Getting started and help](https://github.com/ImAvgErix/ExoLauncher/blob/main/docs/HELP.md) · [Support on Ko-fi](https://ko-fi.com/uhherix)

## 3.2.1 — September 7, 2026

This update makes everyday controls respond sooner and brings a more consistent feel to the app.

- **Quicker music controls.** The mini player stays ready when you return from Music. Playback changes appear sooner, and pause or skip can respond while a full-library shuffle is still loading.
- **Faster access to your rooms.** Music, Friends, Discover, and Devices start getting ready earlier while you browse your library.
- **Smoother scrolling.** Mouse-wheel scrolling eases more naturally, changes direction promptly, and behaves better inside smaller panels.
- **A cleaner game page.** The purchase panel lines up with the screenshot thumbnails, with system requirements directly below.
- **Easier window dragging.** Drag Exo from the unused space across the top bar while keeping its buttons and music controls clickable.
- **Consistent mouse profiles.** The profile picker now uses Exo's own menu. Applying a profile and switching between games and the desktop are more reliable.
- **Better volume control.** The mini player's volume adjusts your music service directly, with a migration that preserves loudness from older mixer settings.

**Updating keeps your settings and sign-ins.**

Requires Windows 11 x64.

[Getting started and help](https://github.com/ImAvgErix/ExoLauncher/blob/main/docs/HELP.md)

## 3.2.0 — September 7, 2026

This update makes Exo feel quicker and keeps everyday actions running more smoothly.

- **Faster browsing.** Move between Library, Discover, Settings, and Devices with less waiting.
- **Less jumping around.** Buttons and settings stay in place when a message appears or disappears.
- **Pick up where you left off.** Open a game in Discover, then return to the same spot. Search also finds games you already own.
- **Game updates are easier to find.** Available updates appear in Downloads & updates, with clearer progress for downloads, installs, and Steam Workshop content.
- **Smoother music and Discord.** Better resizing and reconnecting, with less background activity. Apple Music's song timer and shuffle across your library work more reliably.
- **More reliable mouse profiles.** Supported mice switch profiles more reliably as you move between games and your desktop. Opening Devices and choosing games for a profile feels quicker too.
- **More dependable saves and installs.** Improved settings saves and Steam game removal, including when Steam has just opened.

**Updating keeps your settings and sign-ins.**

Requires Windows 11 x64.

## 3.1.0 — September 6, 2026

The familiar 3.0 layout, with reliability improvements and three focused fixes.

- Rounded web-app frames follow the window size without changing provider buttons and panel shapes.
- Store game pages use the right-hand panel for available descriptions, genres, release details, review counts, and a compact hardware summary.
- Activate key stays inside the window, including after resizing.
- Runtime installers download asynchronously with progress and cancellation.
- Saved device profiles remain protected when a profile-file read fails.
- Folder-size scans have depth, directory, and time limits and skip junctions.
- Restored non-visual fixes for transfers, stale responses, caching, embedded recovery, logging, and host contracts.
- Aligned CI with the app's .NET SDK and fixed the backend presence error type.
- The installed app and installer both identify as 3.1.0.

## 3.0.0 — September 6, 2026
**Your games. Your music. Your space.**

Exo 3.0 brings your PC gaming session together: a focused library, music that follows you between rooms, Discord within reach, and supported mouse profiles in one app.

### A home for the whole session
- **Your library, brought together.** Browse installed games across stores, pin favorites, and open detailed game pages with available play and install actions.
- **Music gets its own room.** Open your selected service, keep supported playback running in the background, and stay in control with the mini player.
- **Discord stays close.** Use the embedded Discord website from Friends.
- **Make the setup yours.** Customize artwork, adjust text size, manage local wishlist data, and configure supported mice and game-linked profiles.

### Rebuilt where it counts
This release also strengthens the details behind everyday use: clearer transfer errors, embedded music presentation and recovery, responsive library sizing, keyboard focus, host-message validation, and installer staging with rollback protection.

### Download
Choose **ExoLauncher-Setup.exe** below. Requires **Windows 11 x64** and installs per user. The installer is unsigned; Windows may show a SmartScreen warning.

### What to know
- Store capabilities vary; vendor accounts, clients, and ownership still apply.
- Music and Discord require their own accounts and any applicable subscriptions.
- Mouse controls require supported hardware. Upscaler swaps are explicit, backed up, and limited to supported files; game compatibility is not guaranteed.
- Exo email/password accounts are optional. Hosted password recovery, email verification, email magic links, and Google sign-in are not currently enabled.

[Supported features](https://github.com/ImAvgErix/ExoLauncher/blob/main/docs/SUPPORT_MATRIX.md) · [Report an issue](https://github.com/ImAvgErix/ExoLauncher/issues)

## 2.2.0 — August 26, 2026

**Friend-PC pass: hours, devices, launchers, library, and in-app updates on any machine.**

- Riot lifetime hours prefer tracker.gg overview, not a playlist sum. A card that also has Steam/Epic does not add unofficial tracker minutes onto that store's ledger.
- Newest always re-scans the game folder and refreshes the UI. Anti-cheat titles still only replace dests the game already ships, and that warning is visible.
- Viper V3 Pro, Apex Pro TKL, DualSense, and Xbox pads ship product photos. Viper config accepts MI_01 even when the first HID usage is keyboard. Bluetooth DualSense is no longer skipped as MI_00. Open-fail copy names Synapse / SteelSeries GG.
- DPI is live GET preset chips, not a five-tick Compx dial reused on every mouse.
- Helium and Epic close the real process (Helium chrome/helium.exe via QueryFullProcessImageName) and fail if it is still running. Epic no longer advertises launcher VSync. Riot Client and Ubisoft Connect have quiet-startup / overlay modules on official files only.
- F1 25 reads EA `title`/`gameName` keys. Wuthering Waves is found from Kuro's shipping exe on any fixed drive.
- Legendary / gogdl / nile sign-in: GitHub API 403/429 falls back to the pinned download URL; the error still tells you to put the exe on PATH or in `%LocalAppData%\ExoLauncher\tools`.
- Settings → Check for update shows the GitHub result instead of a stale store message. Rate-limited API checks use the public latest redirect. `VERSION` is always published next to the exe.

## 2.1.0 — August 26, 2026

**Exo Hub merges into Exo Launcher; Optimize and Devices become first-class rooms.**

- Optimize Apply is extreme by default (raw-latency NVIDIA, lowest-latency Internet, experimental packs on). Desktop Apply installs Game Ready; notebooks skip that package. Repair stays best-effort.
- Opening a game keeps the library visible; only the details sheet is a solid card (no black-out, no glass).
- Unlocks preview is a mini desktop: click a corner to place the toast, the banner scales to fit, Replay plays it, Sound is opt-in. Steam/Epic/GOG overlay achievement popups are hidden so Exo's toast is the one you see.
- Steam install/update percent stays on screen: watcher heartbeats no longer hide the live byte sampler, a stale "Starting…" resample cannot wipe a bar that already has bytes, and workshop jobs like MECCHA CHAMELEON count `workshop/content` instead of only `steamapps/downloading`.
- Chrome is fully opaque: no glass, scrims, or backdrop-blur. Overlays, toasts, search, nav, cards, and the command palette sit on solid AMOLED fills.
- Settings About now has text color and size. Upscalers report what Newest actually did. Shell icons are Phosphor throughout.
- Optimize is now a room inside Exo Launcher, not a separate app. NVIDIA, AMD, Windows, Internet, Steam, Discord, Spotify, and Brave are in one list.
- Removed the Windows module from Optimize; Windows/system tuning is Exo OS's job.
- Removed the non-functional "Optimize this title" button from game details; the Optimize room stays separate.
- Tightened Optimize row spacing, padding, and card fitting; one consistent state language across all modules.
- Integrated curated React Bits Pro components: global Ctrl+K command palette, 3D hover on game cards via DepthCard, React Bits settings switch row, and AnimatedList friend activity feed. All React Bits surfaces are themed to Exo tokens (no neutral gray), demo commands are removed, and unused block files are dropped from the UI.
- Unknown or missing Optimize module ids are rejected instead of silently defaulting to Discord.
- Every Optimize detect has a 25-second timeout so a hung PowerShell probe cannot freeze the UI.
- Epic playtime and last-played no longer attach to unverified/unknown-account rows.
- Debug builds are stamped with `+short-sha-dirty` in product version metadata.
- Added a manual "Verify all" button to the Optimize room.
- Added `exohub` and `exolink` to exo-id reserved handles, consistent with retired product naming.
- Devices talks to the board over documented HID / WASAPI / HeadsetControl CLI / XInput / DualSense HID. Not G HUB, Synapse, iCUE, SteelSeries GG, or Sonar.
- Apex Pro TKL (VID 1038, TKL SKUs) reads firmware and does onboard brightness GET-SET-GET from public OpenRGB Apex packets. OmniPoint actuation and Rapid Trigger stay on the OLED / GG.
- DualSense Edge enumerates as a pad. Its USB speakers are not a headset card.
- Helium Preferences, Epic Engine.ini HTTP timeouts, and Steam launch options use the documented files. No Chromium policy hive, no undocumented GUS keys.

## 2.0.3 — August 21, 2026

**Friend Exo profiles look like theirs. Search can install Minecraft and Roblox. Settings lists every launcher.**

- Optimize rows stay one Apply. NVIDIA G-SYNC vs raw latency and Internet lowest-latency vs throughput sit on the row. Newest on upscalers morphs in place. Missing runtimes are named in Settings and start their official installer. Settings Stores is Sign in / Open / Get. Find-people-you-play-with is on Friends. Sort and add-custom sit on All. Reduce motion is an Exo toggle that can bypass Windows. Trophy banners use rarity-colored cinematic motion and play a cue in the Unlocks preview. Leaving Settings stops that preview cue. Internet and AMD apply percents keep moving instead of sitting at 40. AMD chipset Apply unpacks core INF/MSI instead of the fat EXE. Discord on a hybrid PC uses the iGPU. Steam library Low Perf is off. Live install percent re-samples the store adapter while a job is running. Leftover Exo scheduled tasks are deleted on launch. Profile Inspector prefers a newer GitHub prerelease when one exists.

- Friend pages use the same profile chrome they authored, without this PC's hours or a handle line
- Hosted avatars and banners load through the profile-media virtual host, including GIFs
- Minecraft and Roblox appear as installable search hits
- Settings lists every launcher with Get, even when the client is not installed
- Steam same-person linking stays a local claim on this PC, not an account merge
- The CEO badge is gone

## 2.0.2 — August 21, 2026

**Issue #13: Roblox posters, Epic hours, home avatar, and the signed-in onboarding flash.**

- Applied Epic playtime on the library scan so Sugar/Fortnite hours show without waiting for a later refresh
- Preferred Microsoft Store posters for Roblox and Minecraft instead of a Steam title match
- Painted the titlebar chip with the chosen game-cover avatar, and retried a cover-host image once instead of wiping it
- Marked account setup complete when a handle or session already exists, so a signed-in 2.0 install does not flash onboarding
- Stopped Exo friend rows from toggling closed, and kept last-played across a small Epic/Riot clock skew

## 2.0.1 — August 21, 2026

**The 2.0 library people actually hit.**

- Loaded Minecraft and Roblox posters from the Microsoft Store catalog instead of a missing Steam match
- Matched Epic last-played to Legendary/EGL ids and titles, and accepted Riot product aliases
- Opened Exo friend profiles with the reserved handle, kept roster avatars when media arrived without Content-Length, and stopped unverified installed Steam titles from becoming **Unavailable**
- Offered Exo account setup after a 1.x → 2.0 upgrade instead of skipping it
- Sent live download byte counts to the UI so install percent can move
- Stopped the achievement preview from restarting mid-hold

## 2.0.0 — August 21, 2026

**Online identity, truthful libraries, and a launcher that stays out of the way.**

- Added production Exo accounts with remote email/password authentication, DPAPI-protected desktop sessions, reserved handles, expressive profiles, avatar/banner/gallery media, privacy controls, direct friends, blocking, and presence that becomes unknown when providers are unavailable
- Added server-authoritative profile badges and owner-only badge management while keeping roles and management capability out of public profile responses
- Made store identity and entitlement state explicit so linked Steam, Epic, and GOG accounts cannot be reused by another Exo profile, refunds become **Buy again**, and unknown ownership never becomes a false Play action
- Rebuilt artwork discovery and caching across library, profile, friends, and game detail surfaces with validated local overrides, refetch/report controls, high-resolution friend art, collision-safe cache keys, and resilient fallbacks
- Expanded achievements with account-scoped Steam/Epic providers, retries, honest unavailable states, secure icon origins, expressive Exo notification themes, and a compact profile trophy cabinet
- Hardened optional DLSS, FSR, and XeSS updates with signed-vendor validation, correct x64 PE export parsing, official-source preference, semantic version truth, persistent caches, safe restore, and neutral states for unusable destinations
- Added a compact active Now dock, responsive virtualized libraries, smoother preloading, faster cached startup, cleaner search, game-detail title actions, controller navigation, and extensive accessibility/reduced-motion contracts
- Deployed the Cloudflare exo-id Worker with D1, R2, per-user Durable Object presence, strict privacy/rate limits, and offline fail-open behavior for the local launcher
- Replaced GitHub page and README screenshots with the current library, game, profile, and settings surfaces

Google sign-in and email magic links remain unavailable until real provider credentials are configured. Email/password accounts are live.

## 1.0.103 — August 21, 2026

**A useful Now dock instead of a decorative hero.**

- Replaced the oversized landscape banner with a compact 112px active-state dock using a cached cover thumbnail
- Added store, total playtime, transfer phase and speed where available while keeping one clear state action
- Hid recent-only Now state so Pinned moves up whenever nothing is playing, transferring, or awaiting an update

## 1.0.102 — August 21, 2026

**Title-owned favorite and visible close control.**

- Moved the game-detail favorite action beside the game title and removed its floating artwork overlay position
- Increased close affordance size and icon contrast with a solid surface, stronger border/shadow, and clear hover/focus feedback

## 1.0.101 — August 21, 2026

**Full-bleed heroes without boxed-in details.**

- Restored full-bleed Steam library heroes with a top-biased focal crop instead of visible side pillars
- Removed the heavy stats card and outer utility container while retaining readable text shadows and compact individual button surfaces

## 1.0.100 — August 21, 2026

**Uncropped heroes and readable controls.**

- Contained standard Steam library heroes over a blurred edge-fill in the ultra-wide home banner so faces and focal artwork stay visible
- Reduced search to 96px resting and 184px focused, with its caret delayed until the 200ms expansion completes
- Made detected but ineligible or unsupported upscalers neutral gray while preserving red only for usable outdated files
- Added stable contrast plates to game stats and utility controls so bright banner artwork cannot wash out labels or buttons

## 1.0.99 — August 21, 2026

**Favorite controls belong in details.**

- Removed the favorite star, hit target, hover behavior, keyboard focus path, and related styling from every library and pinned card
- Kept the single favorite action in the game detail overlay

## 1.0.98 — August 21, 2026

**Unboxed favorites and visible hero art.**

- Replaced the persistent black favorite badge with one floating, shadowed star and a transparent accessible hit target
- Restored full-strength home hero artwork and brightened game, profile, and showcase banners while retaining localized text contrast

## 1.0.97 — August 20, 2026

**Trusted upscaler recovery and final visual fit.**

- Fixed x64 PE export validation so genuine signed NVIDIA, AMD, and Intel upscaler DLLs pass provenance checks, with official-source preference and bounded persistent catalog and per-game status caches
- Fixed real Legendary ownership caches containing null install sizes so Rocket League and other Epic entitlements remain playable, and made successful same-account background refreshes republish the library immediately
- Refined the pinned shelf, card corners, single-layer favorite control, and title spacing; restored a visibly oval search capsule with a smooth reduced-motion-aware expansion
- Removed the Profile Studio divider, tightened its spacing, and kept failed upscaler downloads retryable with readable error wrapping

## 1.0.96 — August 20, 2026

**Online authority, repaired artwork, and entitlement truth.**

- Deployed server-authoritative owner/developer roles and fixed profile badges; Erix now has the exclusive Founder plus CEO and Developer badges, with a bounded owner-only community badge manager
- Repaired the retained pinned-shelf offset, broken Friends preloader layout, cover-corner wedges, profile density, and the oval titlebar search with a smooth reduced-motion-aware reveal
- Hardened profile/library art with exact origins, streamed byte limits, full decode validation, atomic promotion, collision-safe native keys, legacy-cache compatibility, and post-write pressure control
- Made upscaler status compact and honest while requiring signed vendor identity, expected PE exports, embedded version agreement, byte equality, stale-restore protection, and fail-closed anti-cheat checks
- Scoped achievements to the active provider account, required a complete matching Steam schema, redacted locked hidden data, and removed arbitrary remote icon loading
- Added typed entitlement propagation across variants and disk cache so refunded/revoked games show **Buy again**, unknown ownership stays unavailable, and native launch/install/update paths fail closed
- Deferred background store observers and trophy-WebView warming until the shell is visible, and restricted WebView local/image access to the minimum required origins

## 1.0.95 — August 20, 2026

**Readiness-driven startup and a complete profile viewport.**

- Made the opening Exo mark represent real library readiness by decoding first-screen covers and prewarming installed-game upscaler status before revealing the shell
- Cached DLSS, FSR, and XeSS status so game details open with their tool rows already populated
- Added restrained search expansion without the white focus box, with an instant reduced-motion state
- Moved the trophy cabinet beside the showcase and compacted game rows so profile achievements remain visible without scrolling at the standard window size

## 1.0.94 — August 20, 2026

**Reliable profile art and cleaner identity.**

- Fixed lazy covers in hidden profile rooms exhausting their fallbacks before the browser requested them; all library art now waits until its tile approaches the viewport
- Removed the redundant public handle line and its obsolete visibility control from the profile surface
- Let profile-picker titles wrap fully and simplified the nested Activity statistics surface

## 1.0.93 — August 20, 2026

**A cleaner favorite and quieter game details.**

- Removed catalog descriptions from the opened game surface while retaining store, genre, and year
- Replaced the oversized layered favorite control with one compact 30 px surface and a clearer 15 px state glyph

## 1.0.92 — August 20, 2026

**Complete titles, expressive profiles, and stricter provider truth.**

- Enlarged library cards and made the home surface cover-and-title only, with complete wrapped names and detail-owned store, genre, year, and description
- Rebuilt the profile showcase around featured artwork, real achievement progress, a provider-backed trophy cabinet, and safer account-scoped achievement reads
- Added Epic/Riot/Deadlock metadata fallbacks, corrected Riot's 2XKO mapping, and stopped install history or another account's cache from becoming ownership
- Hardened upscaler downloads with vendor signature checks, improved WebView memory behavior, and removed forced termination of pre-existing store clients

## 1.0.91 — August 20, 2026

**Reachable shelves and warmer navigation.**

- Fixed the pinned shelf starting offset that clipped the first game; added explicit previous/next controls so every pinned title is reachable
- Kept card metadata on the cards, removed snap-induced horizontal clipping, and preserved consistent single-line title geometry
- Preloaded Settings, Friends, Profile, and likely-next artwork behind the first shell paint so navigation does not start cold network work

## 1.0.90 — August 20, 2026

**Entitlement-safe installs and a bounded edge-case sweep.**

- Steam owned-game snapshots now distinguish an authoritative empty library from a private/unavailable response, so refunded titles return to Buy instead of inheriting stale Download state
- Cached manifest proof is pruned only after an authoritative account snapshot, and direct install bridge requests reject unowned titles
- Hardened Steam JSON reads with bounded response bodies and tightened store-search ownership semantics so install capability never masquerades as entitlement
- Friend activity cards now use warmed wide art with a portrait fallback, so Steam/Epic/Riot games do not collapse into blank banner slots

## 1.0.89 — August 20, 2026

**A skeptical social refresh and provider-ready identity.**

- Kept the pinned shelf in one horizontal row, shared catalog metadata across shelves, and exposed full card metadata to assistive technology
- Made social refreshes capability-isolated, removed the initial false outage flash, and added Steam portrait fallback for friend activity
- Added honest Google/email-link readiness messaging and operator setup documentation without fabricating credentials
- Added bounded, idempotent Resend delivery when email credentials are configured

## 1.0.88 — August 20, 2026

**Sharper cards, expressive profiles, and honest upscaler status.**

- Cards now keep a single-line title and compact `Store · Genre · Year` metadata when official catalog data is available
- Removed the redundant achievement progress bar and added Exo-specific flare/eclipse and nova/black-hole unlock effects
- Moved dependency checks into About, reset search when leaving the library, and improved favorites and profile editor controls
- Added drag reordering, eye visibility toggles, handle display privacy, richer showcase presentation, and corrected FSR 3.1 semantic version selection

## 1.0.87 — August 20, 2026

**Production Exo identity, reliable artwork, and a cleaner social surface.**

- Deployed the production Exo ID Worker with D1 migrations for bounded profile GIF/gallery media and provider-scoped account uniqueness
- Fixed wide cached hero/banner art being rejected on wide surfaces while keeping portrait cards poster-safe; stale lower-revision covers no longer overwrite refreshed art
- Made profile identity changes debounce-save to the signed-in Exo account, with resilient avatar/banner fallbacks and high-quality friend/Steam artwork
- Kept Steam `personastate=0` as authoritative Offline and unknown/private responses unavailable rather than falsely online
- Widened and symmetrically padded title-bar search so the full placeholder remains visible

## 1.0.86 — August 19, 2026

**Faster large libraries, repairable artwork, and one calmer interaction system.**

- Added native per-game Replace cover, Reset cover, Refetch artwork, and Report wrong art controls with strict PNG/JPEG validation, local content-addressed storage, grouped-card ownership, authoritative reset, and cache-version busting
- Replaced paint-only containment with zero-dependency row virtualization: the main grid preserves exact height and keyed focus while mounting at most 99 cards at 1920×1080, even for 5,000-game libraries
- Added honest local-only store diagnostics and retry: Amazon/Nile sessions are recognized, Steam ownership requires a readable account, and Epic/GOG download capability requires the actual helper instead of optimistic labels
- Fixed stale global keyboard activation, added column-preserving grid navigation and F2 Pin/Unpin, and added conservative gamepad navigation that can open only explicitly safe views—never Play, Install, Update, Stop, Remove, dependency, purchase, account, or artwork actions
- Scaled the native minimum window for display DPI, added Windows High Contrast and stronger focus rings, repaired radio/step focus behavior, announced real trophy overlays, moved progressbar semantics outside buttons, and added proper main landmarks
- Made the Exo account form a real single-column flow with a quiet Sign in/Create account switch and one primary submit action; shared shell chrome now owns the final titlebar/button cascade
- Reused parsed Steam playtime, kept the synchronous cover handler only as a virtual-host failure fallback, and added privacy-safe process-to-window startup timing plus a non-capturing restore callback
- Kept Friends game art portrait-shaped, Steam `personastate=0` authoritative Offline, unknown/private Steam states Unknown, and avatar status textual rather than a presence circle

## 1.0.85 — August 19, 2026

**Optional email/password accounts, profiles, and friends—without putting the library behind an account.**

- Deployed the optional exo-id stack with Better Auth, D1, R2 profile media, per-user hibernating Durable Object presence, hourly metadata cleanup, and a pinned HTTPS origin; disposable production smoke accounts were removed afterward
- Added email/password account creation and sign-in: strict 12–128-character passwords, salted Scrypt hashes in D1, native-only DPAPI bearer sessions, exact 2 KiB auth bodies, and no password/hash/token returned to React
- Added privacy-aware public profiles and sharing, reserved handles, direct requests, accepted friendships, removal suppression, blocking, verified Steam/Epic/GOG link contracts, and Steam/Epic mutual discovery
- Kept bearer sessions native in DPAPI-protected storage; added viewer-scoped offline caches, bounded sanitized avatar/banner media, session-revalidated presence, provider capability detection, export, and fresh-auth account deletion
- Wired online diagnostics, privacy, sessions, store links, requests, profiles, media, and presence into the existing React/WebView shell while preserving signed-out local/store fallbacks and never putting identity on Play, install, launch, or library scan paths
- Restored portrait Friends art, preserved selected avatar/banner art through edits, made the profile rail/showcase responsive with an opaque editor header and readable activity stats, and fixed compact-titlebar search clipping
- Removed avatar presence-dot overlays while retaining textual status; used Steam `lastlogoff` as evidence for Offline while keeping indeterminate/private rows unknown, session-revalidated, friend-only, and bounded
- Made keyboard actions immediate, gated hover to fine pointers, honored reduced motion on continuous spinners, and removed redundant anti-cheat warning copy without weakening native refusal
- Kept Google and email magic-link sign-in visibly unavailable without real provider credentials. Password-account emails are currently unverified, and password recovery is unavailable until real mail delivery exists

## 1.0.82 — August 17, 2026

**Native WinUI 3 shell. No React, no WebView2 UI.**

- Home, Now, tiles, the 400px plate, settings, and first-run are XAML + C#. `ShellController` replaces JSON-RPC `WebHostBridge`.
- `dotnet build` is the UI build. WebView2 remains for GOG login only.

## 1.0.81 — August 15, 2026

**One chrome house. Play and Apply actually click.**

- First-run is for a stranger. Steam-only names Steam. Nothing names the next move (add a folder). Missing stores are not listed. Empty library does not send them to search.
- Titlebar is the mark. No Exo / Launcher wordmark. Play lives on the game (hero, card, detail), not the chrome.
- Settings is one column, not a catalog. Stores on this PC only. Achievement toast is 360x80, waits to be on screen before it animates, and Preview no longer lies when nothing showed.
- tokens.css no longer forks winbtn size or hover. Cover virtualization matches the CSS grid. Empty-library and banner CTAs keep chrome height.
- Settings and first-run wait for the real store list. They do not paint four fake Not installed rows.
- Official-store Update/Install/Play that only opens the vendor client is a handoff, not Update complete.
- Library grid no longer skip-jumps from content-visibility or a measure loop.
- Search is not a store shelf. Unowned catalog hits stay off the live screen. Owned-not-installed sit under On your account, not Install. Public Steam catalog is not queried when Steam is missing. Settings says so when no store apps are on the PC.

## 1.0.80 — August 15, 2026

**Apply swaps what the title already ships. Play just launches.**

- One Apply on the armed game fetches and swaps only DLSS / FSR / XeSS files that title already has. A DLSS-only install is not told to download the rest.
- Play does not wait on the upscaler pack. Apply can fetch. XeSS and other latest labels only claim a real version.

## 1.0.79 — August 13, 2026

**Closing a game no longer flashes Home.**

- The card stays until the dim fades. A CSS enter animation was holding opacity at 1, so the fade popped instead.

## 1.0.78 — August 13, 2026

**Library tiles open in place.**

- Clicking a title in the grid no longer scrolls Home under the game page. Close details stays put. The Now plate and pinned row stay where they were.

## 1.0.77 — August 13, 2026

**Opening a library tile no longer breaks Close.**

- Game cards and the game page do not share a poster morph. That projection was stealing clicks after the Now plate fix, so regular titles did the same in-and-out mess.

## 1.0.76 — August 13, 2026

**Install percent is visible. Uninstall does not skip the library. Now opens and closes once.**

- Steam’s ACF often sits at `0 / total` while a leftover `downloading/` folder is already larger than this job. That is unknown, not 0% and not 100%. The bar is indeterminate until `BytesDownloaded` moves, then it shows the live percent on the button.
- Uninstall no longer publishes install Preparing/Completed, so Now does not flip to Downloading and the library does not reload twice.
- The Now plate is a real open control. It does not share a poster morph with the game page, and Close details sits above the overlay.

## 1.0.75 — August 13, 2026

**Steam percent matches the live job. Home has one Now plate. Steam chrome stays gone.**

- Install percent uses the `steamapps/downloading` folder when content_log is only the start snapshot (`download 0/total`). Exo no longer freezes at 0% while Steam’s Downloads row climbs.
- Uninstall calls Steam IPC once. A failed confirm is not retried, and `steam://uninstall` only runs if the helper never reached the client. Steam stays hidden.
- Search no longer flashes the same owned title in Library and Install. Dead Steam OCR/capture/click paths are gone.
- Home’s featured row is one game with one reason (downloading, playing, update, last launched). Wide Steam `library_hero` only. Not a carousel.

## 1.0.74 — August 13, 2026

**Install percent follows Steam’s download. Uninstall stays hidden.**

- The install bar uses Steam’s live `download done/total` from content_log when the appmanifest still holds leftover totals, and it no longer jumps from 99% download to a 40% staging bar.
- Uninstall no longer restores the Steam window. Steam stays a hidden backend.

## 1.0.73 — August 12, 2026

**Home is installed games. Steam uninstall reports the real result.**

- The library grid is downloaded titles only. Owned-but-not-installed Steam games stay in search as Download.
- Steam uninstall no longer treats every helper exit as success. A hidden Steam confirm was swallowing the removal; Exo now leaves Steam visible for that step and fails in seconds if files do not start going.

## 1.0.72 — August 12, 2026

**Steam percent, Download for owned titles, smoother motion.**

- Install/update percent ignores leftover ACF totals from a previous job, uses staging when the download counters are already finished, and polls every 400ms. A leftover 37 GB counter next to a 36 MB patch is no longer shown as 100%.
- Steam titles in this account’s librarycache that are not installed appear in Exo with Download. Search uses tickets, localconfig Apps, and that cache — not Buy — when Steam already knows the game.
- Library tiles, the progress fill, and the busy mark use short compositor tweens. Springs and perpetual `willChange` layers are gone.

## 1.0.71 — August 12, 2026

**Progress, Open store, one client at a time.**

- Steam install/update percent is `downloaded / toDownload` from the appmanifest. Exo no longer maps 0–100 onto 10–95 or invents a climbing number while queued.
- Settings → Open shows any installed official client, including during an update. Steam gets `steam://open/main` so a silent instance actually appears. Store hiders skip a client the user just opened.
- Minimize-while-playing is always on. The Settings checkbox is gone.
- Idle titlebar mark is the original E (short middle bar), optically tighter in the square. The three equal bars only run while Exo is busy.
- Launching or opening one store asks the others to exit. Steam gets `-shutdown`, Riot gets its client kill API, then a graceful thread quit. Unused shells that ignore that are terminated. Vanguard is never touched.

## 1.0.70 — August 12, 2026

**Steam updates actually start.**

- This Steam build’s `GetIClientAppManager` is engine slot 43, not 36. Slot 36 was the network-device manager, so installs returned a fake error and bytes never moved.
- `InstallApp` takes app id, folder index, and a legacy flag. Deadlock’s queued 36.6 MB patch downloaded through that call.
- The Steam helper is a self-contained process in `steam-ipc\`, so it does not share WinUI’s runtime.

## 1.0.69 — August 12, 2026

**Steam is commanded as a backend. The mark only moves when Exo is actually doing work.**

- Install, update, and uninstall talk to the running Steam client through a helper. This Steam build’s app-manager layout does not yet match the known slots, so Exo fails honestly if bytes never move instead of clicking Steam chrome.
- Library tiles use 600×900 posters instead of 1200×1800, so WebView does not decode a 2x bitmap for every card.
- The three bars sit on one diagonal. The wave runs during boot, install, update, search, and launch — not while idle.

## 1.0.68 — August 12, 2026

**Steam’s Downloads row is read by a helper that never loads WinUI.**

- Capturing from ExoLauncher.exe stayed black even in a child process. A separate capture host snapshots the painted desktop, then Exo clicks the queued title

## 1.0.67 — August 12, 2026

**Steam’s Downloads row is read by a short helper process, not the WinUI compositor.**

- Capturing from inside Exo kept returning a black Steam window. Exo now snapshots the painted desktop from a child process, then clicks the queued title

## 1.0.66 — August 12, 2026

**A bad GPU texture create no longer blocks reading Steam’s Downloads row.**

- Desktop capture now creates an empty staging surface. If that path still fails, Exo keeps waiting for a painted compositor frame and clicks the queued title

## 1.0.65 — August 12, 2026

**Steam’s Downloads row is copied from the painted desktop, not the first black GPU frame.**

- The compositor often hands Exo an empty first frame. Exo now waits for a real desktop image, crops the Steam window, and clicks the queued title

## 1.0.64 — August 12, 2026

**Steam’s Downloads row is read from the monitor compositor, then cropped.**

- Capturing Steam’s HWND stayed black. Exo now copies the painted desktop the same way a screenshot does, keeps the Steam rectangle, and clicks the queued title

## 1.0.63 — August 12, 2026

**Steam’s GPU frame is captured the same way a desktop screenshot is.**

- GDI still copies a black Downloads window even when Steam is on top of Exo. Windows Graphics Capture reads the compositor, then Exo clicks the queued row

## 1.0.62 — August 12, 2026

**Steam has to activate for a moment or its Downloads UI stays black.**

- A no-activate overlay covers Exo with an empty GPU surface. Exo now shows Steam for the OCR click, then hides it

## 1.0.61 — August 12, 2026

**Steam’s layered UI is copied with CaptureBlt, not a black GDI blit.**

- Default CopyFromScreen skips DirectComposition windows. Exo now includes layered pixels, reads the leased Steam frame, then clicks the queued row

## 1.0.60 — August 12, 2026

**Steam’s GPU UI is read from the screen, not PrintWindow.**

- Modern Steam captures black via PrintWindow. Exo copies the visible Downloads frame from the desktop, clicks the queued row, then hides Steam

## 1.0.59 — August 12, 2026

**Steam has to be on top for a queued update to start.**

- Chromium captures black when the Downloads window is covered or off-screen. Exo shows it over the shell for the OCR click, then hides it again

## 1.0.58 — August 12, 2026

**Steam’s child windows stay painted during a hidden update.**

- The hide loop no longer covers Chrome children of a leased Downloads window. A capture dump lands in logs when OCR is empty

## 1.0.57 — August 12, 2026

**Steam paints behind Exo during a hidden update.**

- The Downloads window sits under the Exo shell instead of off the desktop. Chromium actually draws, so OCR can read the queued row

## 1.0.56 — August 12, 2026

**Hidden Steam still paints for OCR.**

- The offscreen Downloads window keeps a 2px sliver on the desktop so DWM composites it, and OCR reads the Chrome child instead of a blank SDL frame

## 1.0.55 — August 12, 2026

**Steam OCR can actually read the Downloads row.**

- Captures are written with DataWriter and DetachStream. The previous flush wrapper disposed the buffer, so every frame died as ObjectDisposedException

## 1.0.54 — August 12, 2026

**Steam OCR no longer aborts a queued update.**

- The hidden Downloads screenshot is flushed into WinRT before decode. A bad frame is skipped instead of killing the whole update

## 1.0.53 — August 12, 2026

**Steam queued updates actually start.**

- A patch sitting at 0 bytes with Steam’s UpdateStarted bit still gets the hidden Downloads-row click. Previously Exo treated that as already busy and never promoted it

## 1.0.52 — August 12, 2026

**Search placeholder clears on focus.**

- Clicking the search pill hides “Search” so the caret isn’t sitting on the word

## 1.0.51 — August 12, 2026

**The mark actually moves.**

- Titlebar, boot, and Settings logos run the three-bar wave — draw in, then scale like Grok’s thinking mark. The old 2px wobble is gone

## 1.0.50 — August 12, 2026

**Home is just the library.**

- The Continue Playing film strip is gone. Pinned and the grid are the home screen

## 1.0.49 — August 12, 2026

**Chrome icons are one family, one size.**

- Titlebar, search, Play/Stop, pin, folder, and Settings use Phosphor at 16px — not Amicons sharp
- Outline for chrome. Fill for Play, Stop, and pin-on. Lucide and Tabler stay out

## 1.0.48 — August 12, 2026

**The titlebar is easier to grab.**

- Empty space beside search, and the strip above and below the pill, drags the window. Logo, search, and window buttons stay clickable

## 1.0.47 — August 12, 2026

**Continue Playing stays put.**

- The spotlight film no longer auto-rotates through titles. Click a poster if you want a different one

## 1.0.46 — August 12, 2026

**Chrome uses Amicons.**

- Titlebar, search, Play/Stop, pin, folder, and Settings glyphs are Amicons free sharp — not the homemade 1.5-stroke set
- Line for chrome, solid for pin-on and Stop. Lucide and Tabler stay out

## 1.0.45 — August 12, 2026

**One launcher at a time.**

- Opening Steam, Epic, GOG, Riot, Xbox, EA, Ubisoft, Battle.net, Amazon, or Rockstar from Settings closes the others
- Play, install, and update do the same for unused store clients
- Steam as a hidden backend starts without Friends or chat toasts. Settings → Open Steam still brings the full client
- Rockstar close/hide is path-qualified so an unrelated `Launcher.exe` is left alone

## 1.0.44 — August 12, 2026

**Steam Uninstall confirms the prompt.**

- Uninstall sends a hidden `steam://uninstall/<appid>` for the selected title, then clicks only an offscreen OCR-verified Uninstall/Remove control that names that game — not Cancel, not a sequel
- Failure stays on screen instead of vanishing after a few seconds

## 1.0.43 — August 12, 2026

**Steam Update actually starts the patch.**

- A scheduled Steam update no longer dies after one missed Downloads-row click. Exo clears that game's schedule, keeps requesting the exact app, and retries the hidden row until bytes move
- Downloads OCR still requires the selected title, but accepts Steam's size/percent/scheduled suffix and a split title line so Counter-Strike 2 matches its own row — not a sequel or a dedicated server

## 1.0.42 — August 12, 2026

**Update sits on the poster.**

- Games with an update get a white Update mark on the cover and a brighter frame — not a gray store-line footnote, not an orange brick

## 1.0.41 — August 12, 2026

**Search does not spin.**

- The titlebar search pill no longer grows a spinner while stores look up results; progress stays on the Install list

## 1.0.40 — August 12, 2026

**Shine only on the poster.**

- Cover sweep runs when the pointer is on the poster, not the title, caption, or plate around it

## 1.0.39 — August 12, 2026

**A game you already have is never Buy. The strip is a film of posters.**

- Search collapses Steam catalog Buy rows when the same title is already in the library from any store (Steam, Epic, GOG, Riot, Xbox, EA, Ubisoft, Battle.net, Amazon, Rockstar, local)
- Opening a leftover catalog hit still lands on the library card
- Spotlight is a film strip: current poster larger, neighbors peek, no dots — click a peek to switch

## 1.0.38 — August 12, 2026

**Splash waits for the library. One stroke icon set. The mark actually animates.**

- Cold start keeps the splash up until installed covers and playtime are on the rows (20s cap); owned-not-installed posters keep filling after
- Boot mark is three HTML bars (WebView2 would not animate SVG polygons), held at least 1.4s so the assemble is visible
- Chrome icons are one 24-grid, 1.5 stroke, square-cap set — not Tabler, not mixed sizes
- Spotlight resets when the slide set changes identity (same length, different games no longer desync); Update/Install on the strip uses the download mark, not Play

## 1.0.37 — August 12, 2026

**The Exo mark is the mascot. Chrome uses one icon pack.**

- Boot and titlebar use the slanted three-bar E; on boot the bars assemble and idle like a visor
- Copy `brand/exo-mark.svg` and `ui/src/brand/` into ExoOS / Exo Control for the same identity
- Window and in-app icons are Tabler (MIT, 6100+) at 1.75 stroke — not Lucide, not a paid pack we cannot ship

## 1.0.36 — August 12, 2026

**Covers for every store, cached on disk.**

- Library warm fills a poster for every real title (Steam, Epic, GOG, Riot, Xbox, EA, Ubisoft, Battle.net, Amazon, Rockstar, local) — not only Steam app ids and favorites
- Non-Steam games resolve official Steam library posters by title, then keep the file under `%LOCALAPPDATA%\ExoLauncher\covers`
- GOG uses the store’s own cover URL / Galaxy v2 art instead of a fake `{id}_product_tile` path
- Unreal plugins and the Add-portable row stay out of the cover crawl

## 1.0.35 — August 12, 2026

**Owned means Download. Covers paint without hover. The strip is a detached plate.**

- Search for a game you own says Download, not Buy on Steam, and opening it stays on the game plate
- Steam treats a mismatched TargetBuildID as an update; the caption stays quiet
- Covers decode immediately (no opacity-0), tile shine still sweeps on hover
- Startup is the logo splash while settings and the library load — not “Starting…”
- Rotating strip drops Download, skips live-service loops like Rocket League for Pick back up / Unplayed, and sits on a detached dark plate with a sharp inset poster

## 1.0.34 — August 12, 2026

**Search hugs the word. Spotlight is a tall poster panel.**

- Search sits in the gap between the logo and the window buttons, sized to “Search”, then grows when you type
- The rotating strip is a 280px plate with a full-height portrait on the left — not a small card, not a washed background

## 1.0.33 — August 12, 2026

**The strip is a plate again. Search is a small pill.**

- Spotlight is a dark plate with a sharp poster on the left — no veiled background wash
- Download slides prefer a title that actually has a poster
- Centered search starts at 118px and grows to 240px when you type

## 1.0.32 — August 12, 2026

**Update lives under the title. Continue is the game’s art, not a widget.**

- Library cards no longer stamp an Update pill on the poster — it sits in the caption with the store name
- The rotating strip fills with the same portrait, veiled, with a sharp poster and the lane label on the art
- Search sits in the middle of the titlebar as a small pill and grows when you type

## 1.0.31 — August 12, 2026

**Covers show up. Download means download. Labels say what they mean.**

- Library posters load without waiting for hover; Steam capsules that are a hair under 450px tall still count as covers
- Cold start is a quiet logo and bar under the titlebar — not “Scanning libraries…”
- Tile shine is a clipped sweep inside the rounded frame
- Owned store titles say Download, not Buy; opening one from search keeps the game plate open
- Steam pending byte deltas count as updates; the Update badge matches the other quiet badges
- Spotlight is a detached dark plate with a poster: Continue playing, Update ready, Download, Been a while / Unplayed — no Last / Played / Almost done
- Game plate stats are Time played and Last launched
- Settings is tighter; achievement toast says Unlocked and the cue is a warmer chime

## 1.0.30 — August 12, 2026

**A compact spotlight that pages itself.**

- Continue Playing is a short strip again, not a tall hero
- The strip rotates Continue / Last played / Almost done (achievement 60–99%) / Update ready when those are real and distinct
- Arrows and dots to go through by hand; auto-advance pauses on hover. Almost done uses cached achievement snapshots only — no home-screen refresh flash

## 1.0.29 — August 12, 2026

**Click-in is a plate over the library. Continue is the hero, not a green field.**

- Opening a game keeps the library behind a blur and puts poster, stats, and Play on a dark plate
- Continue Playing is a full-bleed Steam hero with a black text veil — no hashed color wash
- Library and pinned cards are a step larger, with side inset so rounded edges are not sliced
- Tile and poster fallbacks stay `#050505` instead of a hue that changes per title

## 1.0.28 — August 12, 2026

**Readable stage, honest hours, a Continue banner that isn’t a cropped slice.**

- Continue Playing uses a wide Steam hero on the right of a taller strip, faded into a color field — portraits are never sliced across the banner
- Library cards sit at Grok scale (larger than pins, not huge); cards meet the window lip
- Game page type sits on a dark plate over a colored wash so Rocket League stays readable
- Playtime keeps the highest known hours on a card; opening a title no longer flashes Checking / Updating
- Tile shine is an inside highlight so rounded edges stay clean

## 1.0.27 — August 12, 2026

**Home layout and art that match what you actually see.**

- Game page sits on a hue gradient with a blurred cover wash; Continue Playing stays a sharp 2x Steam hero (or 2x poster), not a stretched CSS background
- Pinned titles no longer repeat in Library; library cards are larger than pinned cards
- Pinned count and Recent / A-Z / Played chips are gone

## 1.0.26 — August 12, 2026

**Library polish: no clipped pins, no fake blur, a window that actually resizes.**

- Pinned games wrap in the same grid as the library instead of a dual-axis carousel with edge masks, and pinned titles stay in Library
- Continue Playing and the game page use the cover as a color wash with a sharp poster — portraits are never blurred to fake a hero
- Opening a game keeps last-good playtime and achievements on screen; background refresh no longer flashes Checking / Updating or overwrites known hours with zero
- Steam lifetime hours prefer `PlaytimeForever` when both keys exist
- Cards sit on the window lip, hover Play is gone, and shine is clipped inside the tile so rounded edges stay clean
- The window is resizable and maximizable, default 1400×900, floor 1100×700

## 1.0.25 — August 12, 2026

**Working stop, real store libraries, and the Grok library shell.**

- Stop kills verified game helpers without `Kill(entireProcessTree: true)`, so Easy Anti-Cheat / BattlEye / Vanguard children are never force-terminated
- Steam achievements stay on the active account; uncorroborated `0 / 0` no longer wipes last-good counts, and a confirmed empty catalog shows None instead of `0 / 0`
- Playing / Stop flags clear when the host says the process is gone
- EA, Ubisoft, Xbox PC, Battle.net, Amazon Games, and Rockstar titles appear from proven local installs and launch through the official protocol or executable
- Library home matches the intended shell: Continue Playing hero, titles under posters, Recent / A-Z / Played, and a full game page instead of a side rail

## 1.0.24 — August 11, 2026

**Exact source control · session-true trophies · broader honest client support.**

- Kept exact same-title Steam and Epic copies on one card while retaining source-specific ownership, installation, updates, playtime, account scope, achievement lookups, and Play/Install/Update/Stop/Uninstall routing
- Isolated Steam and Epic cache state by active account, and kept machine-proven installs visible without turning unknown account identity into a false ownership claim
- Sent Steam install/update requests directly to the selected app ID instead of letting missing generic runtimes divert the action, and added a bounded Epic cold-start readiness handoff for exact game URIs
- Prepared the achievement baseline before vendor handoff, activated attribution only after a successful launch, and prevented manual/detail refreshes or failed launches from fabricating unlock notifications
- Rebuilt the achievement cue as an original 1.18-second 48 kHz stereo Exo sound and aligned the in-app preview with the native 432×122 AMOLED plate, real achievement art, rarity accents, scale-and-fade motion, and exact nine-point anchoring
- Expanded quiet-mode client catalogs to Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games, and Rockstar Games Launcher while explicitly protecting services, overlays, anti-cheat, game processes, and user-opened client windows
- Added honest Installed/Not installed and Open-only integrations for those official clients without inventing libraries, account state, or unsupported per-game actions
- Reworked Settings into a compact two-pane workspace with support links, restored card hover lift and clear store wording, constrained pinned-game overflow, upgraded undersized cover art, and made remaining app surfaces pure AMOLED black
- Removed idle Core Audio enumeration and endpoint rebinding while preserving fast 250 ms suppression during active Exo-driven operations
- Moved WebView2 state outside the replaceable application tree and bounded shutdown to Exo-owned WebView children so atomic updates do not strand cache-only rollback folders

## 1.0.23 — August 11, 2026

**Reliable trophies · honest store status · faster first paint.**

- Rebuilt the native achievement notification as one coherent 432×122 trophy plate with exact nine-point screen anchoring, directional entrance/exit motion, reduced-motion support, and Bronze, Silver, Gold, and Platinum accents derived from trusted provider data
- Added validated local caching for real Steam and Epic achievement artwork, including corrupt-cache repair, so the provider's unlocked image is ready when the notification appears
- Added a durable, account-scoped presentation outbox: unlock transitions are saved before dispatch, acknowledged only after the native window opens, and safely replayed after a matching account refresh if Exo closes between those steps
- Replaced the rejected generated cue with Kenney's compact CC0 glass achievement sound, shipped with its source and license notice and validated before playback
- Separated visible vendor clients from Exo's headless backends so bundled gogdl or Legendary can no longer make absent GOG Galaxy or Epic Games Launcher installations appear Ready, Connected, or Openable
- Removed remote Epic playtime from the startup critical path, retained last-good values across transient service errors, deferred background cover networking until after first paint, reduced cold-start cover contention, and added startup milestone logging
- Kept Steam partial achievement summaries tied to Steam's authoritative totals while accepting a newly exposed unlocked row only when an exact one-count provider delta identifies it unambiguously

## 1.0.22 — August 11, 2026

**A sharper Exo achievement signal.**

- Replaced the simple tone stack with an original 0.78-second stereo glass-and-air cue that adds a restrained cinematic lift without reusing or redistributing third-party audio
- Added a deterministic source generator, strict PCM validation with a built-in fallback, and single-flight playback so rapid unlocks cannot stack the reward sound

## 1.0.21 — August 10, 2026

**Quiet game sessions · verified achievements · smarter discovery.**

- Added a safe Stop action for running games with exact executable, install-root, process-instance, and launcher/overlay/anti-cheat protections
- Kept Steam, Epic, GOG, and Riot client chrome and client audio quiet while Exo drives installs, updates, launches, and active game sessions, then restored only audio sessions Exo muted
- Rebuilt trophy notifications as a compact branded Exo signal with an authored reward cue, a fixed 3.5-second display, and nine exact preview-matched screen anchors
- Made Steam zero-achievement results require independent official-catalog confirmation, removed stale Community XML progress, revalidated details on open, and made contradictory Epic totals fail closed
- Added typo-tolerant, punctuation-insensitive, word-order-aware library and store search, including safe accidental sequel-number matching such as `Mortal Shell 2`
- Removed playtime from dashboard cards, redundant detail status, account/profile/sync surfaces and retired local profile data, notification timing/sound choices, and explanatory Settings copy while enlarging the remaining Settings workspace
- Increased update-badge contrast and kept update actions scoped to the exact selected title without surfacing a vendor launcher

## 1.0.20 — August 10, 2026

**Exact game updates · honest achievements · Launcher-only focus.**

- Removed the then-experimental Exo account, profile-sync, and cloud playtime code from that Launcher release while Steam, Epic, GOG, and Riot authentication stayed available
- Replaced Steam's global Downloads action with an exact-app request plus a hidden, fail-closed scheduled-row promotion that requires the selected app ID, manifest title, unique OCR title row, and final queued-state check to agree before it can click
- Stopped treating Steam's empty local `0 / 0` cache as proof that a game has no achievements; stale placeholders now refresh through Steam's own Community catalog while positive local progress remains preferred
- Made the Update badge a high-contrast amber marker that remains readable over light or busy cover art
- Removed the redundant Status row from game details while keeping Playtime, Achievements, and Size there and keeping playtime off dashboard cards
- Removed obsolete profile RPCs, persistence, automatic sync workers, settings, UI modules, and tests without changing store credentials or local launcher data

## 1.0.19 — August 10, 2026

**Quieter UI · reliable updates · corrected lifetime stats.**

- Reworked the Launcher profile surface for a clearer identity and showcase layout
- Reworked Settings into a readable divided two-column workspace with larger type, controls, store connections, and notification configuration
- Moved playtime off dashboard tiles and kept it in the selected game's detail view, where the extra context belongs
- Unified game and launcher update progress inside the original action button with stable, transform-driven fills and restrained state crossfades
- Rebuilt trophy notifications as one compact near-black Exo surface with matching web preview, safe achievement artwork, free positioning, and graceful reduced-motion behavior
- Fixed Steam update nudges to target the exact numeric app through a hidden client command plan and explicitly queue stalled downloads before checking progress
- Reused the cached library during startup so opening Exo and launching store-backed games no longer repeats full adapter scans unnecessarily
- Kept distinct Steam, Epic, and GOG totals additive without double-counting local fallback sessions
- Reconciled provider achievement corrections and retractions while retaining a durable one-time notification ledger, including valid supported zero-progress catalogs

## 1.0.18 — August 10, 2026

**Refined profiles and trophies · automatic sync · quiet-play hardening.**

- Rebuilt Exo Profile into four fixed, keyboard-accessible Identity, Style, Games, and Privacy views with no scrolling cards, a compact six-slot shelf, and a cleaner live canvas
- Replaced the generic Profile titlebar button with a compact Exo identity bar showing the signed-in display name, profile picture, and connection state
- Unified trophies around one DPI-sharp Exo notification surface, improved sound cues, and a freely positionable preview instead of visual-style presets
- Removed the detail-panel trophy cabinet and manual achievement refresh; missing local coverage now refreshes automatically while unsupported sources stay explicit
- Kept an installed game's Update badge, status, and primary action consistent, including Steam manifests that report an update-required state
- Restored the last successful Riot lifetime totals from the former local sync cache as a one-way, account-scoped Exo Tracker observation so valid VALORANT and League hours do not disappear
- Preserved proven Steam ownership across a successful uninstall, including atomic recovery, so Exo can still offer the same owned game for one-click reinstall
- Kept install, update, and uninstall actions behind one serialized job gate, and made an in-app update close through the normal shutdown path so sessions and settings flush first
- Made GOG and portable-game registrations durable across restart; in-place portable removal now unregisters without deleting user files, while only catalog-proven managed copies can be removed recursively
- Upgraded Quiet Game Mode to hide unused launcher shells and request graceful exit while protecting the active store, games, services, overlays, and anti-cheat; Exo never force-terminates a busy client
- Kept the authenticated account display name when an older editable profile name is empty instead of showing the generic Exo Player fallback
- Replaced manual account sync with a native single-flight scheduler that coalesces sign-in, startup, profile/settings, library, achievement, and completed-session changes with offline backoff
- Kept Exo sign-in and last-good profile data usable when one stats endpoint is unavailable, and only advances the sync checkpoint after every playtime and achievement upload succeeds
- Made portable account settings thread-safe and revision-aware, and added a per-device three-way favorite baseline so an unfavorite made on one PC is not resurrected by another
- Hardened on-demand Legendary and gogdl installation with exact official GitHub assets, mandatory published SHA-256 digests, bounded downloads, redirect allowlists, and atomic promotion
- Fixed Riot cold starts that briefly reported an installed League game as missing, including transient product-registry/eligibility reads, slow accepted handoffs, and idempotent already-running sessions
- Made new GOG and managed portable installs stage into unique private folders, promote atomically, and roll back only paths created by that exact operation on cancel or failure
- Restored a hidden Exo notification-area icon automatically after Explorer restarts, and bounded verified in-app updater artifacts with cancellation/failure cleanup

## 1.0.17 — August 10, 2026

**Exo Profile showcase · universal trophies · full-session Quiet Game Mode.**

- Added a standalone, blurred Exo Profile studio outside Settings with a live canvas, first-party profile-picture and banner uploads, themes, frames, nameplates, effects, bio, badges, and privacy controls
- Added a six-game profile shelf with combined lifetime playtime, achievement progress, recent unlocks, per-game visibility, and ordering controls
- Added private achievement discovery and sync for existing Epic and Steam data, with explicit partial-coverage labels and no game injection or raw account identifiers
- Added native, no-focus trophy notifications with four visual presets, position, duration, sound, rarity/perfect variants, and a Settings preview
- Kept vendor windows hidden for the complete detected game session while Exo moves to the notification area; unused clients still close softly and required services remain untouched
- Fixed explicit Open actions so orphan helper processes can no longer prevent the real Steam, Epic, GOG, or Riot client from surfacing
- Extended silent sign-in sync to refresh supported installed/recent game achievements before uploading settings, combined playtime, and trophy progress

## 1.0.16 — August 10, 2026

**Exo Profile and Exo Tracker · faster launches · quieter store control.**

- Replaced the fragile Tracker.gg browser flow with an optional private Exo account, customizable ecosystem profile, portable settings sync, and first-party lifetime playtime aggregation
- Combined distinct Steam, Epic, and GOG histories; retained Exo sessions only as a fallback when no native lifetime total exists
- Scoped Steam observations and GOG owned-library caches to the active store user so shared PCs never merge or expose another user's history
- Added Epic's authenticated lifetime playtime source and restored Rocket League totals across Epic and legacy Steam history
- Fixed GOG connection and owned-library sync with token refresh, pagination, cached metadata, and immediate post-connect refresh
- Reduced launcher handoff delays and kept Steam, Epic, GOG, and Riot windows out of the way while games start
- Moved both game-start and ordinary minimize behavior to the Windows notification area
- Made uninstall direct and quiet while restricting prompt automation to the exact selected game; destructive local removal is guarded to managed game roots
- Expanded Settings into a two-column account, profile, playtime, stores, update, gameplay, and portable-game workspace
- Hardened WebView navigation, settings recovery, updater origin/digest checks, single-instance startup, session recovery, and atomic installer rollback

## 1.0.15 — August 9, 2026

**Tracker.gg sign-in + sync fixed.**

- Sign in opens your Valorant profile for **Sign In with Riot** (old `/auth/login` 404 removed)
- Sync keeps the Tracker.gg window visible (Cloudflare) and reads hours from the page/API
- No extra Riot copy on detail cards; Epic still has no Tracker.gg lifetime hours (Exo sessions only)

## 1.0.14 — August 9, 2026

**Full detail poster · Tracker.gg playtime (opt-in).**

- Detail rail: full 2:3 poster again (no max-height crop / fade)
- Settings → Tracker.gg: Riot ID, Sign in, Sync for Valorant / League

## 1.0.13 — August 9, 2026

**Steam openable again · quieter UI · pin/covers.**

- Playtime: Steam localconfig (as before); Epic/Riot/Local track the real game process through launcher handoff (not bootstrap PIDs); Riot last-played from client settings; GOG Galaxy JSON when present
- Covers: larger 172px poster tiles; search + library paint official Steam CDN posters immediately; faster warm (capsule+classic race, no cancel-on-search, mapped Steam art for Epic titles)
- Buy on Steam / Open Steam: reveal main Steam window only (no steamwebhelper duplicates)
- Covers: portrait posters only — never wide heroes / letterboxing; monogram until a real 2:3 cover exists
- Covers: Epic `catcache.bin` + Legendary metadata for tall art when GraphQL is blocked
- Settings: Open Steam / Epic / GOG / Riot; Add portable folder
- Epic install auto-fetches Legendary; Riot install uses patch API progress when available
- Search: Buy on Steam (browser) when a catalog hit is not owned
- Store hide no longer sets TOOLWINDOW (Steam was stuck unopenable); restore on stop + Settings → Open Steam
- Install progress shown once (detail rail when selected — not library + CTA + detail)
- Pin: layout motion + CoverArt no longer remounts on favorite
- Softer motion; sibling store close only when those clients are actually running
- Cover match threshold raised (fewer wrong/soft posters); Epic/GOG copy clarifies Legendary/gogdl CLI

## 1.0.12 — August 9, 2026

**League Play click + Steam scheduled updates.**

- Riot: auto-click product-page Play (UIA + relative CTA) when warm client ignores `--launch-product`
- Steam: stop treating every “Steam” window as an install dialog (false Enter accept stalled updates at 0 bytes)
- Steam update: click Downloads → **Download now** when Steam parks updates as Scheduled (overnight window)
- Steam update: keep re-nudging `steam://install` until BytesDownloaded moves

## 1.0.11 — August 9, 2026

**League launch + Steam update/shutdown.**

- Riot: warm client only opens the product page — restart Riot then cold `--launch-product`
- Riot: never hide LeagueClient; close unused stores at launch start
- Steam unused clients: official `-shutdown` / `steam://exit` (WM_CLOSE was a no-op)
- Steam update/install: keep Steam visible until bytes actually download (hide-on-click was stalling)

## 1.0.10 — August 9, 2026

**Steam update kick + close unused store clients.**

- Steam update: click Update, keep Steam visible until download starts, re-nudge stuck queues
- On launch, soft-close other store clients (never Vanguard / anti-cheat)
- Search: ignore cancelled RPC so results do not wipe
- CoverArt remount key preserves art across pin

## 1.0.3 — August 9, 2026

**Library polish, Store search, locked defaults.**

- App update: close + relaunch after quiet install
- Hide Steamworks / redistributables / tools from library
- Not-installed tiles fully grayscale; hover shows large library poster
- Store tab: live search (Steam + owned Epic/GOG) with install path picker
- Settings: remove resize / updates / redist / close-store toggles (locked on)
- Fixed window size; Local / portable removed
- Epic/GOG Sign in opens visible console (import Epic session first when possible)
- Cover seed for MECCHA CHAMELEON

## 1.0.2 — August 9, 2026

**Cover art quality + library layout.**

- Prefer Steam CDN `library_600x900_2x` posters (multi-CDN fallback)
- Title → Steam app id search (community SearchApps + storesearch + seed map)
- Larger 2:3 library cards (`minmax(168px)`); portrait detail cover
- Allowlisted Steam/GOG/Riot CDNs in CSP so art shows while cache warms
- Upgrade disk cache from low-res 1x to hi-res 2x on warm

## 1.0.1

**Distinct Exo Launcher brand mark.**

- New app icon and logo: mint launch chevron (separate from Hub).
- Title-bar logo asset; README brand mark; favicon update.

## 1.0.0 — August 7, 2026

Phase 1 public release + polish pass.

### Product

- Fixed 1400×900 AMOLED shell (WinUI 3 + React + WebView2); optional resize
- Library UI: covers (cached + monogram fallback), title, store dot, favorites, sort, recents
- Detail rail: **Play | Install | Update**, secondary actions (folder, uninstall, favorite), install speed
- Install progress in Exo (percent, speed when known, cancel)
- Shared `IStoreAdapter`: auth, library, install, update, launch, uninstall, progress, cleanup
- **Local:** portable register in place (optional copy) + direct launch
- **Epic:** Legendary CLI install/update/launch with stdout progress (GUI optional) + Sign in
- **GOG:** gogdl download/repair/launch; Galaxy optional + Sign in
- **Riot:** fixed product tiles; official RiotClientServices; hide UI; no Vanguard kill
- **Steam:** appmanifest library; playtime best-effort; minimized install/launch
- Stubs: Xbox / EA / Ubisoft / Battle.net / Amazon (honest handoff messages)
- Dependencies panel + consent installers
- Settings: store agents matrix, default install root, portable copy, update check, sort
- Opt-in GitHub release update check
- Docs: honest store matrix, architecture, vendor pin strategy
- Tests: CLI helpers, LocalAdapter fixture, bridge parity

### Fixes

- Cover art virtual host + progressive warm
- Test fixture pollution filtered from Local library
- Version pipeline aligned to 1.0.0
- Phase-2 stubs no longer report false install/launch success
