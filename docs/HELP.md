# Getting started and help

## Install and update

1. Download **ExoLauncher-Setup.exe** from the [latest release](https://github.com/UhhErix/ExoLauncher/releases/latest).
2. Run it, choose Install, then Launch Exo. First run asks for your game stores, your music service, Discord, your capture keys, Lucid and your PC's graphics settings, then offers to remove the apps Exo replaces (PC > Apps lists them first later). The installer fetches what Exo needs beside itself, the dictation model among it, so everything is ready on first run.
3. Exo finds your games. It checks for a new version each time it starts; **Settings > About** shows it, and **Install update** downloads and installs it. Your settings, library and sign-ins stay.

Exo needs **Windows 11 x64** and installs for your Windows user only. The installer isn't code-signed yet, so SmartScreen may warn the first time: choose **More info**, then **Run anyway**. Each release lists the installer's SHA-256.

To uninstall, open **Windows Settings > Apps > Installed apps**, choose **Exo Launcher**, then **Uninstall**. Your games, settings and accounts stay on your PC.

## Find anything

Press **Ctrl+K**, or the Exo mark at the top left, to reach any room, game, action, music control, or Discord DM and server. Ctrl+K brings back your last search, selected, so typing replaces it.

**Dictation:** press the mic in a search box or one of Discord's boxes and talk; a line follows your voice, and **Stop** types what you said where your cursor was. It runs on this PC; nothing you say leaves it. Settings > General > Dictation turns the mic off.

## Look and feel

**Settings > General**:
- **Appearance:** Match Windows (Light or Dark, as your Windows apps are), Light, Dark or Void (pure black, for OLED screens).
- **Material:** Liquid Glass, or Solid with nothing showing through.
- **Text size**, **Reduce motion**, and **Show tools and soundtracks** (servers, SDKs and soundtracks in the Library).

Every screen gets the same layout: on a 1440p or 4K screen Exo draws the 1080p layout larger, so it looks the same, only sharper.

## Library

- **A game is missing.** Press the round **Refresh** button next to **+**. Exo reads every store again. For Epic, GOG or Amazon, sign in under **Settings > Accounts and keys**. For a game outside any store, press **+ > Add a game** and pick its .exe, or **Find a game in a folder**.
- **Play, install and update.** Click a game to open its page, or right-click it for Play or Install and Open folder. **Continue** at the top keeps the games you're playing one click from Play. Downloads and updates show on the game's card. Downloads can pause while you play and wait for the hours you set in **Settings > Downloads**.
- **Folders.** **+ > New folder**: name it, pick a color and tick its games. A game can be in as many folders as you like: right-click it and tick or untick its folders, or pick **New folder** to start one with it. Right-click a folder to edit or delete it (its games go back to the grid). An open folder's sort menu orders its games by **Folder order** (the order you added them) or as the Library sorts. A folder is also how you keep favorites.
- **A folder's picture.** In **Edit folder**, press **Add picture** (or drop a PNG, JPEG or WebP under 15 MB on the sheet), then drag and zoom it in the cropper and press **Done**. **Remove picture** puts the games' covers back.
- **Hide a game.** Right-click it and choose **Hide**: it leaves the grid, **Continue** and its folders. The sort menu's **Show > Hidden games** lists the games you hid, each with **Unhide**; Ctrl+K still finds them.
- **Uninstall.** **Uninstall** on a game's page removes it through its store, after Exo asks you once. When the store asks you to confirm, or only its app can remove the game (Xbox), that window shows. A game you added yourself says **Remove**, and Exo leaves its files where they are.
- **Properties.** The sliders button on an installed game's page, or right-click its card: launch options, language, cloud saves, the Steam overlay, betas, updates, checking its files, **Rebuild shaders** (Call of Duty: when colors or textures look wrong after a game or driver update, the game builds its shaders again as it next starts), moving it to another drive, its DLC or optional content (for example a high-resolution texture pack) on or off, and **Artwork** to replace its cover with your own picture. Anything that downloads or deletes shows its size and asks first.
- **Optimize.** On Modern Warfare 4's page (Call of Duty), press **Optimize** and Exo sets the game's own settings for your PC and setup where the guides, benchmarks and shared configs agree: borderless on your screen's highest refresh (the mouse kept in the game with a second screen; on a G-Sync or adaptive-sync screen, frames held 3 under its top rate), your card's low-latency mode with V-Sync and frame generation off, render threads for your processor, full render resolution with your card's own upscaler and sharpening at the preset your screen suits (DLSS's faster CNN model on an RTX 20 or 30), no blur of any kind, textures and the video memory budget for your card's memory, the costly effects lowest (detail and terrain kept at Medium on a strong card), the widest field of view for your screen with aiming scaled to it, raw mouse input (no acceleration, filtering or smoothing, ADS multipliers eased in, no scroll delay), music and cutscenes muted and reduce tinnitus on so footsteps carry, MW4's headphones mix in stereo without Windows Sonic when you listen on headphones, no quality drop when you hold still, and no intro videos. The line then says **Applied**. Close the game first; Exo never changes them while it runs. Your resolution, voice and master volume, sensitivity and keys stay yours. Call of Duty no longer asks to start in safe mode, which would reset these settings: Exo's Stop lets it quit cleanly, and after a crash Exo clears the mark it leaves.
- **Steam Family.** Games your Steam Family shares show in the Library next to your own and install through Steam.
- **A store's servers are down.** When Steam can't reach its servers (or hangs on Loading User Data), Exo says so once. Press **Play offline** there, or Play on a Steam game, and Exo restarts Steam in its own offline mode after asking; it needs Steam to remember your sign-in. Exo switches Steam back online once its servers answer, and says so. GOG games start without GOG Galaxy anyway, and the Epic Games Launcher goes offline by itself. The EA app and Ubisoft Connect have **Go offline** in their own menus; Battle.net plays offline only the few games that support it, and Riot games need Riot's servers.
- **More than one account.** **Settings > Accounts and keys** lists the Steam accounts Steam remembers and the Riot accounts Exo saved: **Switch** restarts that store's client as the other account, and **Add account** opens its own sign-in. Epic, GOG and Amazon have their own Account menu.
- **Steam stays current, on its beta.** Steam downloads its own updates while it runs and installs them only as it next starts, which is when "Updating Steam" shows. Exo starts Steam again out of sight once one is waiting, while no game runs and Steam's window is closed, so the update is in before you press Play. Exo also puts Steam on its Beta Update once (Steam's own Client Beta Participation); leave it there and Exo leaves it.
- **Epic's new launcher.** On a PC with the old Epic Games Launcher, **Settings > Accounts and keys > Epic > Account > Switch to Epic's new launcher** downloads Epic's installer from Epic and opens it; it moves your games over and takes out the old launcher.
- **Quiet stores.** Steam's and Epic's own pop-ups and sounds stay off for every account on this PC, and the Steam overlay is off for every Steam game; a game's **Properties** turn its overlay on. Hours add up every Steam and Epic account on this PC.

## PC

The PC room is this PC, in five pages: Overview (its About card and Best settings), Graphics (the driver and Exo's profile, Upscalers and Shaders), Display (the screen and HDR), Performance and Apps (the uninstaller and game runtimes).
- **Driver and profile** (Graphics) shows your NVIDIA driver and NVIDIA's newest for your card, Game Ready or Studio, looked for at most once an hour. **Update** installs it clean and driver-only (on a GTX 10 series or older card, which Game Ready drivers no longer support, NVIDIA's newest driver for it), with no NVIDIA app, PhysX or HDMI audio; Exo puts your profile back after. It waits while a game runs or you're in a Discord call. On an AMD Radeon or Intel graphics, the same row shows AMD's or Intel's newest driver for your card: **Update** installs AMD Software: Adrenalin Edition with its driver (Fluid Motion Frames needs AMD Software), or Intel's driver alone, without Intel Graphics Software. Exo never restarts your PC; the row says when the new driver needs a restart. Exo's driver profile (lowest latency, with G-SYNC or without, picked for your screen) applies with one press, and **Import** takes a Profile Inspector file. On an AMD Radeon, Exo's profile (Chill, Boost, Super Resolution and Image Sharpening off, FreeSync on with a frame cap, V-Sync left to each game, FSR 4 for FSR 3.1 games, Anti-Lag while you play) applies the same way, with Undo, and a game's page can turn on Fluid Motion Frames for that game (it needs AMD Software running). On an Intel Arc or Iris Xe, Exo's profile (Endurance Gaming and sharpening off, Low latency on, Adaptive Sync on with a frame cap, Intel's prebuilt shaders) applies the same way, with Undo, and a game's page can set XeSS frame generation for that game. If the driver doesn't answer, it lists what to set in AMD's or Intel's own app.
- **HDR** (Graphics) turns HDR on only while a game with real HDR runs; each game's page can set Auto, Always or Never. **SDR looks right in HDR** draws the desktop with gamma 2.2 while HDR is on, so blacks stay black.
- **Display** shows **Best for this screen** and uses it with one press. **Each setting by hand** shows the rest: refresh rate, color format and depth (only what the screen itself takes), scaling (with **Override the scaling set by games**), color from brightness to hue, and RTX Video. A change to the picture comes back by itself after 15 seconds unless you keep it.
- **This PC** shows only what holds your PC back (RAM below its rated speed, Resizable BAR off, a power plan that slows your processor, Game Mode off, GPU scheduling off on a card with frame generation; on a PC with two graphics chips, **Graphics for games** asks Windows to run your games on the fast one), leads with **Use best settings** when something is left to set (Exo's power plan for your processor, the driver profile for your card, HDR for HDR games; each stays in its own section with its own Undo), and keeps your chipset driver up to date: AMD's on a Ryzen, Intel's Chipset Device Software on an Intel processor where it's installed. Exo installs a newer one while you're away (once nobody is using the PC: ten minutes without input, or one with it locked, and nothing playing, no call, no other program at work, no game, the PC plugged in), or now with **Update**. It never restarts your PC; the row says when a restart finishes the update. **Settings > Downloads > Keep the AMD chipset driver up to date** (or Intel's) turns it off.

## Upscalers and Shaders

- **Upscalers** (PC › Graphics; a game's page shows where it stands and opens it) update DLSS, FSR and XeSS files a game already has. Close the game first. **Restore**, in the game's Properties, puts the game's own files back. A game with anti-cheat changes only after you allow it for that game, at your own risk; Riot games are never changed. DLSS presets are part of Exo's driver profile, on an NVIDIA graphics card.
- **Shaders** (PC > Graphics > Shaders) precompiles games' shaders. **Refresh** finds what to compile and **Compile** works through the queue; press a game to add it or take it out, and drag to change the order. A game's Shaders row opens it here. Nothing runs until you press one of them, and it stops soon after: after a graphics driver update, **Refresh** shows the games to compile again. It skips a game Windows already compiled for your card.

## Store

Browse, search and wishlist games. **Get on Steam** and the other buttons open the store's own app (or its page when the app isn't installed) to buy. Prices are in your Windows region's currency, and a game's system requirements are checked against your PC's parts. Wishlisted games on sale show on the Store tab.

## Discord

The Discord room is **Exo's own Discord client**; there is no Discord app to install. Open the Discord room, then on your phone open Discord, go to **You > Scan QR code** and scan the code; confirm on the phone. Exo never sees your password.

- **Calls.** Click a voice channel to join it, or the call button at the top of a DM; mute, deafen and the call sit in the bar at the foot of the channel list. Share Your Screen in a call opens Exo's own picker (an app or a screen, with its sound), and Turn On Camera shares your camera.
- **Servers.** Drag a server up or down the list, or onto another to make a folder; Discord's own apps show the same order. A discord.gg or discord.com/invite link joins the server right here.
- **Menus.** Right-click a person for their menu (with Timeout, Kick, Ban and Roles where you have the permission), a message for its menu, or a picture to copy or save it. **Report** on someone else's message, on a person, on a server you don't own or on an event asks Discord's own questions step by step, then offers Block and Ignore. Right-click a server's icon for everything Discord's menu has there (Server settings straight to a page, Create, Move to, Copy server info), and a server folder for its name and color. Right-click a thread or forum post (in the sidebar, the Threads list or a forum) for Mute, Notification settings, Join or Follow, and for its owner or a moderator Edit tags, Pin, Close and Lock; the threads you're in show under their channel. Right-click an emoji in the picker to add it to your favorites.
- **Settings.** The gear in your bar opens Discord's own User Settings: Profiles, Account, Data & Privacy, Messaging Permissions, Notifications, Voice & Video (noise suppression, echo cancellation, Push to Talk, sensitivity, attenuation, camera), Appearance, Accessibility, System (keybinds), Activity Privacy, Connected Apps, Developer and **Log Out**. Settings that belong to your Discord account change it everywhere you use Discord. Exo keeps Discord's data and privacy switches off for you (data to improve and personalize Discord, sponsored content, your messages on web pages, profile-update pings, Share my activity, messages from friends in games), and your account's language and time format follow your PC's, so those have no switches.

Notifications follow your Discord notification settings and go away when you read the chat, here or in any Discord app. Discord's terms don't allow other clients, so use it at your own risk.

## Music

Pick Spotify, Apple Music or YouTube Music in **Settings > Accounts and keys > Music**, then press **Sign in** in the Music room and sign in on the service's own page. Spotify needs its desktop app, which Exo can install for you, and Spotify Premium. **Songs**, under Home, lists every song you saved. A Spotify link someone sends you in Discord opens here, and pasting a Jam link into **Jam** joins it. The mini player in the top bar controls playback from every room.

If the Music room stays empty or signed out, check that the service works in your browser, then sign in again from the Music room.

## Captures

The Capture button in the top bar, or its keys (set in **Capture settings**, in the Capture menu and in **Captures**), saves an instant replay, starts or stops a recording, or takes a snip. **Captures** lists them by game, with trim, favorites, copies sized for Discord or vertical video, and **Send to Discord**, which copies the file and opens the Discord room for you to paste it. While you aren't playing (and a laptop is plugged in), Exo re-saves your recordings at about a third of the size with the audio untouched, one at a time, biggest first, after checking each new file looks the same as the original; the original goes to the Recycle Bin only then. **Capture settings > Make recordings smaller** turns it off, and **Make smaller** on a recording does that one now.

**Notes** in Captures (or **New note** and **Open notes** in the Capture menu, or Ctrl+K) holds your notes: each is a plain text or Markdown file in `Documents\Exo Notes` (your user folder's `Exo Notes` when Documents is kept in OneDrive), saved as you type and named for its first line. The notes you had open come back as tabs. **Open** (Ctrl+O) edits any text file where it is; it is written only when you press **Save** (Ctrl+S), and its edits wait on this PC until then, after a restart too. Ctrl+F finds and Ctrl+H replaces, a right-click on an underlined word gives Windows' spelling suggestions, and **Text** or **Markdown** sets a note's kind; a Markdown note has a formatting bar and **Preview**, where a checklist's boxes tick. To open .txt, .md and .log files with Exo, **More > Open text files with Exo** adds Exo to Windows' Open with and Default apps and opens Default apps, where you pick **Exo Notes**; Exo never changes your defaults itself. **More > Stop opening text files with Exo** takes it back.

## Devices

**Devices** shows your mouse, keyboard, what you listen on and your controller, each with its picture; press one to open its page. On your headphones' page, **Pick** the pair you wear (Windows can't tell which pair is on the jack) and set up Lucid, Game Mode and Night Shift. Keyboards are marked Coming soon.

For your controller, open its card. **Test** checks its sticks, **Measure** its polling rate. On a DualSense or DualSense Edge connected with a cable, **Calibrate** runs its own center and range routine (leave the sticks alone, then roll both all the way round); **Undo** puts back what it had; an Edge gets Exo's calibration back each time it connects. **Lights and sound** sets its light bar, player light, microphone, speaker and vibration, and **Game profiles** change them for the games you link. Exo always turns Steam Input off for every Steam game that supports a PlayStation controller (**Games read it themselves** says how many); a game that reads only Xbox controllers keeps it. A Steam game's **Properties** set it per game under **Controller**. Exo never changes a controller while a game runs.

For your mouse, plug in a supported mouse or its receiver and open its card. Only the settings your mouse has are shown; every change is written to the mouse and read back. Save a profile, link it to games, and it switches on by itself when one starts. If the mouse isn't found, wake it, check its battery and connection, then open Devices again.

## Your data

Everything Exo keeps is in `%LocalAppData%\ExoLauncher`, your captures in your Videos folder and your notes in `Documents\Exo Notes`. Exo has no account, ads, analytics or telemetry. The [privacy policy](../PRIVACY.md) lists every network request.

## Report a problem

Check [existing issues](https://github.com/UhhErix/ExoLauncher/issues) first, then [open a report](https://github.com/UhhErix/ExoLauncher/issues/new/choose). Include your Exo version (Settings > About) and, if you can, the log at `%LocalAppData%\ExoLauncher\logs\app.log`. Exo removes passwords, keys and account ids from it, but read it before you post.
