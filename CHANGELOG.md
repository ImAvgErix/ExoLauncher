# Changelog

## 3.15.0 — October 23, 2026

Exo's Discord grows into the whole of Discord's: every page of Server Settings, group DMs and video calls in DMs, bookmarks and reminders, message requests, quick reactions, link previews that play and much more. Shaders tells ready games apart and is honest about compile times, snips start at once, and the Devices room thanks snekxs, who made OpenMouse.

- **Snip starts at once.** Snip in the Capture menu waited for the menu to fade before freezing the screen; the menu now goes at once, and the snip overlay shows in about 65 ms instead of 240. _Verified: end-to-end._
- **The snip editor stays light.** It drew every snip at full size, a snip across several screens taking hundreds of MB in Exo's page; it now draws at the size it is shown (Save still keeps every pixel) and lets go of the picture as it closes. Five snips in a row leave Exo's page where it was. _Verified: end-to-end._
- **When Windows runs out of memory, the log says who took it.** Exo's page once restarted after a snip because Windows itself had run out of memory (Windows said so too); the snip was not the cause. If the page ever runs out of memory again, app.log records how full Windows' memory was and the programs holding the most. _Checked in code only._
- **A message's time fits beside it.** Pointing at a message in a run shows its time in the avatar's column, as Discord does, a clear gap before the words and never cut off, at every text and chat size. _Verified: on screen._
- **Channel topics, as Discord shows them.** A channel's topic in its header shows its mentions, emoji and links instead of codes like <#2114…>, and pressing it (anywhere but a mention or link) opens Channel topic with all of it. _Verified: on screen._
- **A Server Guide resource opens beside the guide with Discord's side header:** the channel's name, its notifications and Close, instead of a copy of the channel's whole header that squeezed the name to one letter. A call's chat beside it shows the channel's own icon. _Verified: on screen._
- **Forums load lighter.** A forum's list asks Discord for each post's picture at the size it shows instead of the full photo (often 4K and several MB each), and a post's first line shows its emoji and mentions, with the author's picture beside their name instead of above it. _Verified: on screen._
- **The Server Guide's welcome names you** where the server's message says [@username], as Discord's does. _Verified: on screen._
- **Mark Unread brings back New messages.** After Mark Unread on a message, the bar counting what's new since then shows again while that message is above you, until you mark it read, as in Discord. _Verified: on screen._
- **Shaders tells ready games apart.** Teamfight Tactics read Not supported though Exo Shaders compiles it (all but a few encrypted files); it reads Ready · some files encrypted now, and only games whose shaders stay locked are left out. A game says what it waits for: Marvel Rivals says Start it once from Steam (its launcher clears the shader cache at a new season) instead of asking for a recording its anti-cheat would block, and a game whose ray tracing alone needs a recording reads ready, its poster saying Compile without ray tracing (partial); that last case is checked in code only. _Verified: on screen._
- **Honest compile times.** Shaders' times come from your PC's own compiles of each game, with reading and preparing its files counted. Until a game has compiled on your graphics driver its time is a range and the queue says rough estimate instead of promising a finish time, where it said 5 min for Counter-Strike 2 (its one cold compile here took 1 h 22 min) and 10 min for Beast of Reincarnation (about 1 h 40 min). A quick recompile of a game whose cache still held it no longer counts as how long it takes: VALORANT read under a minute, where a compile from scratch takes about 4. _Verified: on screen._
- **Access, as Discord's.** Server Settings › Access picks how people join: Invite only (with the rules members agree to), Apply to join (an application of up to five questions, short answer, paragraph, multiple choice or your rules, with Discord's examples and Apply from profile; leaving it asks to approve or reject the applications waiting) or Discoverable (Discord's checklist while the server doesn't meet it, then its description, category, tags, emoji information and language). Age-restricted server is there too. Seen on a real server; saving was tried only in the dev mock. _Checked in code only._
- **Server Insights.** Server Settings › Server insights shows this week's visitors, communicators, new members and new member retention against last week's, as Discord's page does, and opens the full insights on Discord's website; a server under 500 members gets Discord's note instead. The note was seen on a real server, the numbers only in the dev mock. _Checked in code only._
- **Server tags in Discord's art.** Server Settings › Server tag shows each of Discord's badges as Discord draws it, in the colors you pick, with the badge before the tag's name. Exo reads the art from Discord's own web client when you open the page; nothing of it ships with Exo, and if Discord's client changes shape the picker lists the badges by name. A server without Server Tags can try badges and colors, as in Discord, but can't set a name. _Verified: on screen._
- **Linked roles.** A role's Links tab, as Discord's: the accounts members must connect to get the role (Steam, X, Reddit, eBay and the rest, or the server's apps that link roles) and, where Discord reads them, how much of each, such as an account at least 30 days old or 10 Steam games, with all of them or any one needed. As in Discord, requirements start only on a role with no members. Seen on a real server; saving was tried only in the dev mock. _Checked in code only._
- **Onboarding, as Discord's.** Server Settings › Onboarding turns it on or off with Discord's requirement shown, and Advanced server setup, and edits each part: the default channels new members start with (whole categories too), the questions asked before or after they join (up to 4 before, 15 in all, each answer giving channels and roles, with Discord's checks), and the Server Guide's welcome sign, 3 to 5 tasks and resource pages. Seen on a real server; saving was tried only in the dev mock. _Checked in code only._
- **Quick reactions.** A message's hover bar starts with the three reactions you use most, and its menu with a row of four, as in Discord: a press reacts, and a second takes it back. Only emoji you can use there are offered, with Discord's own picks while you've used few. _Verified: end-to-end._
- **Who's who in voice and the member list.** People in a voice channel show LIVE while they stream, a camera while it's on, and their mute or deafen mark, the server's own in red and yours from their menu apart, as Discord shows them; a channel with a user limit shows how full it is (2/10). The member list shows the server owner's crown and a booster's gem, and the picture someone set for that server. The member list's marks were seen on a real server, the voice marks only in the dev mock. _Checked in code only._
- **Custom status.** Discord settings › Account sets your custom status as Discord's Set your status does: an emoji, up to 128 characters, and when it clears (24 hours, 4 hours, 1 hour, 30 minutes or never), with Clear status; it shows under your name in the panel. Exo now always tells Discord your custom status while it's your active app, so using Exo no longer hides one you set elsewhere. Saving was tried only in the dev mock, to leave the account as it was. _Checked in code only._
- **Server pictures and voice channel statuses.** Messages show the picture someone set for that server, as Discord does, instead of their own. A voice channel's status (what the call is about) shows under its name, with its emoji; in the channel you can set it (Set channel status, 500 characters) and a channel manager can clear it, as in Discord. Server pictures were seen on real servers, statuses only in the dev mock. _Checked in code only._
- **Message requests.** DMs from people you aren't friends with, and the ones Discord files as spam, wait under Message requests (with a count) instead of among your DMs, as in Discord: each shows its latest message with Accept and Ignore, and opened, a request shows Discord's Accept the request line with Ignore and Accept DM where the message box goes. Seen only in the dev mock: the test account has no requests. _Checked in code only._
- **Calls know when they're recorded.** While Exo Capture keeps a replay or records, Exo tells your calls it records them, as Discord's clips do, so everyone in the call is told their voice may be recorded; and when someone else in a call has clips on, the voice panel says so. A voice channel's status line no longer reshapes the voice panel. _Checked in code only; needs a call with a second person._
- **Publish, Remove embeds, Remove all reactions.** A message's menu has Discord's Publish in an announcement channel (to every server following it), Remove embeds on your message (or anyone's for a moderator), asked first unless you hold Shift, and Remove all reactions for moderators. Remove embeds is covered end-to-end, Publish and Remove all reactions only in code. _Checked in code only._
- **@silent messages.** Start a message with @silent and it goes without notifying anyone, as in Discord, the word left out. A silent message shows Discord's crossed-out bell after its time and starts its own group, and a forward's note starting with @silent sends the forward silently too. _Verified: end-to-end._
- **Modify attachment.** A file waiting to be sent has Modify beside Mark as spoiler, as in Discord: rename it, give a picture or video a description (alt text, read out to people using screen readers), or mark it as a spoiler. _Verified: end-to-end._
- **Previews of your own pictures show.** A picture added to a message had an empty preview, and so did a new avatar or banner before you saved it: Exo's page blocked pictures made on your PC. They show now. _Verified: on screen._
- **Long messages.** Near Discord's limit (2,000 characters, 4,000 with Nitro) the message box counts down what's left, in red past it, and sending past it offers Upload as file, which sends the message as message.txt; the + menu's Upload your message as a file does the same. A paste longer than a message can be goes in as message.txt, as in Discord, where Exo used to cut it off at 4,000. _Verified: end-to-end._
- **#channels, @roles, @everyone and @here in the message box.** Typing # suggests the server's channels you can see, with their icon and category, and @ now suggests the server's roles in their colors and Discord's @everyone and @here after people; each goes into the message as Discord sends it. _Verified: on screen._
- **The image viewer goes through a message's pictures.** Opening one of several pictures in a message shows Previous and Next (the arrow keys too) and which one it is, and the viewer has Discord's Zoom (or a click on the picture) to see it at its own size, Save image, Open in browser and Close. _Verified: end-to-end._
- **Video calls in DMs.** A DM's header has Discord's Start Video Call beside Start Voice Call: the call rings as usual and your camera comes on as it connects. An incoming call has Join with video too. _Verified: on screen._
- **Ignore & Block.** Discord settings › Messaging permissions lists the people you blocked and ignored, as Discord's Ignore & Block does, each with Unblock or Unignore. _Verified: on screen._
- **Invite friends, as Discord does.** Invite people (the server's header, its menu, a channel's or a server's right-click) opens Discord's Invite friends: your friends with Invite, which sends them the link, the link with Copy, and Edit invite link to choose how long it lasts, how many uses and temporary membership. It used to copy a 7-day link and nothing else. _Verified: end-to-end._
- **The server menu, as Discord's.** It has Create channel, Create category and Create event for those who may, Notification settings (as on a server's right-click), and Hide muted channels, which leaves muted channels out of the list, and a category with nothing left under it, as in Discord. _Verified: on screen._
- **Link previews that play.** A YouTube, Twitch, Vimeo, TikTok or SoundCloud link's preview has Play, which plays the video right there, as in Discord; Spotify and Apple Music links show their own players. A preview's author and footer show their icons, and the footer says when the post is from. _Verified: end-to-end._
- **A category's menu, and New unreads.** Right-click a category for Discord's Mark as read, Collapse category, Collapse all categories, Mute category, its Notification settings, Edit category (its name and permissions) and Delete category. When unread channels are scrolled out of sight, a New unreads bar (or how many new mentions) shows at the list's top or bottom and goes to them. _Verified: on screen._
- **Group DMs, as Discord's.** Create DM picks up to nine friends (one opens your DM with them, more make a group), as Discord's Select friends does. A group DM has its member list beside it (Show member list), with the owner's crown; anyone in it can Edit group (its name and icon) from its right-click, and the owner can Remove from group or Make group owner on someone's right-click. People joining or leaving show at once. _Verified: on screen._
- **Bookmarks and reminders.** A message's menu has Bookmark message and Remind me (in 30 minutes, 1 hour or 4 hours, the next 9:00 in the morning, or Monday at 9:00), as in Discord, and the Inbox has Bookmarks and Reminders tabs, each with Jump and Remove bookmark or Mark complete. They are Discord's own, so they match Discord on your other devices, and when a reminder comes due Windows says so. _Verified: end-to-end._
- **A post's pictures together.** A link whose preview carries several pictures, such as a post on X with four, shows them in one preview in Discord's grid (two side by side, one beside two, or two by two) instead of a preview per picture, and the viewer goes through them with Previous and Next. _Verified: on screen._
- **Everything someone is doing, on their profile.** A profile shows each of their activities, not just the first: a song on Spotify with its album, how far in (ticking) and Play on Spotify, a stream with Watch, and the voice channel they're in on a server you share, with Join voice. _Verified: on screen._
- **Speak message.** A message's menu has Discord's Speak message, which reads it aloud as "Name said …", mentions, emoji and links read as words; pressed again while it speaks, it stops. _Verified: on screen._
- **Role icons and In voice.** In a server with role icons, a name in chat and in the member list shows its highest role's icon (picture or emoji), as in Discord, and someone in a voice channel shows In voice in place of what they're doing. _Verified: on screen._
- **A server's Privacy settings.** The server menu has Discord's Privacy settings: direct messages and message requests from its members, and sharing your activity status and activity joining there. _Verified: on screen._
- **Thanks to snekxs.** Under your mouse, the Devices room says Built on OpenMouse by snekxs, with a heart: Exo Mouse is built on OpenMouse, which snekxs made, and is in Exo with their permission. _Verified: on screen._
- **The readout under your mouse reaches across.** In a narrow window, the hairline over the Devices room's battery, signal and polling figures stopped short above an empty column; it runs the whole width now. _Verified: on screen._

## 3.14.1 — October 23, 2026

- **Exo Shaders on SCSKiller's newest engine.** Exo Shaders now runs SCSKiller 1.4.0-alpha.3, which carries every fix from its stable 1.3.2 too, with Exo's own changes kept. Exo sets its new options at their best by itself: games on a hard disk are read as they compile (only those drives), a game never compiled compiles while your PC is idle, and no game has a compile size limit. _Verified: on screen._
- **Built on stable releases.** Exo now runs on the stable .NET 10, Windows App SDK 2.5 and WebView2 releases instead of previews, so their preview bugs never reach your PC. _Verified: on screen._
- **Exo starts lighter and sooner.** Store, Discord's page, Music and System now load the first time you open them instead of at every start, and Music's Spotify starts with Music (or is picked up if it's already running). On this PC Exo starts with 923 MB in use instead of 1.4 GB and 6 processes instead of 13, the Library is ready in 1.6 s instead of 2.1 s, and the opening splash is shorter to match. A room opened the first time shows once it's whole, its pictures included, with its tab pulsing meanwhile (a fraction of a second; Music about two while Spotify starts), and at once after that. Discord stays connected the whole time, for calls, messages and the Bar's count. _Verified: on screen._
- **Lighter between calls.** Noise suppression's model loaded when Discord signed in and stayed until Exo closed, about 80 MB with no call. It now loads as you join a call (half a second, inside the time the call takes to connect) and is freed a minute after the call ends. _Checked in code only._
- **Select games to act on together.** Select, beside the Library's sort menu, turns a press on a cover into a pick: Shift picks every game from the last one you pressed, Ctrl+A picks them all, and the keyboard's arrows, Enter and Space work as well. A glass bar at the foot of the Library counts them and hides them (Unhide in Hidden games), adds them to a folder or a new one, or picks all or none; Done or Escape ends it. _Verified: on screen._
- **Uninstall several games at once.** When every picked game is installed and none is running, the bar has Uninstall (Remove for games added from a folder): a second press confirms, and Exo removes them one after another, as each game's own Uninstall does. _Checked in code only._
- **A folder with your picture matches the others.** Its name sat on a flat box in the folder's color over the picture; now it has the same glass front as a folder of covers, as tall, with the same edge and type, taking its color from the picture under it, and its name reads in white (Light's ink on Light) whatever the folder's color. _Checked in code only._
- **Your stream's sound is your games and music, never your voice.** Sharing a screen sent everything but Exo, so it left out what Exo itself started too: the Music room's Spotify or YouTube Music, and games launched from Exo. And an app playing your mic back to you (Voicemeeter, Sonar, Wave Link, Windows' Listen to this device) went into the stream, so friends heard you twice. Now every app that plays sound is taken on its own and mixed: games, music and browsers are in; the call, Discord, mixers and voice changers are out. _Checked in code only; needs a Go Live with music playing._
- **Exo viewers keep a stream in H.265.** Exo said it plays H.265 before Discord had signed in, so the server never heard it: each time someone on Exo started watching, the stream dropped to H.264 and started over, which is when it sat loading or didn't start. Exo now says so whenever it loads. _Checked in code only._
- **A stream that won't open tries again.** Discord at times ends a watch the moment it starts, and Try Again then worked; Exo now asks once more by itself a second later. _Checked in code only._
- **Glass stays glass when a page slides in.** Switching servers in Discord, Settings sections, server and channel settings, or Music's pages turned every glass name and button on the page into a flat dark plate that only became glass again as the slide ended, the glass you saw fade in. Glass now stays glass the whole way. _Verified: on screen._
- **A stream's controls fade cleanly.** When a watched stream's controls hid or came back, their glass drew clear for the fade and its blur popped in after; each control now fades on its own. _Checked in code only._
- **Move folders by dragging.** Drag a folder in All games onto another and it moves before or after it, with a bar in the gap showing where; a folder's menu has Move earlier and Move later. _Verified: on screen._
- **H.265 streams show whole.** A friend's H.265 stream was drawn cropped to its top-left corner, big or small: Edge's decoder called every picture 1280×720. Exo now reads each stream's real size from the stream itself, so a 1080p or 1440p stream fills its frame. _Verified: on screen._
- **Watch several streams at once.** As in Discord, Watch Stream on another person keeps the first: the newest is big above the call and the others are tiles among the people, each with its own sound. Click one to make it big; Stop Watching on each. _Checked in code only._
- **Your stream's tile fits when it's small.** In a full call or under a watched stream, its buttons sat over LIVE and its numbers were cut off: small, it shows LIVE and its name, the buttons when you point at it, and the numbers in the Share menu. A long name on any small tile ends in "…". _Checked in code only._

## 3.14.0 — October 23, 2026

- **Friends can watch your Go Live, smoothly.** Viewers on Discord's app saw your stream load forever: Discord's server threw every frame away, because Exo labeled its video packets differently from Discord's own. They match now, and the stream plays smoothly: it starts at 8 Mbit/s and follows what Discord's server says it can take (never more than its limit, down at once when packets go missing, slowly back up), packets go out evenly instead of in bursts, a lost one is sent again once, and a whole picture goes out when someone starts watching or loses one, not every five seconds. Your own preview plays live while Exo is on screen, even with a game in front on another screen. _Verified: on screen._
- **Go Live in H.265.** When everyone watching can play it, as Discord's app decides, your stream goes out in H.265, sharper at the same bit rate, and changes to H.264 by itself if someone joins who can't. Friends' H.265 streams and cameras play in Exo too where your graphics card decodes it. _Checked in code only._
- **The call's controls are glass.** The call's bar, a stream's buttons and name, the names on people's tiles, New messages, an incoming call, unsaved changes and Discord's notices are liquid glass like the rest of Exo, and a stream's volume slider reads in Light. _Verified: on screen._
- **Hide Members, as Discord draws it.** Watching a stream, Hide Members is a down arrow and Show Members an up arrow, and with members hidden the stream ends above the call's bar instead of under it. _Verified: on screen._
- **Lighter in a call with clips on.** With instant replay or a recording running during a call, Exo cleaned your microphone twice, once for Discord and once for the recording; the recording now takes the call's cleaned voice. In a call with League running, Exo went from 70% of a CPU core to 32%. _Checked in code only._
- **Streams cost less to watch.** Video in calls and streams is drawn at the size it shows instead of the stream's own (a 1440p stream shown 1500 wide drew almost three times the pixels), and reaches Exo's page as raw bytes instead of text. _Verified: on screen._
- **Quicker in small ways.** The Library is usable 0.8 s sooner at startup, the music volume reads at once instead of waiting on retries, System reads your graphics driver once instead of twice, and a store rewriting its files unchanged no longer makes the Library look again. _Checked in code only._
- **A saved clip says so, out of the way.** Saving a clip or a recording shows one small line at the top middle of your screen for a moment (Clip saved · 30 s) instead of a card in the corner of your game, where a shooter's minimap or kill feed is. _Checked in code only._
- **Starting… stays solid.** While a game starts, its Play button in Continue keeps its look instead of turning see-through over the cover. _Checked in code only._
- **No stray windows from Riot Client.** Bringing a store's window back for a sign-in or a launch also showed windows the store keeps hidden: a blank "TrayIconClass" window and a "Default IME (Not Responding)" one. Exo brings back only what it hid and the store's main window. _Checked in code only._
- **What's new, when Exo updates.** The first time Exo opens after an update, a sheet says what's new in that version, each change in a line on what it does for you, then Continue (Escape or a press outside closes it too). It shows once per version, never on a new install, and never over setup, a game's page or another dialog. Settings › About › What's new opens it again. _Checked in code only._
- **Your voice gets through, clear and on time, without your fan or desk.** Noise suppression ran at full strength, which took words out with the noise: your voice sounded robotic, and the gate, judging what was left, held whole words shut. It now takes noise down by about 40 dB (a fan nearly gone) and keeps your voice, and Automatic sensitivity opens on your voice and stays shut on what suppression strips (a fan, a key). A low cut at 80 Hz takes out your desk's rumble before anything hears them, and a knock or slam on the desk no longer opens Automatic sensitivity (a voice already going out isn't cut by one) (the high-pass Exo's tips mentioned wasn't actually there). Noise Suppression for Others steps aside while any game runs, even one started outside Exo, so it never starves your microphone, and if cleaning someone's voice fails, their voice keeps playing instead of going silent for the rest of the call. _Checked in code only._
- **Noise Suppression like Discord's.** Voice & Video › Noise Suppression is one choice, as Discord's: Exo AI (the default: Exo's DeepFilterNet run the way Krisp runs its own model, your voice brought to the level the model knows and back again, so a quiet or a hot mic is cleaned as well as any), Standard (Google's WebRTC, Discord's Standard) or None. _Checked in code only._
- **Your voice starts and ends as on Discord, at its own level.** Automatic sensitivity keeps the 100 ms before a word and holds 500 ms after it, as Discord's does, and the last moment fades out instead of ending on a click. Automatic Gain Control is gone: your voice goes out at its own level, as TeamSpeak sends it, and if your mic sends nothing for 15 s in a call the voice panel says so. Recordings get your voice whole. Exo's call audio gets Windows' real-time audio scheduling, so a game using all of your CPU can't make your voice or theirs stutter. _Checked in code only._
- **Recordings without your fan.** Before your first word and between sentences a recording carried your fan (and turned it up); now pauses sit 30 dB down behind a gentle gate that looks 100 ms ahead, so every word is whole from its first sound. Captures also open at half volume in the player. _Checked in code only._
- **Everyone at one volume.** Voice & Video › Even Out Voices (on by default) brings each person in a call to about the same loudness, so one friend's hot mic isn't louder than the rest; your volume for each person still applies on top. _Checked in code only._
- **Only My Voice.** Exo can learn your voice from a Voice Check (Use as My Voice) and, with Only My Voice on, hold back voices that aren't yours in calls: someone else in the room, a TV or a stream on your speakers. It listens for about a second before it decides, never holds back on one uncertain moment, and lets your voice straight back out. The voiceprint stays on your PC, encrypted, and Forget deletes it. _Checked in code only._
- **DLSS settings checked in the game.** While a game runs, Exo asks the driver whether its DLSS override actually took (the preset, the mode, frame generation) and notes it in its log. _Checked in code only._
- **Discord's checkout page never gets Exo's access.** A copy of the page that holds Stripe's card field was also served from Exo's own address; messages from it are refused now, as Exo Mouse's are (from a security audit). _Verified: end-to-end._

- **Discord stays fast after you switch windows.** Every time Exo left the front it throttled and trimmed itself at once, and Discord then took 4 to 10 seconds to load messages and profiles (and seconds before your voice went out in a call). Exo now waits a minute out of focus before stepping back (10 seconds when minimized), and a quietly dead connection to Discord is noticed within seconds instead of holding everything for 30. _Verified: end-to-end._
- **Every channel you can see opens.** A channel without Read Message History (rules, welcome) was grayed out and couldn't be pressed; it opens now, as in Discord, showing what arrives while it's open. _Verified: on screen._
- **Chats open at the newest message.** A conversation you had read could open a little above the bottom and stay there. _Verified: on screen._
- **Profiles open at once, smoothly.** A profile card settles in on a spring with what is known, and grows to its full size when the rest arrives instead of jumping; hovering someone reads their profile ahead. _Verified: on screen._
- **Voice channels show a speaker,** as in Discord, instead of a headset. _Verified: on screen._
- **System, as you had it.** Display is back to its full set of settings; Exo's profile says Applied on its line, beside its sync and Undo; Shaders has Refresh again. _Verified: on screen._

- **Games move to the middle of the screen.** Opening a game from the Library, its cover flies to the middle and its place in the grid stays empty until it flies back on close; a Continue banner opens out into the whole page and shrinks back into its place. _Verified: on screen._
- **Gifts in your Library.** Games someone gave you on Steam or Epic show in the Library under Continue, each with its art, who sent it and their message: Accept adds the game to your library, and it shows up there a moment later; Decline asks once, since the sender gets a refund. Exo reads each store's own gifts page in the background. Steam's website needs one sign-in, with Sign in on Steam's row in Settings › Game stores or in setup (Exo never holds the Steam app's sign-in); Epic needs nothing more. _Checked in code only._
- **Folders look like folders.** A Library folder is exactly a cover's size, its name and how many games it holds on its glass front, without the tab that stuck out behind it. It opens out of its place, which stays empty, and when it closes back in its front swings shut and the covers drop back in. _Verified: on screen._
- **Sort a folder your way.** An open folder has its own sort menu beside its name: Folder order (the order you put games in, as before), or Name, Installed first, Last launched, Time played, Size and Store, as the Library has. Each folder keeps its choice, and the covers on its card follow it. _Checked in code only._
- **A game in more than one folder.** A game's menu lists every folder under Folders, with a check on each one it's in; pick one to put it in or take it out, and its other folders keep it. Editing a folder can tick games that are in other folders too (each says which). A game in a folder still shows only inside its folders, not loose in the grid. _Checked in code only._
- **Your own picture on a folder.** Edit folder › Add picture (or drop a picture on the sheet) takes a PNG, JPEG or WebP under 15 MB and opens it in a cropper the folder's exact shape, with the folder's glass and name where they'll sit: drag to move it, scroll or use the slider to zoom, or use the arrow keys and + and −. The folder's card then shows your picture with its name and count on a shorter glass front; Remove picture goes back to the games' covers. Exo checks the picture itself, writes it again as its own JPEG in its cover cache, and deletes it with its folder or when you replace or remove it. _Checked in code only._
- **Hide games.** Right-click a game and choose Hide to keep it out of the Library: the grid, Continue, Friends are playing and its folders. The Library's sort menu has a Show section, All games or Hidden games, which lists only the games you hid (the heading says Hidden games), each with Unhide. Ctrl+K still finds a hidden game, so you can open and play it. _Checked in code only._
- **A press outside closes it, the first time.** Every sheet, dialog, popover and menu (a game's page, the Store's, Settings, Properties, a folder, What's New, Ctrl+K, Discord's settings, dialogs, profiles and menus) closes on the first press anywhere outside it, the dimmed space, the edges and the Bar over it included, and that press doesn't also open what was under it. With a menu open over a page, one press closes the menu and the next the page; a press inside never closes it, nor does selecting text that ends outside. Some Discord menus needed two or three presses and a few never closed; they close now. _Checked in code only._

- **Exo's profile on Radeon.** System › Driver and profile now tunes an AMD Radeon in one press, as it does a GeForce, through AMD's own driver settings: Radeon Chill, Boost, Super Resolution and Enhanced Sync off, FreeSync on for every screen that has it, and with FreeSync the frame rate target just under your refresh rate (the same cap as Exo's G-SYNC profile, and it follows a new refresh rate). Every value it replaces is saved first, so Undo puts back exactly what you had; a driver update that moves a setting shows with Re-apply, and Exo applies it again by itself. Fluid Motion Frames stays yours. If AMD's driver doesn't answer, the page lists what to set in AMD Software, as before. _Checked in code only; needs a Radeon._
- **Anti-Lag while you play.** On a Radeon, Exo's profile turns Anti-Lag on just before a game Exo launches starts, and off once no game runs: left on everywhere, AMD's driver also runs it in Windows' own apps and costs CPU. Always and Off are beside Use. _Checked in code only; needs a Radeon._
- **FSR 4 for FSR 3.1 games.** On a Radeon RX 9000 or 7000, and an RX 6000 from AMD Software 26.10.1, Exo's profile turns on the driver's FSR upgrade, so games with FSR 3.1 get FSR 4's sharper image with no change to the game. _Checked in code only; needs a Radeon._
- **Fluid Motion Frames per game.** On a Radeon, a game's page has Fluid Motion Frames: on, Exo turns AMD's frame generation on just before that game starts and off once no game runs (AMD's switch is for every game at once). It needs AMD Software running, and the page says so when it isn't. If Exo closes mid-game, it switches it back the next time it starts. _Checked in code only; needs a Radeon._
- **Your Radeon, right now.** Under Exo's profile, a line shows the GPU's temperature and hotspot, power, clock, load, VRAM in use and fan speed, every three seconds. _Checked in code only; needs a Radeon._
- **Sharper, steadier Radeon.** Exo's Radeon profile also turns Radeon Image Sharpening off (it sharpened every game on top of the game's own) and leaves V-Sync to each game (Wait for Vertical Refresh: Off, unless application specifies), so Fluid Motion Frames keeps working. On a Radeon, System › This PC asks AMD's driver whether Smart Access Memory is on before it says Resizable BAR is off. _Checked in code only; needs a Radeon._
- **Exo's profile on Intel.** System › Driver and profile now tunes an Intel Arc or Iris Xe in one press, through Intel's own driver settings: Endurance Gaming and the sharpening filter off, Low latency on (On with boost and Off are beside Use), Adaptive Sync on and, on a screen that can run it, frames capped just under your refresh rate (the same cap as Exo's G-SYNC profile, and it follows a new refresh rate), and Intel's prebuilt shaders downloaded, so games don't compile them while you play. Every value it replaces is saved first, so Undo puts back exactly what you had; a setting your driver doesn't take is left out instead of stopping the rest, and Exo applies the profile again by itself after a driver update moves it. If Intel's driver doesn't answer, the page lists what to set in Intel Graphics Software. _Checked in code only._
- **XeSS frame generation per game.** On an Intel GPU, a game's page sets XeSS frame generation for that game: the game's own choice, or 2x, 3x or 4x. Exo sets it in Intel's driver just before the game starts and puts back what you had once no game runs, even if Exo closed mid-game. _Checked in code only; needs an Intel GPU._
- **One-click driver updates on Radeon and Intel.** System › Driver and profile shows your AMD or Intel graphics driver beside the newest one for your card, as it does on a GeForce, read from AMD's and Intel's own download pages (graphics built into your processor get the processor's driver). Update asks first, downloads from AMD or Intel, checks their signature (and the checksum Intel lists), waits while a game runs or you're in a Discord call, then installs: on a Radeon, AMD Software: Adrenalin Edition with its driver, since Fluid Motion Frames needs AMD Software; on Intel, the driver alone, without Intel Graphics Software, the Driver & Support Assistant or Intel's data collection. Exo never restarts your PC; when the new driver needs a restart, the row says so. On a Radeon, Exo's profile is put back afterwards. _Checked in code only; needs a Radeon and an Intel GPU._
- **NVIDIA driver updates wait for your call.** Installing a driver ends a Discord call, so Update now waits for the call to end, as it waits for a game. Exo's profile is put back after the install again: it was skipped, read while the driver still counted as installing. _Checked in code only._
- **Interrupts in one press (MSI mode).** System › This PC › Interrupts says whether every device that can use MSI mode already does. Optimize interrupts switches a graphics card, network adapter, audio controller or USB controller still on a shared interrupt line to MSI mode, so it signals the processor on its own instead of waiting behind a busy neighbor. Windows asks permission once, and the change takes effect when you restart; Exo never restarts for you. Storage, everything between the processor and the drive Windows starts from, bridges, Sound Blaster cards and any device without a signed, running driver are left alone, and interrupt priority and affinity stay untouched (no measured gain). Each value is saved first: Undo all, or Revert on one device, puts it back. After the restart it says what switched and what Windows or a driver update put back, and Devices lists each one, its interrupt now and what Exo does to it. _Checked in code only._
- **Windows' precompiled shaders show in System.** Under Shaders, one line says whether this PC gets games' shaders precompiled from Windows (Advanced Shader Delivery) and how many games came with theirs; those are left out of Compile. Exo Shaders with this check is now the one Exo runs. _Verified: on screen._
- **System, tighter.** Game Ready or Studio sits beside the driver's Update, and G-SYNC or not beside Exo's profile. Display has one row each for refresh and color, scaling, color (its sliders in a popover beside Adjust) and RTX Video, instead of a row per setting behind Show. _Verified: on screen._
- **This PC, checked.** System has a This PC part that shows only what holds your PC back, in plain words: RAM running below its rated speed (turn on EXPO/XMP in the BIOS), Resizable BAR off, an AMD or Intel chipset driver behind its maker's newest, and Game Mode off. A power plan that holds your processor back says why: Power saver, a capped processor, boost off, Best power efficiency, or on a dual-CCD X3D or a Ryzen 9 9900X or 9950X a plan that keeps every core awake, which stops AMD keeping a game on one CCD (the V-Cache one on X3D). When all is well it says All good. _Checked in code only._
- **Exo's power plan, tuned for your processor.** Exo makes one Windows plan named Exo, a copy of Balanced (so AMD's and Intel's own tuning of Balanced carries over), with settings for your kind of processor: single- and dual-CCD Ryzen and Intel up to 11th gen get full clocks between frames, energy preference 0, aggressive boost, no core parking and no power saving on PCIe, USB, NVMe and SATA, with idle states left on so boost isn't hurt; a dual-CCD X3D, and a Ryzen 9 9900X or 9950X, whose second CCD AMD's driver also parks in games, keep Balanced's parking and minimum state so it can; Intel's 12th gen and newer keep their P-cores awake at energy preference 0, with the E-cores and Windows' Thread Director policies as Balanced and Intel have them. Idle states stay on everywhere: the gaming plans that turn them off placed last in a 39-plan Fortnite test. A laptop gets it plugged in only, and keeps Balanced on battery. On Windows' own plans Exo switches to it by itself; on your own or a third party's plan, This PC offers Use Exo's plan for your processor. Undo puts your plan back and Exo won't switch again; the settings it sets show in Control Panel's power options, and uninstalling Exo puts your plan back and deletes Exo's. _Checked in code only._
- **AMD's chipset driver, kept up to date.** On a Ryzen, when AMD puts out a newer chipset driver, Exo installs it by itself while you're away: after ten idle minutes, with no game running, no Discord call and the PC plugged in, since the install can drop USB and sound for a moment. It downloads AMD's own installer, checks AMD's signature, its version and its size, and runs it silently. If Windows needs a restart, This PC says Restart to finish the AMD chipset update; Exo never restarts your PC. Where Windows would ask for permission, it waits for you to press Update. If an install fails, the row says why and Update opens AMD's page. Settings › Downloads › Keep the AMD chipset driver up to date turns it off. _Checked in code only._
- **Intel's chipset driver, kept up to date.** On an Intel processor with Intel's Chipset Device Software installed, Exo keeps it on Intel's newest the same way: while you're away (ten idle minutes, no game, no Discord call, the PC plugged in), it downloads Intel's own installer from Intel's download page, checks Intel's signature, its version and the SHA-256 Intel lists, and runs it silently with no restart. System › This PC shows the Intel chipset driver, what's installed and what's next, with Update to install it now; Settings › Downloads › Keep the Intel chipset driver up to date turns it off. Where Intel's package isn't installed (Windows Update or your PC's maker brought the chipset's files), Exo leaves it so, and it never installs Intel Management Engine, which Intel says comes from your PC's maker. _Checked in code only._
- **A speaker on servers with a call going.** A server in Discord's rail shows a small speaker while people are in its voice channels you can see. _Verified: on screen._
- **Discord's settings open at once.** They stay ready with the room instead of being built when you open them. _Verified: on screen._
- **Lists fade into their edges.** Discord's channel, message and member lists, its server rail and Music's library fade out where they scroll on under a panel's edge, instead of being cut off by its corner. _Verified: on screen._
- **Every edge the same.** Text fields (Discord's message box and searches) have the same thin edge as panels; theirs was brighter. The mouse in the top bar shows its profile and battery without an icon. _Verified: on screen._
- **A long settings page stays in its sheet.** Changing to a long page in Discord's settings no longer draws past the sheet's bottom edge for a moment. _Verified: on screen._

- **Glass or Solid.** Settings › General › Material is now two choices. Glass is Liquid Glass even with Windows' transparency effects off; Solid has nothing showing through. Tinted is gone (a saved Tinted opens as Glass). Solid used to leave Settings and the menus opened in it see-through; they are solid now. _Verified: on screen._
- **No more glass flicker.** On a game page, in Settings, in Discord and in lists, glass buttons and panels showed a flat tint for a moment and then popped into glass as a page, room or sheet came in, because the whole thing faded in and glass can't see through a fading parent. Nothing that holds glass fades in whole anymore: rows rise in, sheets settle by size, tabs are there at once. _Verified: on screen._
- **The dim covers the whole window.** Behind Settings, a game's sheet, a folder or a Discord dialog, the top bar now dims with everything else instead of staying bright. _Verified: on screen._
- **Glass that looks like glass.** Every glass surface in Exo, Exo Mouse included, is now clear liquid glass: the colors behind it show through softly, its edges catch the light (a bright top line, a fainter bottom one, a soft glow inside), and there is no gray tint or wash across it that read as metal. Words on it keep a soft shadow and at least 4.5:1 over the brightest art, in Void, Dark and Light. _Verified: on screen._
- **Exo opens in your Appearance.** If the page's own copy of your Appearance was out of date, Exo could open in Light for a moment before switching to Void (a white flash). Exo now hands the page your saved Appearance before it draws. _Checked in code only._
- **System is one page.** Driver and profile, HDR, Display, Upscalers and Shaders fit on one screen without tabs. Display shows Best for this screen, with each setting by hand behind Show. Shaders lists only the games that still need compiling, and finds new games by itself (no Scan games button). _Verified: on screen._
- **Every Discord member is there.** A big server's member list is its full length from the start, with placeholder rows, and loads the members wherever you scroll (it used to grow only 100 at a time from the top). A late update for the list you just left no longer replaces the new one with a single row. _Verified: on screen._
- **Discord's bottom bar stays put.** Switching servers no longer slides your profile bar away and back, and the channel list ends above it instead of scrolling under it (its edges no longer doubled up, and profile effects no longer showed past it). _Verified: on screen._

- **Light, Dark and Void.** Settings › General › Appearance: Match Windows (Light or Dark, as your Windows apps are), Light (soft gray and white), Dark (charcoal) or Void (pure black, for OLED screens). Every room, menu, sheet and dialog follows it, Discord and the Devices room too, and Exo opens in it from the first frame. _Verified: on screen._
- **Words you can read on every background.** Every word in Exo was measured against what is really behind it, art and glass included, in Light, Dark and Void, and brought to at least 4.5:1: a game's sheet and a Store page sit on thicker glass, badges over cover art dim the art more, a Store banner fades under its side column, and a game without a cover shows its name brighter. _Verified: end-to-end._
- **The whole picture on big games.** Continue's cards and the Store's banners show their whole art, with the name and buttons on a small glass plate, instead of fading the art away under them. _Verified: on screen._
- **Ctrl+K brings back your last search, selected.** Typing replaces it, as in Spotlight; it used to be added to the end. _Verified: end-to-end._
- **Escape closes a game's sheet however soon you press it.** A sheet opened and closed quickly could stay open. _Verified: end-to-end._
- **Discord and Music open smoothly the first time.** Exo already loaded every room behind the opening animation, but kept them folded away, so the first visit to Discord still had to lay out its whole chat (a 109 ms hitch; later visits 30 ms). Now each room is laid out out of sight while Exo opens, and the first visit is as quick as any other. _Verified: on screen._
- **Light looks finished.** Buttons and panels keep their edge on Light's page (they had almost none), Discord's home button shows (it was white on white), styled display names in Discord read on the light page, a glass button keeps its frost under the pointer instead of going clear, and a game's stats lose a dark smudge. _Verified: on screen._
- **Devices matches the rest of Exo.** Exo Mouse draws its panels and controls in Exo's own colors in Void and Dark, and sits in the same frame as every other room, so switching rooms no longer moves everything a few pixels. _Verified: on screen._
- **More mice in Devices.** Exo Mouse now sets up 40 more SteelSeries mice: the Rival 100, 105, 106, 110, 300, 300S, 5 and 600, the Rival 3 Gen 2 and Rival 3 Wireless Gen 2, the Aerox 3 Wireless Gen 2, the Prime, Prime Wireless and Prime Mini, the Sensei 310, Sensei [RAW] and Sensei Pro / Rival Pro, and their special editions. Endgame Gear's OT mice start their session as Endgame's own app does, a Logitech mouse with an older onboard profile format saves its buttons instead of refusing over lift-off, and a mouse whose connection died while the PC slept is opened again by itself. A Lamzu that stores X and Y per DPI stage shows both, each set without moving the other, with Separate X and Y beside them. _Checked in code only._
- **Tidier rooms.** Music's home tiles fill their row, its covers line up with their titles, and search shows the top result beside the first songs. System's panels end level, with Check again on its title line, and Display's two columns come out even. The Store's Free on Epic shelf fills its row and drops two arrows that did nothing, and the banner's button matches Continue's Play. Discord's Account puts your card beside your details, Active Now no longer repeats a game's name and time, Add Friend lines up with the tabs beside it, and a profile opens at once with what Exo already knows. _Verified: on screen._
- **Small things that were off.** A menu opened with the mouse no longer lights its first row. A new folder's picked games show their check, and its search shows its magnifier. A game's store is a label, not a button that did nothing. Ctrl+K and the Store say "Nothing matches" the same way, in the middle, with Clear search. Devices › Profiles has real Apply, Games, Rename and Delete buttons (Delete in red) instead of gray words, and the profile on the mouse no longer shows a dead Apply. A playlist, album or artist page has a title as large as a game's, and every title plate over art has the same corners. _Verified: on screen._
- **The same feel everywhere.** Discord's buttons give way when pressed like every other button, a few transitions that ran on their own timings take Exo's, and pressing the tab you're already on no longer slides the page over itself. _Verified: on screen._
- **Captures have your call in them.** With Exo's own Discord client, Exo Capture looked for the Discord app to record a call's voices, found nothing, and a game's capture had the game and your mic but not your friends. The voice track now follows Exo's own client when the Discord app isn't running. _Checked in code only._
- **Downloads checked for revoked signatures.** NVIDIA's driver, Exo's runtimes and upscaler files were checked for a valid signature from the right publisher, but not whether that publisher had since revoked the certificate (as after a leaked key). Now they are; with no connection to check, the signature alone decides, as before. _Verified: end-to-end._
- **Menus and sheets read over bright art.** Large glass (menus, Ctrl+K, popovers, sheets) now dims what is behind it as small glass does, so its gray words keep 4.5:1 even over white art (they fell to 2.6:1 on Dark). _Verified: on screen._
- **YouTube Music's ad blocker keeps working.** It is now uBlock Origin Lite. uBlock Origin is an older kind of extension that Edge's engine, which Exo's music players run on, is retiring this year, so it would have stopped blocking. _Checked in code only._
- **Continue's titles stay dark glass.** Over bright art (VALORANT's) a title and its More turned white; they now stay the same dark glass on every card, as do a sheet's close buttons. _Verified: on screen._
- **Changing Appearance fades.** Switching between Light, Dark and Void (or Windows switching for Match Windows) crossfades the whole window over a third of a second instead of snapping. _Verified: on screen._
- **Rooms come in in one color.** As a room or page changed, its panels drew the page's own color and turned into glass only when the move ended, a visible second step on every room. They are glass the whole way now. _Verified: on screen._
- **Dark stays Dark behind Settings and sheets.** The dimming behind Settings, a game's sheet and Discord's settings took Dark's charcoal page down to Void's black; on Dark it now dims half as much, as on Light. _Verified: on screen._
- **Glass never turns white.** The Bar and Music's player no longer turn into white glass over bright art; they stay the same glass everywhere. Labels over videos and pictures (a call's names, Watch, a GIF's star) keep white words in Light too. _Verified: on screen._
- **Smoother room changes.** Changing rooms worked out the style of the whole page twice, as the move began and as it ended (15 to 30 ms each, a dropped frame or two on a fast screen). Now only what moves is touched. _Verified: end-to-end._
- **New glass.** Exo's glass is now Glassy UI's: edges lit from the top left with a softer light opposite, a sheen at the top, frosted menus and sheets, and a soft two-layer shadow. _Verified: on screen._
- **Folders are glass.** The front of a Library folder is Liquid Glass tinted in its color, bending the covers behind it, drawn at your screen's own resolution while it tips open. _Verified: on screen._
- **Exo Shaders skips games Windows already compiled.** On a PC that gets precompiled shaders from Windows (Advanced Shader Delivery: Windows 11 24H2, Gaming Services, and on NVIDIA an RTX card with driver 621.01 or newer), a game installed with its shaders already built for your card shows Precompiled and isn't compiled again. Exo Shaders' Cache page says whether your PC takes them, or the one thing it still needs. _Checked in code only._
- **Threads look like Discord's.** The thread mark in a channel's header, a message's thread, Create Thread and the Inbox is now Discord's own (from its web client), not a reply arrow. _Checked in code only._
- **Server invites show as Discord's invite card.** A discord.gg or discord.com/invite link in a message gets Discord's card under it: the server's icon, name and verified badge, how many are online and in it, and Join, or Joined when you're already in, which opens it. An expired or unknown invite says Invalid Invite, and a message that is only invite links shows just the cards. _Checked in code only._
- **Discord settings open at once.** Each section read its data again every time you opened it, so Account, Voice & Video, Data & Privacy and the others showed Loading or empty controls first. A section you've seen now opens with what it showed and refreshes quietly behind it. _Checked in code only._
- **The selection glides without flicker.** In Settings, Discord's settings and every tab group, the hover light could sit under the sliding selection as it passed, the row you clicked lost its light before the selection reached it, and the chosen row could show two fills for a moment as a sheet opened. Now no other row lights while the selection moves, the row you clicked keeps its light until the selection arrives, and the chosen row only ever has the one fill. _Checked in code only._
- **Settings starts with its sections.** The small gray "Settings" label over General is gone, so the list starts level with the page's first title, as Discord's settings and Apple's System Settings do. _Checked in code only._

## 3.13.4 — October 21, 2026

This update fixes Exo on 4K and 1440p screens.

- **Exo fills big screens again.** On a 4K screen at 150% (or a 1440p one), 3.13.3 drew the page larger as intended but showed only its top-left three quarters, cut off at the right and bottom with black around it. The whole page now fills the window. _Verified: on screen._
- **New NVIDIA drivers show up the same day.** Exo looks for a newer NVIDIA driver at most once an hour instead of once a day, so a driver NVIDIA puts out today is offered today. _Verified: end-to-end._
- **Discord invite links join in Exo.** Clicking a discord.gg or discord.com/invite link in a message joins the server right in the Discord room and opens the channel it invites to, as Discord's app does, instead of opening your browser. If you're already in, it just opens it; an invite Discord refuses opens Join a Server with the reason. _Verified: on screen._

## 3.13.3 — October 21, 2026

This update makes voice calls work the same on every PC: noise suppression that never drops a word, any microphone picked up properly, and nothing cut when the PC is busy.

- **Noise suppression that keeps up on every PC.** Exo now runs DeepFilterNet directly, built by Exo from the project's own source: every 20 ms of your voice is cleaned right away, in about a millisecond. The plugin Exo used handed each frame to a thread of its own that woke up on a timer. On a busy PC, or one where Windows slows timers, it fell behind and filled the gaps with silence, so some people went through silent, choppy or garbled. Measured on a real voice over a fan, 94 to 100% of the words now go out, none cut mid-word, with the voice's clarity kept. Each voice cleaned, yours and each one you hear, has its own copy of the model. _Checked in code only._
- **The gate hears what noise suppression hears.** DeepFilterNet judges how much of each moment is voice, and that judgment now opens the automatic gate, as Discord's does with Krisp. The quiet start and end of a word go out too. _Checked in code only._
- **Your first words are cleaned too.** The model loads when Discord starts in Exo, not when a call begins, so the first two seconds of a call no longer go out uncleaned. _Checked in code only._
- **Windows' noise suppression stands in.** If Exo's isn't ready yet, Windows' own cleans your voice instead of nothing. _Checked in code only._
- **Your default microphone, as Discord picks it.** Default means the microphone and speakers chosen in Windows' Sound settings, as in Discord. Exo used Windows' communications device, which on some PCs is a webcam's mic or a monitor's speakers. _Checked in code only._
- **Bluetooth and USB mics with a deep buffer go through.** Exo drops sound only when it's 40 ms later than that microphone usually is. Every packet from a mic that always runs over 40 ms behind used to be dropped, so nothing went out. _Checked in code only._
- **A microphone that comes back keeps the call.** When a mic or headset goes away mid-call, Exo keeps looking for one for as long as the call lasts. It used to stop after 10 seconds. _Checked in code only._
- **NVIDIA Broadcast and AMD mics are cleaned too.** Exo stepped its noise suppression aside for any mic named like NVIDIA Broadcast's or AMD's, even with that app's own cleaning off, and then nothing cleaned the voice. _Checked in code only._
- **Spotify's own window stays out of sight.** Spotify's window could open on your screen while Exo started it: Exo asked it a second time to show when it was slow, and Spotify answered by raising its window. A start that handed off to a Spotify already running wasn't hidden either. Now every window Spotify opens is hidden until Exo holds it, and Spotify stays hidden if hosting it fails, until it can be hosted. _Verified: on screen._
- **Display offers only what your screen takes.** Refresh rates and color depths in System > Display now come from what the screen itself says it takes, not only from Windows' and the graphics driver's lists: a 60 Hz screen no longer shows 144 Hz, and an 8-bit screen no longer shows 12-bit. On HDMI, a color that doesn't fit the cable's bandwidth at the current mode isn't offered either. _Verified: on screen._
- **SDR looks right in HDR, every time.** When HDR comes on, the screen can take a few seconds to settle, and Exo's gamma 2.2 for the desktop could miss it and never come on, leaving everything washed out. Exo now tries again for about 30 seconds, and if Windows still refuses, app.log says why. An SDR screen with Auto color management is no longer taken for an HDR one on Windows 10 and 11 before 24H2. _Checked in code only._
- **HDR goes back off after a crash.** If Exo closed while it had HDR on for a game (a crash, an update, the PC shutting down), HDR stayed on, washed out, until you turned it off in Windows. Exo now remembers, and turns it off once no HDR game runs. _Checked in code only._
- **Steam stays out of sight when Exo didn't start it.** Exo hid the windows of a store app only when Exo had started it, so a Steam that Windows started at sign-in could still pop up its web helper, update and sale windows. Now Steam, Epic, GOG, the EA app and Ubisoft Connect running in the background are kept hidden and out of the tray the same way, within 30 seconds. One you open yourself stays open. _Verified: on screen._
- **Turn off every store notification and sound at once.** Settings > Game stores > Notifications and sounds turns off Steam's and Epic's own pop-ups, toasts and sounds with one press, for every Steam account on this PC: toasts and their sound, achievement and controller toasts, friends coming online, messages and invites, taskbar flashing, the screenshot sound and new-release notices, and Epic's notifications and What's New page. Exo keeps them off. Steam closes and starts again hidden to take it, unless a game is running or Steam is downloading. _Verified: on screen._
- **Folders in your Library.** Library > + > New folder: name it, pick its color and tick its games from your whole Library (search finds one fast). Create folds the sheet into the folder's card at the front of All games: a real folder in its color, the size of a cover, holding its first three covers like papers. Hover it and it opens a little, the front tipping toward you as the covers rise and fan out. It opens out of that card into a panel of its games and folds back. Right-click a game to add it to a folder, start a new one with it, or take it out; right-click a folder or press its pencil to edit it, or delete it (its games go back to the grid). A folder is also how you keep favorites. _Verified: on screen._
- **Add any game.** Library > + > Add a game picks the game's own .exe, wherever it is, and Exo plays it from there: the title comes from the game itself, and an engine's build folder (Unreal's Binaries\Win64) isn't taken for the game's. Add a game folder still finds the .exe for you. _Verified: on screen._
- **Uninstall says what it does.** A store game's page says Uninstall instead of Remove. A game added from a folder still says Remove, since Exo leaves its files where they are. _Checked in code only._
- **The same layout on every screen.** On a 1440p or 4K screen Exo fit more in, so the Library, the Store and Devices looked different from a 1080p screen. Exo now lays its page out as on a 1080p screen and draws it larger to fill a bigger one, so every screen gets the same layout, only sharper. A 1080p screen, or a smaller one, is unchanged. _Verified: on screen._
- **Go Live joins the way Discord's app does.** Your stream's connection now names its channel and says it carries a screen, and it uses the stream channel Discord gives instead of working one out. Viewers could stay on "joining" forever. app.log now records each step of a stream: whether Discord holds it paused, who watches, what viewers ask for, and how its encryption settles. _Checked in code only._
- **Voices you hear never drop on a busy PC.** When cleaning other people's voices falls more than 100 ms behind, the waiting sound plays as it came until it catches up, instead of being cut. _Checked in code only._

## 3.13.2 — October 21, 2026

This update makes your voice clear in calls with noise suppression on, lets Go Live start on any PC, shows Epic, Steam and Xbox games as they really are, and lets you arrange your Discord servers.

- **Your voice at a normal volume.** Automatic Gain Control now works before noise suppression and brings a quiet microphone up to a normal speaking level within a second or two of talking. A quiet mic could reach a call 25 dB under everyone else's voice, so noise suppression and the gate held most of it back and people couldn't hear you. Gain control used to work only after the gate, rising 1 dB a second. It never turns a voice down, and a loud word never clips. The gain it learns for your mic is where your next call starts. _Verified: on screen._
- **Go Live when your graphics card's encoder won't start.** Exo Capture tries your graphics card first, at your settings and then at lower frame rates and 1080p. If the card still won't encode, your stream now goes out on Windows' own H.264 encoder, on the CPU, at up to 720p 30, instead of not at all. _Checked in code only._
- **Epic games that aren't installed stop showing as installed.** A game Epic Games Launcher started installing but never finished, Unreal Engine, Epic's tools, and a game whose folder was left behind after it was removed no longer show up as installed games. Games show their real names instead of Epic's internal code names. _Checked in code only._
- **Remove works for Epic games.** Remove on a game Epic Games Launcher installed opens Epic's own uninstall confirmation. Exo used to say a game was removed when nothing had been removed; now it tells you when no store has the game installed. _Checked in code only._
- **Remove works with every store.** Remove kept Steam's, GOG Galaxy's, Riot Client's, Ubisoft Connect's and Battle.net's windows hidden, so a removal that asked you to confirm waited on a question you couldn't see; the store's window now shows so you can answer it. Xbox games, and any game Windows has no uninstaller for, open their store's app so you can remove them there, instead of stopping with a message. When you finish a removal in a store's app, Exo notices and updates the game's card. A game the Amazon Games app installed is removed through that app; Exo used to say it was removed when nothing had been. Exo finds a game's uninstaller even when Windows lists its name with ™ or ®, and a GOG game from GOG's offline installer is removed with its own uninstaller when GOG Galaxy isn't installed. _Checked in code only._
- **Epic updates show up.** While you're signed in to Epic in Exo, Exo compares each Epic game with the newest version Epic has, so Update shows before Epic Games Launcher starts the update itself. Epic games installed with Heroic show up too. _Checked in code only._
- **Steam games keep their Update.** A game whose update Steam had started, paused or couldn't finish showed as not installed, or not at all; it now shows Update. Update also stays when Steam is closed and Exo can't check which games you own. _Checked in code only._
- **Xbox games in the folder you chose.** Exo finds games the Xbox app installs to a folder you picked, not only in XboxGames. _Checked in code only._
- **Covers stay covers when you switch Steam accounts.** After a switch, a game whose cover wasn't ready yet showed Steam's wide banner in its tile, and kept it for as long as Exo was open, even once the cover had arrived. The cover now replaces the banner as soon as it's ready, and a tile still showing a banner tries for its cover again each time you come back to it. _Checked in code only._
- **Arrange your Discord servers.** Drag a server or folder up or down the server list and it moves there at once, with a white bar where it will land. Drop a server on another to make a folder of both, or on a folder to add it. Exo saves the order to your Discord account the way Discord does, so Discord's own apps show it too, and a change made in Discord's app shows in Exo. A folder with one server stays a folder, as in Discord. _Verified: on screen._
- **The right NVIDIA driver for GTX 10 series cards.** NVIDIA's Game Ready drivers no longer support GTX 10 and 900 series cards, older ones, or TITAN V, and Exo offered them a newer hotfix driver that wouldn't install. These cards now get NVIDIA's newest driver for them, its security update driver, and show as up to date once they have it. RTX 20 series and newer cards still get NVIDIA's newest Game Ready or hotfix driver. _Checked in code only._

## 3.13.1 — October 21, 2026

This update fixes calls with noise suppression and makes Go Live work on more graphics cards.

- **Noise suppression never silences a call.** With Noise Suppression for Others on, you heard no one and your own voice stopped going out after a minute, and stayed off until Exo restarted. Every voice cleaned at once shared one copy of DeepFilterNet's model, and they wrote over each other. Now each voice gets its own copy, so your microphone, each person you hear, and Voice & Video's meter run side by side. Three voices cleaned at once now come out exactly as each does alone. _Checked in code only._
- **Go Live on more graphics cards.** When a graphics card's video encoder refuses a stream's settings, Exo Capture tries again at 60, then 30 fps, then at 1080p, instead of failing. When it can't start at all, Exo now says why (the window, the screen, or your graphics card's encoder) instead of always blaming Windows. _Checked in code only._
- **Streams play on any graphics card.** A stream that a graphics card can't decode now plays through software decoding instead of staying black. _Checked in code only._
- **A clearer streaming icon.** Share Your Screen, LIVE in voice and friends who are streaming now show a screen with an arrow going up. _Checked in code only._

## 3.13.0 — October 21, 2026

This update gives Exo a new look: Liquid Glass on everything but the black behind it, white words, Inter as it was drawn, Tabler icons with one for every meaning, a new app icon, and motion that answers at once and settles.

### Look and feel
- **Liquid Glass you can always read.** The top bar's capsules, menus, popovers, Ctrl+K, notices, Music's player and the buttons over game art are glass again, Apple's regular kind: they take on the color of what passes behind them and bend it at their edge, and keep it in a band that leaves every word clear, even over white art (the glass in 3.10 let text fade there). Like Apple's small glass, the top bar and Music's player turn light with dark words over bright art and dark again over dark art; menus stay dark gray. The Library scrolls under the top bar now and stays sharp, and a press lights the glass where you click. Settings > General > Glass picks Clear, Tinted (frosted, so less shows through) or Solid, and only that, or contrast turned up in Windows, changes it: not a running game, not Exo in the background, not Windows' Transparency effects switch. Still glass costs nothing, and scrolling under it keeps your screen's full frame rate. _Verified: on screen._
- **White, done for AMOLED.** Words and accents are white instead of cream: primary buttons, switches, sliders, focus rings and the glass's light. The grays under it are neutral and keep exactly the contrast the warm ones had, and every word on glass reads better for it (6.6:1 at the worst spot on the top bar's glass, 10.4:1 in menus). White buttons dim a shade under the pointer, since white can't get brighter. Exo Mouse matches. _Checked in code only._
- **Inter as it was drawn.** Exo now uses Inter's own release: open 6 and 9, an l with a tail so l, I and 1 never look alike, square periods and colons that match the icons' dots, real italics in Discord, and Cyrillic, Greek and Vietnamese in the same font instead of a fallback mid-line. Titles use Inter's tighter Display cut without the extra squeeze, and small words on glass stay a touch heavier. _Checked in code only._
- **A new app icon.** Exo's mark as Liquid Glass: black glass lit from above with a bright rim, and the three bars as slabs of white glass with soft corners. It is sharp at every size, from the taskbar to the desktop, and setup, the uninstaller, shortcuts and Apps & features all use it. Setup's screens are white instead of cream too. _Checked in code only._
- **Icons, one family, one for every meaning.** Every icon in Exo, Exo Mouse and setup is Tabler's now, on a fine line that matches the words beside it, and no two things that mean different things share one: the Devices room is a mouse and the top bar's mouse capsule shows the profile it is on; Exo Capture's button is a viewfinder, Save replay rewinds and Open captures is a gallery; a server's members and your friends, a voice channel and the volume, Browse Channels and Music's queue, a song you like and a game on your wishlist, a poll and a file upload, Copy ID and Copy each have their own. Ctrl+K shows its games as games, and the Library's game menu has its icons. Play, pause and skip are solid, as players draw them; a toggle that is on (a liked song, a starred capture) and the room you're in fill in; the checks that draw themselves in use the same line. Exo's mark in the top bar and on the opening screen is the app icon's, same lean, same squircle, rim all the way round. _Verified: on screen._
- **No more fades by words.** Lists, chats, pages and the Store's shelves end at their edge instead of fading the words or rows near it. _Verified: on screen._
- **Glass on everything but the background.** Only the black behind everything is AMOLED now. Sidebars and panels, Settings, Discord's settings, Properties, the game and Store pages, cards, menus and every button, field and chip are Liquid Glass: black over black, never a flat gray, and the color of the art behind it over art. Scrolling stays as smooth as it was. _Verified: on screen._
- **Black stays black.** Glass over the black page is black now, drawn by its hairline: Discord's channel and member lists, Music's panels, Devices' cards, the message box and every control had a faint gray from a 3% floor. Over art it still takes the art's color. Tinted keeps its frost. A panel's edge is drawn on its glass, so it shows over black, and Friends are playing's capsules take the Bar's brighter glass rim. _Verified: on screen._
- **Clear is clearer.** Clear glass shows more of what is behind it while every word keeps its contrast, and the gray words on a hovered row turn white. _Verified: on screen._
- **Buttons and fields with all four edges.** Every button, search and text field, the Discord message box too, has one even edge all around; the old shading made its top line disappear on black. _Verified: on screen._
- **A smooth opening.** The opening screen shows the app icon itself, sharp at any size, and settles in smoothly instead of stuttering; setup and first run show the same mark. _Verified: on screen._
- **Discord's bar floats across.** Your picture, mute, deafen and settings, with a call in progress above them, float as one glass bar from the server icons to the channel list, your picture in line with the server icons, and the lists scroll under it. _Verified: on screen._
- **Requirements at a glance.** A Store page lists the system requirements a row per part, your PC's part beside its name with a check or a cross and Steam's Minimum and Recommended quieter under it (side by side when the page is wide), with one small verdict at the top. DirectX and Network are judged too: the DirectX your graphics card runs, and whether Windows is online. _Verified: on screen._
- **One button for emoji, GIFs and stickers.** Discord's message box has a single emoji button; its picker has Emoji, GIFs and Stickers as tabs. _Checked in code only._
- **Motion that answers and settles.** Every move in Exo now follows one set of rules: things that appear ease out, things that travel move on a calm spring that never bounces, and nothing you watch lasts longer than a third of a second. Buttons give way the moment you press and come back smoothly (most used to snap). Menus grow from the button that opened them and fade away when they close, instead of popping in and vanishing; dialogs and sheets settle in from the middle, Properties and a playlist's details too. Switching rooms is quicker and shorter, a shelf lands in under half a second, and the top bar's live capsules drop in without the old overshoot and blur. Game covers in the Library, the Store and Shaders all lift toward the pointer the same way and take a little light from their own brightness instead of a gray film. A liked song's or starred capture's icon fills in from the middle as Apple's do, buttons that appear with a row or card fade the same everywhere, and someone speaking gets the same green ring everywhere. What you do with the keyboard happens at once. The top bar no longer turns the wrong shade for a moment after you switch rooms over bright art, Music's player stays on top while its pages slide under it, Discord's banners leave instead of replaying their entrance, and a jump to a message lights at once and fades slowly. With Reduce motion on, nothing slides or grows, but menus, sheets and rooms still fade in instead of snapping. Exo Mouse moves the same way. _Checked in code only._

### Discord calls
- **Noise suppression keeps up on a busy PC.** When a game leaves too little CPU for DeepFilterNet, Exo's Discord runs its lighter first stage alone, about 30% less work, instead of letting your voice fall 10 ms further behind each time it can't keep up, and goes back to the full model once the PC has had room for 20 seconds (longer each time it has to go light again in a call, so a busy PC doesn't swap back and forth). Your mic, live, in Voice & Video says which one runs. _Checked in code only._
- **Echo cancellation on speakers.** When your speakers aren't on your head, Exo's Discord takes the call's own sound out of your mic with WebRTC's echo canceller (AEC3) before noise suppression, so nobody hears themselves back: it finds how far away your speakers are by itself, follows a new device, and costs almost nothing. On headphones or a headset it doesn't run, as there is no echo to take out. Voice & Video has the switch, on by default, and Your mic, live says whether it runs. _Checked in code only._
- **Noise suppression for others.** Turn on Noise Suppression for Others in Voice & Video and Exo's Discord takes the background noise out of everyone else's voice too, for you: each person gets a DeepFilterNet of their own, on a thread of their own, so one noisy friend never slows the rest, and the end of each sentence plays when they stop, not at their next word. A Go Live stream's sound is never touched. Off by default: each person talking costs about what your own mic's noise suppression does. _Checked in code only._

### Fixes
- **Discord Shop.** Checkout shows the item, its price and the card field again (the top of the dialog went blank), the card form is half as tall, and Stripe's Autofill button, which did nothing in Exo, is gone. The Shop shows eight items to a row at 1080p, each on Discord's own colors for it, with search in the header. _Verified: on screen._
- **Close buttons over art are glass.** A game's and a Store page's close button were drawn solid by mistake; they're glass now and, like Continue's More, turn light over bright art. With Reduce motion on, glass appears at once instead of fading in. _Verified: on screen._
- **Dialogs and switches.** Dialogs no longer show a white ring around their edge when they open, and the knob of a switch that's on sits inside its track. _Verified: on screen._
- **Profiles near the edge.** A profile opened low in a server's member list slides up to fit beside the name instead of jumping to the top of the window. _Verified: on screen._
- **Discord menus fit and read.** A submenu (Roles, Timeout, Mute, Notification Settings, Move to, Add to Calendar) opens beside its row as real glass, flips to the other side at the window's edge and keeps its row lit; a hovered row lifts the glass instead of glaring white or red, so a role's name no longer disappears; a menu taller than the window scrolls. _Verified: on screen._
- **Sharp covers on hover.** A game's cover in the Library, the Store and Shaders lifts without growing, so it stays sharp instead of blurring for a moment. _Verified: on screen._
- **Fans no longer open your mic.** With automatic input sensitivity, Exo's Discord now sends your voice only when it rises over the room after noise suppression, so the steady sound of fans and the room never opens it, and a word still opens it from its first syllable. The Input Sensitivity meter shows the sound as it's judged, after noise suppression. _Checked in code only._
- **Stream buttons say what they do.** While watching a stream, Hide Members shows people instead of a smiley, and Full Screen shows the window's own maximize instead of an arrow. _Checked in code only._
- **Sound Volume for Discord's sounds.** Notifications › Sounds has one volume for all of Discord's sounds: messages, rings and the call's sound effects. _Checked in code only._
- **Settings, Discord's settings and the Roles page** are glass like every other dialog, and a sheet opened from the keyboard no longer draws a ring around itself. _Verified: on screen._
- **Settings and Properties float.** They're a panel of glass over the dimmed room again, instead of glass across the whole window. _Verified: on screen._
- **Optional keys line up.** Save and Get one (or Remove) sit beside the field instead of wrapping under it. _Verified: on screen._
- **Discord's menus line up.** A menu row's icon sits at its end, as Discord draws it, so every label starts on one line, and every Copy ID has its ID icon. GIFs and Stickers are the same size as Emoji, so switching tabs never resizes the picker. _Verified: on screen._
- **A stream you watch stays on.** Leave for another server and come back, and the stream you were watching is still there; its controls and LIVE show while you move the pointer over it and hide when it rests. _Checked in code only._
- **New upscalers without an Exo update.** When Exo opens it asks NVIDIA, AMD and Intel on GitHub for their newest DLSS, FSR SDK (FSR 4) and XeSS release, and a new one reaches your games without waiting for Exo: Exo fetches the files your games use from the release itself and accepts each only when it matches GitHub's listing and carries the vendor's signature. FSR 4 had no other way in. _Checked in code only._
- **FSR 3.1 updates found.** A game shipping FSR 3.1.2 (Call of Duty) was taken for newer than Exo's 3.1.4, because 3.1.2's file is numbered higher, and never offered the update; FSR 3.1 is now compared, and shown, by its release. _Verified: on screen._
- **Your phone's calls show at once.** Joining or leaving voice on another device shows on the PC right away; the PC no longer takes it for its own call and waits for a connection that never comes. _Checked in code only._
- **No flicker on glass.** Glass buttons over a game's page, the Library's cards and the Bar no longer flash clear and pop back as a page opens or a room switches; a page rises from black instead of fading, so its glass reads the art from the first frame. _Verified: on screen._
- **The Store's banner.** The next game fades in only once its art has loaded and the last one leaves when the fade ends, so two games never show through each other, and the list beside it loads its art at once. _Checked in code only._
- **Every Store page laid out the same.** A game's page has one layout now, whatever the game: the trailer and screenshots with Details under them on the left, and the logo, buying, About and the same seven requirement rows on the right, in the same places and sizes on every page. Each part holds its place from the first frame while the details load, so nothing moves or snaps when they land; it was arranged anew for every game, and again as its details arrived. Opening and closing start at once: beginning any view transition made the whole page work out its styles again (25 to 70 ms before anything moved). Closing, the tile's picture appears only as the page lands on it, not blown up across the window. _Verified: on screen._
- **Spotify opens in Music every time.** When an earlier Spotify left its crash reporter running, Exo took it for Spotify itself, so Music stayed on Opening Spotify and restarted Spotify each time you opened it. Exo now starts Spotify properly whatever was left behind. _Verified: on screen._
- **Epic games update in Epic, out of sight.** A game Epic's new launcher installed now updates in that launcher: Exo starts it hidden, asks it to update the game and follows its own record until the update lands. Exo had patched the files itself, which left Epic's launcher saying the update never finished, so the game showed Update forever. Games Epic's launcher doesn't own still update through Exo directly. _Checked in code only._
- **Tall profiles fit.** A profile with many roles stays under the top bar instead of losing its banner beneath it. _Checked in code only._
- **Your mouse stays connected.** Devices no longer drops a mouse that is still there while you use it. Exo reads a device's name and serial once instead of every time any device on your PC comes or goes, so a busy receiver can't make the mouse look like a different one; when several of those checks overlap, a late answer from an older one no longer disconnects it; and a check that fails leaves the mouse connected. _Verified: on screen._
- **Finalmouse on its cable.** A Finalmouse plugged in by its cable shows up in Devices and is set directly, with your saved profiles and its battery charging; unplug it and plug the receiver back in, and Devices goes back to the receiver by itself. With both in, Exo uses the cable. _Verified: on screen._
- **No 255 % battery.** Back on its receiver straight from the cable, before the mouse has linked again, a Finalmouse keeps its last battery reading instead of showing 255 %. _Checked in code only._

## 3.12.0 — October 20, 2026

This update gives Exo a new installer and a new first-run setup in Exo's own look, and lets Exo clear out the apps it replaces.

### Install and setup
- **A new installer in Exo's look.** Install, Update and Uninstall open in one small window with Exo's buttons, switch, colors and motion, and Exo's own minimize and close. Options holds the desktop shortcut, and a bar shows real progress while Exo copies. _Verified: on screen._
- **The installer cleans up after itself.** Every install and update removes copies left by earlier updates, old logs and downloaded installers, so only the app remains. _Verified: end-to-end._
- **A new first-run setup.** It opens with a welcome, then walks through game stores, music, Discord, captures and this PC in Settings' own rows and buttons, each step sliding in from the side it comes from. A step with nothing to set up on your PC is left out. _Verified: on screen._
- **Captures in setup.** The keys for saving a replay, recording and snipping, and Print Screen, ready to change before your first game. _Verified: on screen._

### Clean up
- **Remove the apps Exo replaces.** Setup and Settings > Clean up list Discord, Snipping Tool, Photos, Xbox Game Bar, Clipchamp and the NVIDIA App when your PC has them. Remove runs the app's own uninstaller, then deletes every folder, registry key, startup entry and shortcut it leaves, as Revo Uninstaller does. Anything in Program Files asks for Windows' permission once. Spotify, the Xbox app and the NVIDIA driver are never offered: Exo needs them. _Checked in code only._
- **After a removal, nothing nags.** Without Snipping Tool, Print Screen opens Exo's snip; without Game Bar, Windows stops asking to install it again. _Checked in code only._
- **Leftovers cleared for you.** What apps you already uninstalled left in your folders is cleared when setup or Settings > Clean up opens. _Verified: end-to-end._

## 3.11.0 — October 20, 2026

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
- **Lucid's sounds and a roomier headphone picker.** With Lucid's notifications on, Switch sound and Game sound sliders set its cue volumes (0 is off); the headphone search opens as its own panel with taller rows. _Checked in code only._
- **Start Lucid with Exo stays on.** Saving settings copied each one by name and missed Lucid's, so the switch turned itself back off; every setting now survives a save (a test checks them all). _Verified: in the host._

### Look and feel
- **Snip without a flash, and Esc cancels:** starting a snip no longer flashes black over your screens, and a snip started from Exo takes the keyboard, so Esc cancels and Enter takes the whole screen. _Checked in code only._
- **Rooms stay as you left them:** coming back to Music or Discord no longer reloads the page, replays its slide-in or jumps to the top; the open chat keeps its messages and only fetches what is new. The Store keeps its scroll too. _Checked in code only._
- **Songs without the mood row:** the genre and mood chips over your Liked Songs are gone, so the list starts right under its title. _Checked in code only._
- **True black everywhere:** panels and sheets are AMOLED black set apart by one thin line instead of a gray, see-through fill. Properties' section list sits inside its panel, as in Settings, and Devices matches. _Verified: on screen._
- **Black liquid glass:** buttons, fields, chips, Discord's composer and search, and Exo Mouse's controls are one solid polished-black material with a light rim along the top, plainly visible on black. Fields sit recessed, a press sinks, and Play, Get on Steam and Confirm stay cream. _Verified: on screen._
- **Setup is Exo's own window:** black and borderless, with the Exo mark, one Install button, a thin progress bar and Launch Exo; if setup fails, the reason shows on its last screen. Setup, the uninstaller, shortcuts and Apps & features all show the current Exo mark, sharp at every size, and in-app updates stay silent. _Verified: on screen._
- **Rooms come back loaded:** their arrival animation plays only the first time. _Checked in code only._
- **No flicker:** Settings opens with one float-in, and pages never stretch mid-transition. _Checked in code only._
- **No light rim** on the rounded corners of game art. _Checked in code only._
- **One look for every field,** words on true center. _Checked in code only._
- **Captures favorites:** the star turns at once and a Favorites chip shows every starred capture. _Checked in code only._
- **Clicks never get lost:** a click while a page is still moving lands on what you pointed at, opens it and stays open, and a page can never freeze mid-move. _Verified: on screen._
- **Downloads live on the cards:** the top-bar download pill and the floating download list are gone; each game card shows its own progress ring and Update tag. _Checked in code only._
- **Settings panels are evenly inset.** Exo's and Discord's floating settings keep 12px all round, and the boxes inside follow the panel's corners. _Checked in code only._

## 3.10.0 — October 19, 2026

This update rebuilds the Library around what you actually do, gives Exo a warm cream-on-black look with its own black Liquid Glass, and makes Exo's Discord client match Discord feature for feature in Exo's style.

### Library
- **Properties for every installed game.** On its page and in its card's right-click menu: launch options, language, cloud saves, its files (size, Browse, Verify, which drive it's on), automatic updates, betas with access codes, and its DLC, such as Marvel Rivals' High-Resolution Pack, switched on or off with the space it takes or frees shown before anything downloads or is deleted. Epic games get their optional content, such as high-resolution textures, the same way. _Checked in code only._
- **Your Steam Family's games.** Every game your Steam Family shares sits in the Library next to your own, installed or not, and Install hands it to Steam; one you are downloading shows up with Steam's progress the moment it starts. Games you own yourself always count as yours, only games make the list, and Steam games you own but haven't installed no longer go missing. _Verified: on screen._
- **Steam accounts in Settings.** Every account Steam remembers on this PC is listed under Steam with the signed-in one marked, Switch and Remove on the others, and Add account to sign another in through Steam's own window. Switching restarts Steam hidden as that account, and your library and hours follow it. _Checked in code only._
- **Riot accounts.** Switch between your League and VALORANT accounts in Settings > Game stores, or right-click a Riot game and pick Play as. Exo keeps each account's "Stay signed in" session encrypted on this PC, never switches while a Riot game runs, and never touches Vanguard. _Checked in code only._
- **Store apps stay out of sight.** Steam, the Epic Games Launcher, GOG Galaxy, Riot, the EA app, Ubisoft Connect, Battle.net, Amazon Games and Xbox stay hidden for as long as Exo runs them, not just while a game starts, and their tray icons go away until you open one or close Exo. Sign-in, Steam Guard and agreements still show. _Checked in code only._
- **Apps start when they're needed.** Settings > Game stores and General have a switch for each store app, Discord and Spotify that starts with Windows, and first run turns them off for you unless you say otherwise; turning one back on restores it exactly. The new Epic Games Launcher starts in its tray with its notifications off while Exo starts it. _Checked in code only._
- **Quiet installs.** Visual C++ installs with only a progress bar, WebView2 and Spotify without an installer window, and none of Exo's background tools flash a console window. Windows still asks for permission where it must. _Checked in code only._
- **Set up for you.** On an NVIDIA PC, first run ends with This PC: Exo's driver profile for your card and screen, on by default and applied when you finish (undo it any time in System > Graphics). Everything else Exo tunes by itself, and a new Library sorts installed games first. _Checked in code only._
- **Steam's own downloads show live progress.** When Steam updates a game by itself (its auto-update, or one you started in Steam), Exo now follows it with the same live reader as its own downloads, so Downloads shows real progress and speed instead of a size Steam rewrites only now and then. Nothing is asked of Steam and its window is left alone. _Checked in code only._
- **The Library stays where you left it.** Coming back to the Library no longer scrolls it down on its own (the A to Z strip that did it is gone). _Verified: on screen._
- **Installed first.** A sort that puts the games you can play now first, A to Z, then the rest. Games you haven't installed sit darker and drained of color until you point at them. _Verified: on screen._
- **Continue.** The games you are playing sit at the top as big landscape art with Play one click away: the one running glows green, one downloading or updating shows a ring with its percent, the rest show playtime and when you last played. The first game's art lights the black around it. _Verified: on screen._
- **Friends are playing.** When friends on Exo's Discord client are in a game you own, it shows as one capsule with their faces, the game and Play (or Install). _Verified: on screen._
- **One grid of every game.** Installed and not installed live together in the order you sort them; a game you haven't installed carries a small download mark instead of a separate shelf. _Verified: on screen._
- **Clicking fast opens, never plays.** Clicking a game again and again opened its page and then pressed Play under the pointer (double-click played on purpose). Only Play plays now: presses in the streak that opened the page do nothing. _Checked in code only._
- **The Library holds still.** Opening a game's page nudged the Library up 5 px (its buttons left the shelf header); they stay now. _Verified: on screen._
- **The store beside the name.** A game's page shows its store (or the stores to choose between) as a pill beside its name, and the cover sits alone with the same space above and below it. _Checked in code only._
- **GOG games Exo installs now start, and Update really updates.** Exo recorded the folder above the game, so Play never started it, and Update only re-checked the files it had. It now records the game's own folder, fixes games installed earlier, and Update moves to the newest build. _Checked in code only._
- **Downloads show what's really happening.** GOG shows a percentage; Epic, GOG and Amazon show your network speed instead of disk writes; the status line is Exo's own words instead of the tool's log lines. A failed Epic install says why at once instead of waiting up to two hours. _Checked in code only._
- **Epic games Exo installs, start and leave cleanly.** Play starts a game Exo installed through Epic in about two seconds (Epic's own launcher had no record of it and started nothing), an uninstalled game no longer comes back as installed, Cancel removes the half-finished download while Pause keeps it to resume and says Paused, and a finished install shows as installed at once. _Verified: on screen._
- **Steam downloads show their speed, and Cancel leaves nothing.** A Steam download shows how fast it is going beside the gigabytes, worked out from what Steam reports, and a canceled Steam install leaves no empty folder in your Steam library. _Verified: on screen._
- **Nothing stays stuck.** Stop clears an EA, Ubisoft or Battle.net launch that never started. A canceled Steam update or a damaged download queue no longer blocks installs, paused downloads no longer turn off auto-update, uninstallers can ask Windows for permission, and the window no longer freezes while a game uninstalls. _Checked in code only._
- **A menu on every game.** Right-click any game, or press More on Continue, for Play or Install and Open folder. Play from Continue or Friends are playing says Starting… and reports a failure instead of doing nothing. _Verified: on screen._
- **The game page says what happens.** Shaders compiles that game right there (Get Exo Shaders when it's missing), HDR says what really happens and offers to turn on HDR for HDR games, and failed upscaler reads and library refreshes say why, with Check again or Retry. _Checked in code only._
- **Uninstall does what it says.** An uninstaller whose path has spaces and no quotes starts instead of failing at "C:\Program"; an Amazon or GOG game Exo couldn't remove says so instead of reporting it removed or handing the job to another copy of the game. _Checked in code only._
- **A store that can't be read is read again.** When one store's library can't be read, the next refresh asks it again instead of reusing its older list; Steam playtimes no longer blank out while the Library refreshes. _Checked in code only._
- **Pins are gone.** The Library is one grid in the order you sort it; nothing offers Pin anymore. Settings saved with pins still load. _Checked in code only._
- **Activate key fills the key in for you.** Steam opens its own key page with your key entered and GOG's redeem page arrives filled in; you accept and confirm there. Epic's page can't take a key, so Exo copies it for you to paste, and the popover says which store does what. _Checked in code only._

### Store
- **Game pages fill the window.** On a Store game page the trailer starts at the top and is as large as your screen allows, and the game's name, tags, store buttons, description, details and requirements fill the right side top to bottom with no empty band; on smaller windows the details tuck under the screenshots, and nothing ever scrolls. Steam descriptions no longer show stray &amp; codes. _Verified: on screen._
- **The trailer gets its size back.** System requirements and This PC sit in the right column under buying again, so a game's trailer and pictures fill the left side. _Verified: on screen._
- **The Store recovers on its own.** A search or home page that can't reach the store says so with Retry instead of loading forever, a failed Refresh says it failed, Epic, GOG and Ubisoft prices come back on the next visit after a slow answer, the home page shows refreshes that landed while you were elsewhere, and region-locked games say they aren't sold in your region. _Checked in code only._
- **Clearer game pages.** System requirements sit under the trailer and the tags stay clear of them. Free-to-play games show Free, unreleased wishlist games show their date, and This PC says whether your graphics card is enough for Minimum or Recommended. _Checked in code only._
- **Reduce motion holds the Store still.** _Checked in code only._
- **Your wishlist never looks empty when it isn't.** If Exo can't read the wishlist for a moment, the Store keeps it on screen and says so. Adding or removing a game that can't be saved goes back, with the reason. _Checked in code only._
- **Epic's free games stay current.** A saved Store page no longer shows a free game whose free days have ended. _Checked in code only._
- **Game pages fit narrow windows.** Below 900 px a game page scrolls as one column, and long developer and publisher lists wrap. _Checked in code only._
- **System requirements are back.** Minimum and Recommended show under the trailer again, ending with This PC (your Windows, processor, graphics card and memory against the game's); they had slipped out of view below the media. _Checked in code only._
- **Tags in the same place on every page.** Short logos, tall logos and games with no logo all put the tags at the same height. _Checked in code only._

### Lucid
- **Lucid, set up from Exo.** Settings > Lucid starts Lucid with Exo (out of sight), opens it or its first setup, picks the outputs it plays through, and chooses your headphones from Lucid's own list, with search and favorites. Without Lucid, the same place offers Get Lucid. _Checked in code only._
- **Lucid's game and night settings in Exo.** Game Mode on or off, Keep the game's EQ on Alt+Tab and Only while gaming, Night Shift hours or Windows Night light, its notifications and Discord status. Each game's own EQ stays as Lucid tunes it. Lucid closes the way Windows closes it at sign-out and comes back out of sight with the change; Lucid Voice is never touched, since Exo Voice handles the mic. _Checked in code only._
- **Lucid's updates in Settings.** Settings > Lucid shows the installed version and says when a newer Lucid is out, with Update to open Lucid, which installs it itself. While Exo runs it, Lucid stays out of sight with no tray icon, which comes back when Exo closes. _Checked in code only._
- **First run offers Lucid.** This PC adds Start Lucid with Exo (off until you turn it on) and Set up Lucid, or Get Lucid when this PC doesn't have it. _Checked in code only._

### Look and feel
- **Exo moves like one app.** Rooms slide toward the tab you pick, pages inside a room push in from the side you chose, and the art you open flies to where it lands and back: a game's cover into its page, a Store tile into its game page, an album into its page, the song into Now playing, a Discord picture into the profile or the viewer, a capture into its viewer. A press mid-move ends it and lands where you pointed, and Reduce motion turns it all off. _Verified: on screen._
- **Highlights follow rounded corners.** Discord's server name, hover bar, folders and user panel, Music's sidebar rows and recent covers, Ctrl+K rows, and the focus rings on switches, sliders and text buttons now take the shape of what you point at instead of showing a square. Spacing is even too: Discord's settings, user panel and thread list, System's header, first run's music tiles and the downloads panel sit at the same insets as everything around them. _Checked in code only._
- **Settings float.** Exo's Settings and Discord's settings open as one panel over the room you were in, as Discord's own settings do now: up to 1200 by 880, centered over the dimmed room, and a press around them closes them. _Verified: on screen._
- **A System room.** Your graphics card and screen get their own room between Music and Devices: a slim header with your card, driver and screen, and three pages that fit the window without scrolling: Graphics (driver and Exo's profile, HDR, upscalers in your games), Display, and Shaders. The Library no longer has a GPU button. _Verified: on screen._
- **Shaders, simply.** Scan games, see every game as its poster with its state, press Compile, and watch the queue play out live on the posters: the one compiling first with its progress, the rest in order. Click a poster to compile just that game. _Verified: on screen._
- **The right NVIDIA driver.** Choose Game Ready or Studio. On Game Ready, a newer GeForce hotfix driver from NVIDIA's support site is offered when there is one. Workstation cards (NVIDIA RTX, Quadro) get NVIDIA's RTX Enterprise driver instead of the data center one, and laptops their notebook driver. _Verified: on screen._
- **Upscalers line up.** Update all and Check again end where the panel's line above them ends, instead of 20 px short. _Checked in code only._
- **Pages fade at their edges.** A page that scrolls fades softly into the black at its top and bottom instead of being cut off, and Store shelves fade at a side with more beyond it. The see-through band under the top bar is gone. _Verified: on screen._
- **The Store shows its rhythm.** A thin line fills under the featured game until the next one, and holds while you point at the banner. _Checked in code only._
- **This PC under the requirements.** A Store game's System requirements end with your Windows, processor, graphics card and memory, and whether your memory covers the minimum or recommended. The Steam Deck rating is gone (Exo plays on Windows). _Checked in code only._
- **Things glide.** The rooms slide to make room when the song comes and goes in the bar instead of jumping. _Checked in code only._
- **A quieter top bar.** Search is a smaller capsule without the Ctrl K label, and the light that swept under the bar while a game started is gone (Play's own animation shows it). _Verified: on screen._
- **No duplicates.** Each thing has one home: Discord's account, voice and notification settings live in Discord's settings (the gear in your Discord bar opens them), and in the Music room the song is in the room's own player, not also in the top bar. _Verified: on screen._
- **A cleaner search.** Ctrl+K lists each game, room, DM and server on one line, with no notes under them. _Verified: on screen._
- **Pure black.** Every page's background is black again: no colored glow behind art, and no cut-off color under the top bar. _Verified: on screen._
- **Menus match.** Every menu and dropdown (Exo's, Discord's and the mouse's) has the same rows, the same hover and a check beside the current choice. _Verified: on screen._
- **Smoother art corners.** Art no longer has a hairline under it, which showed as a ragged ring around rounded corners. _Verified: on screen._
- **Cream on black.** Text is a warm cream with warm grays, the same in every room; section titles are one size everywhere, Store included. _Verified: on screen._
- **Black Liquid Glass.** Buttons over game art are a dark glass that bends the art at its edge, with a light rim. It draws solid while a game runs and when Windows' transparency effects are off or contrast is up. _Verified: on screen._
- **One design across the app.** Every room now shares one Apple-style system: controls are filled capsules instead of outlines, one gray marks what is selected everywhere, sidebars and side panels float as rounded panels (Settings, Music, Discord, Devices), Music's player is a floating bar, menus and popovers are one raised panel, sheets have wider corners, and switches are full-size. _Verified: on screen._
- **One bar.** Search on the left (the Exo mark; Ctrl+K), the rooms in the middle, the song beside them, and Capture and Settings on the right. A room's own controls stay in the room. The bar shares its width and compacts in steps on smaller windows, so nothing overlaps. _Verified: on screen._
- **Live, beside the rooms.** The song playing, a Discord call, the game you're playing or a download sits in one small capsule next to the rooms; hover the song and it grows in place into the full player (skip, seek, time and volume) without moving anything. _Verified: on screen._ Downloads _Checked in code only._
- **Every press gives.** Buttons and capsules give way a little under the press. _Verified: on screen._
- **The bar's controls hold still.** The mini player's seek line and volume could drag the window instead of moving: the bar's clickable spots were measured mid-glide and never again. They now follow every glide to where it lands. _Verified: on screen._
- **Songs change in place.** The mini player kept vanishing for a moment between two songs, so it shrank and grew back; the last song now stays until the next one arrives. _Checked in code only._
- **Window controls in the Tools capsule.** Minimize, maximize and close sit in the same floating capsule as Capture and Settings, after a hairline, so the top right is one piece like the rest of the bar. _Verified: on screen._
- **Every room is ready when the splash ends.** Rooms loaded behind the splash didn't actually ask for their data until you opened them (a hidden room runs nothing). Now System (card, driver, screen, Display, Shaders), the Store, Discord and Music load during the splash, so they open filled. _Verified: on screen._
- **Covers at full quality.** The mini player's cover is the picture the music app gives, untouched, instead of a 256 px recompressed copy; Spotify's comes at 640 px and Apple Music's at 600. When Spotify's page has no cover yet, Windows' copy of the same song's cover stands in. _Verified: on screen._
- **Settings uses the window.** On a wide window each panel lays its rows in two columns, and the sidebar lines up with the Search capsule. _Verified: on screen._
- **Settings follows you down the page.** Scrolling marks every section in the sidebar in turn; short sections near the end were skipped. _Verified: on screen._
- **Settings like System Settings.** Each section has its icon in the sidebar and the settings sit right beside it. _Verified: on screen._
- **Cream text everywhere.** A theme setting was painting text plain white over Exo's cream; every room now shows the warm cream and grays. _Verified: on screen._
- **Centered labels.** Text in buttons and tabs sat 1-1.5px above center in places; every label now sits on its middle. _Verified: on screen._
- **New icons.** Every icon in Exo is now Lucide: one clean stroke, the same weight everywhere. _Verified: on screen._
- **The Exo Island.** A small capsule in the top bar while something is live that the room you're in isn't showing: a Discord call (mute and leave from anywhere, the dot rings when someone speaks) or the game you're playing (click to open it). Nothing live, nothing shown. _Verified: on screen._
- **Ctrl+K finds everything.** Besides games and actions it now finds your Discord DMs and servers (Enter opens them) and plays, pauses or skips music. _Verified: on screen._
- **The top bar never overlaps.** The song and the call compact inside their own slots as the window narrows, the download capsule opens the Library's queue from any room, and Ctrl+K keeps Tab inside, walks rows in the order shown and shows Check for updates as a toast. _Checked in code only._
- **Errors where they happened.** Settings and first run put every error in red under the row that caused it (update installs, store sign-ins, folders, Capture and Music), keep first-run errors until you act, and cap Settings at a 1080 px reading column. _Checked in code only._
- **Covers with clean corners.** Game art, posters, avatars and thumbnails no longer show a thin gray rim or jagged pixels at their rounded corners; hover and selection draw a ring just outside the picture. _Checked in code only._
- **Every button the same in every room.** Primary, regular, quiet and danger buttons each have one fill, with one hover and one pressed shade, across every room and Exo Mouse. _Checked in code only._
- **One slider everywhere.** Discord's settings, Capture, Display, Music, the mini player and Exo Mouse all use the same slider with a visible knob. _Checked in code only._
- **The song stays in a small window.** When the window narrows, the mini player keeps the cover, title and volume, letting the room names, artist and skip buttons go first. _Checked in code only._

### Discord
- **An emoji picker you can see.** The picker is Discord's size, nine 32px emoji to a row, server emoji and Unicode alike. Favorites and Frequently Used from your Discord account sit at the top, and right-clicking an emoji favorites it everywhere you use Discord. Reacting with ©, ®, ™, ⚧ or 👁‍🗨 no longer fails with "Unknown Emoji", and without Nitro another server's emoji shows locked instead of failing. _Checked in code only._
- **Discord feels instant.** Channels and DMs you have opened show their messages the moment you go back, a channel or server you rest the pointer on loads before you click, switching servers keeps the channel and member lists on screen, and the member list no longer empties when you change channel or the connection drops for a moment. _Verified: on screen._
- **Notifications behave like Discord's.** No pings for @silent messages, blocked people, message requests, muted categories and their threads, or during Do Not Disturb, and never twice for one message. While Exo is in front they arrive as Exo's own banner with the person's picture, where it was said and two lines of the message; clicking one opens the chat. Opening or reading a chat, here or in any Discord app, clears its notifications, Windows' included. _Checked in code only._
- **Discord's image menu.** Right-click any picture, including one open full size, for Copy Image (the picture itself), Save Image (to Downloads under Discord's file name), Copy Link and Open Link; the viewer has a Save button. _Checked in code only._
- **Pictures load like Discord's.** Pictures are fetched at the size they are shown instead of as multi-megabyte originals that sat half-drawn in busy channels like Finalmouse's announcements; single pictures show up to Discord's 550 by 350, and YouTube previews lose their black bars. _Checked in code only._
- **Menus open where there's room.** Every menu, picker, profile card and submenu flips up or left near an edge and stays inside the window, and a right-click opens the message menu at the pointer, so the last message's menu opens upward. _Verified: on screen._
- **Attachments you can manage.** Attached files show a picture preview and a shortened name, so Remove always shows, and Enter sends pictures with no text. _Checked in code only._
- **The unread dot clears at the bottom.** Reaching the newest message clears the channel's dot and mention count at once. _Checked in code only._
- **The Inbox has For You, Unreads and Mentions.** For You shows Discord's notifications and opens what each one points to; Unreads lists every unread channel across your servers with its newest messages, a jump, and marking a channel or server read; Mentions can show just this server and leave out @everyone, @here or role mentions. _Checked in code only._
- **Search works like Discord's search bar.** from:, mentions:, has:, in:, before:, during:, after:, pinned: and authorType: with suggestions that become chips; results show their count, sort by Newest, Oldest or Most Relevant, and come 25 to a page. _Checked in code only._
- **Reactions show who reacted.** Point at one for the names or right-click for the full list (remove someone's with Manage Messages); super reactions show in their colors and, with Nitro, can be added. _Checked in code only._
- **Files look like Discord's.** Several pictures share one grid, described pictures get an ALT tag, text and code files show their first lines with Expand, and other files show as a card with size and download. _Checked in code only._
- **Slash commands and app buttons.** Type / to run an app's commands with each option filled in, see "is thinking…" and dismiss answers only you can see; app buttons, menus and cards work in both chat styles, app forms open as Exo dialogs, and right-click a person or a message for its Apps. _Checked in code only._
- **Profiles you can act on.** Every connection type shows its own mark and opens like Discord's; where you can manage roles, give or take a role right from a profile; full profiles have the friend buttons and your own has Edit Profile; Settings > Profiles gains Server Profiles for per-server nicknames. _Checked in code only._
- **First run includes Discord.** Setup gains a Discord step: sign in with Discord's phone QR code (or see that you already are), and a pointer to Voice Tune. It can be skipped; the Discord room offers the same code later. _Verified: on screen._
- **Go Live says what it really sends.** A resolution bigger than the screen or window you picked says so, for example "Your screen is 1080p, so this streams at 1080p." Exo never enlarges your picture the way Discord does, which adds no detail. _Checked in code only._
- **Chat reads at Discord's size.** Messages and names are 15px with Discord's line and group spacing, and the last message has room above the message box instead of sitting on it. _Verified: on screen._
- **In Exo's look.** Black surfaces with hairlines, cream text and cream primary buttons, Exo's red and green for states, and a compact server rail with 40px icons. The selection glides between channels, DMs and Friends tabs. _Verified: on screen._
- **Servers like Discord.** Server Guide with its resources, Events (create, edit, start, end, cancel, interested, add to calendar), Browse Channels, Members with join method and roles, Create Channel, Invite, mute and mark as read. Tested on a real server. _Verified: on screen._
- **DMs and Friends like Discord.** Active Now with the game or song and time elapsed, a profile beside each DM with mutual servers and friends, full profiles, and call lines in the chat. _Verified: on screen._
- **Quiet while playing.** While a game runs, Discord messages wait in the unread badge instead of popping up. On by default; turn it off in Discord's settings > Notifications. _Checked in code only._
- **Discord's settings, all of them.** The gear opens Discord's own User Settings, laid out as Discord's: Profiles (display name, pronouns, avatar, banner color, About Me), Account (username, your email and phone number masked, two-factor status, Logged-in Devices with Log Out; the password and closing the account open on Discord's site), Data & Privacy, Messaging Permissions (sensitive media filters, the spam filter, DMs and message requests from server members, who can send friend requests), Notifications, Voice & Video, Appearance, Accessibility, System, Language & Time, Activity Privacy, Connected Apps (connections, and authorized apps with Deauthorize) and Developer. Account settings are your Discord account's own, saved the way Discord's apps save them, so a change here shows in Discord everywhere. _Checked in code only._
- **Voice like Discord's.** Input Profile is Voice Isolation, Studio or Custom; in Custom, Input Sensitivity (automatic, or a threshold over a live meter of your microphone), Noise Suppression, Echo Cancellation (Windows' own, on microphones that have it) and Automatic Gain Control. Push to Talk takes a key or a side mouse button and a release delay; Global Attenuation lowers other apps while someone speaks; Keybinds (Push to Talk, Push to Mute, Toggle Mute, Toggle Deafen, Toggle Voice Activity Mode, Toggle Streamer Mode, Disconnect from Voice) work anywhere in Windows during a call, even in a game. Pick the camera calls use and test it. _Checked in code only._
- **Discord's sounds.** New Message, Incoming and Outgoing Ring, Mute, Unmute, Deafen, Undeafen, PTT, User Join and Leave, Voice Disconnected and Camera On and Off play as in Discord, each one switchable with a preview, and Disable All Notification Sounds silences them all. Windows notifications, taskbar flashing and the taskbar's unread badge can each be turned off. _Checked in code only._
- **Chat as you set it.** Compact message display, text size in chat, space between message groups, zoom, saturation, role colors in names, beside them or not at all, links always underlined, spoilers always shown, pictures and link previews off, emoji reactions hidden, emoticons sent as emoji, stickers in autocomplete, 12- or 24-hour times, /tts messages read out at your rate, Copy IDs with Developer Mode, and Streamer Mode hiding your email, connections and invite links and silencing sounds and notifications. _Checked in code only._
- **A smaller home mark.** The Discord mark at the top of the server rail is a small glyph in its tile, not a full white tile. _Verified: on screen._
- **Only Exo's client.** The Discord room is always Exo's own client; Exo no longer hosts, installs or patches the Discord app, and the switch for it in Settings is gone. _Checked in code only._
- **Your bar floats.** Your name, mute, deafen and settings sit in their own floating bar under the sidebar; devices and status are set in Discord's settings, not from small arrows beside the buttons. A call floats above it and opens the call when clicked. _Verified: on screen._
- **Direct messages shows a count.** No selection pill beside it, just a number when something is waiting. _Verified: on screen._
- **Buttons close what they opened.** Pressing Pinned messages, Threads, Inbox, GIFs, Stickers or Emoji again closes it. _Verified: on screen._
- **Profiles and pictures open.** Click someone's picture in chat or in Friends for their full profile; images and GIFs from links open full size like uploads do. _Checked in code only._
- **Easier to read chat.** Names are a step larger than the messages under them, with Discord's spacing between groups. _Checked in code only._
- **Performance mode.** In Discord's settings > Accessibility, Enable Reduced Motion: GIFs hold still until you point at them, and nothing in Discord animates or moves on hover. _Verified: on screen._
- **GIFs: Trending and Favorites.** Favorites are your Discord account's own list, the same as on every device: the star adds or removes one. GIFs show in two columns at their own heights, and load again: Discord now serves them from a new host Exo had blocked. _Verified: on screen._
- **Voice channels have a view.** Opening a voice channel shows everyone in it as a tile with a ring while they speak, and the call's controls (mute, deafen, disconnect, or Join Voice) in one bar under them. _Checked in code only._
- **Video in calls: Go Live, screen share and cameras.** Share Your Screen in a call goes out end-to-end encrypted like Discord's own, its sound as music-quality stereo that never echoes the call's voices. Turn On Camera does the same with your camera, encoded by the graphics card (H.264). People streaming show LIVE with Watch Stream, which opens their stream big above the call with its sound; cameras show in the tiles. _Verified: on screen._
- **Go Live like Discord, with Exo's own picker.** Share Your Screen opens Exo's picker instead of Windows': Applications and Screens, each a fresh picture with its title and app icon, and the game you're playing first. Then Resolution (720p, 1080p, 1440p or Source) and Frame rate (15, 30 or 60), each with one line saying what it's for, and Stream sound, beside a live preview of what you'll share; Exo remembers your choice. A window sends only its app's sound, as Discord does; a screen sends everything your PC plays except Exo and the call. While live, the button shows your stream with its real resolution, frame rate and bit rate, and Stop Streaming. Exo Capture captures it and the graphics card encodes it (no yellow border, cursor shown, a steady bit rate for Discord), and the picture goes straight to Discord without passing through Exo's page, so it is sharper and lighter than before. _Verified: on screen._
- **No lost video.** A lost video packet is asked for again and Discord resends it; only when it can't be filled does Exo ask for a fresh keyframe, so a stream doesn't freeze or smear on a shaky connection. Your own stream resends what Discord missed and makes a keyframe when someone starts watching. _Checked in code only._
- **Better call sound.** Your voice goes out at the channel's own bitrate (up to 384 kbps on boosted servers) instead of a fixed 64 kbps, and a lost voice packet is rebuilt from the next one (Opus FEC) or smoothly concealed, instead of a click. _Checked in code only._
- **Friends without the extra Message.** The More menu on a friend no longer repeats the Message button beside it. _Checked in code only._
- **Server Settings, the whole of it.** Where you have the permission, the server menu opens Discord's Server Settings in its own groups: Server Profile (name, icon, banner on boosted servers, description), Engagement (system messages and their switches, the inactive channel and time-out, default notifications), Emoji and Stickers (upload, rename or edit, delete, slots left), Members, Roles, Invites (who made each, uses, expiry, revoke), Integrations (webhooks and apps, remove), Safety Setup (verification level, sensitive content filter, the 2FA requirement shown), AutoMod (Discord's five rule kinds: custom words, mention spam, suspected spam, commonly flagged words and member names, each with its actions and exceptions, on or off), the Audit Log (filter by person or action, what changed and why, load older) and Bans (search, the reason, Revoke Ban). Each section shows only with the permission Discord asks for, and changes wait for Save Changes as in Discord. _Checked in code only._
- **The role editor.** Create Role, drag roles to reorder them, and edit one: name, color (Discord's swatches or your own), Display separately, Allow anyone to @mention, every permission in Discord's groups with Discord's descriptions (searchable, Clear permissions), and Manage Members (who has it, remove, Add Members). Each role shows its member count; roles above yours stay locked, as in Discord. _Checked in code only._
- **Edit Channel.** A channel's right-click menu has Edit Channel: name, topic, slowmode, age-restricted, a voice channel's bitrate and user limit, and Permissions per role or member, each one allowed, left to the roles, or denied, as Discord shows them; Delete Channel asks first. _Checked in code only._
- **Right-click anyone.** A name or picture in chat, the member list, a call, the Members page, Direct messages or the Audit Log opens Discord's person menu: Profile, Mention, Message, and on a server what you may do to them: Change Nickname, Server Mute, Server Deafen, Disconnect and Move to while they're in voice, Timeout (60 seconds to a week, or remove it), Kick and Ban (with a reason, and how much of their message history to delete), and their Roles to tick on or off; then Copy Username and Copy User ID. Only what your roles allow, and only for people below you. Clicking someone in the member list opens their profile card. _Checked in code only._
- **Moderating messages.** With Manage Messages you can delete anyone's message; Pin Message shows only where you may pin. One message's menu is open at a time: pressing elsewhere closes it. _Checked in code only._
- **A Discord crash fixed.** The build tool turned a number in Discord's date math into nothing, which crashed the room; that math now lives in one place that builds right, and a check stops it coming back. _Verified: on screen._
- **Discord remembers where you were.** The channel you had open in each server, and the server or DMs you were in, come back when Exo starts again, as in Discord. _Checked in code only._
- **Pictures at full quality.** Profile pictures, server icons and banners load lossless at twice the size they show (they were small compressed copies). _Checked in code only._
- **Emoji stay sharp.** The Apple emoji style is gone: its pictures topped out at 64 px and looked soft. Windows' own emoji draw at any size. _Checked in code only._
- **Stickers like Discord's tab.** Every sticker shows its name, under its server's name and icon: this server's first, then Discord's own packs (Wumpus and friends), then your other servers' (locked without Nitro, as in Discord). Animated stickers play, in the picker and in chat, at full quality; a sticker in chat shows its name under the pointer. Stickers work in DMs too. _Verified: on screen._
- **Polls.** Polls in chat show as Discord's: pick an answer (or several), Vote, see each answer's share fill in with your vote marked, Remove Vote, and End Poll on your own. The + beside the message box has Create Poll (a question, up to ten answers, how long it runs, one answer or several). When a poll ends, its line says who won. _Verified: on screen._
- **Voice messages play.** A voice message plays with its waveform filling as it goes (click it to jump) and the time left; other sound files get a small player. _Checked in code only._
- **Soundboard.** In a call, the Soundboard holds Discord's sounds and your server's: press one and everyone in the channel hears it; sounds others play are heard here too (not while deafened). _Verified: on screen._
- **Replies read right.** The quote over a reply shows mentions as names and emoji as emoji, instead of codes like <@768…>. _Checked in code only._
- **Every name readable.** A role color too dark for Exo's black (a near-black bot role) is lifted just enough to read; other colors are the role's own. _Checked in code only._
- **Delete asks first.** Delete Message shows the message and asks before it goes, as Discord does; hold Shift to skip the question. _Checked in code only._
- **Full profiles one click away.** On a profile card, click the picture or the name for the full profile; the View Full Profile link is gone. _Checked in code only._
- **Smooth GIFs.** The GIF picker played every GIF at once (each one a video); only the ones in view load and play now, and held-still ones still show their first frame. _Verified: on screen._
- **The message box lines up.** It is one of the room's floating boxes now: as wide as the chat, its foot and top on the same lines as your user bar beside it, with who is typing floating above it. _Verified: on screen._
- **Profiles fit.** A profile's banner keeps Discord's shape, or is a short strip of their color when they have none; the full profile fits its content instead of a fixed tall box; the profile beside a DM floats like the member list. Click someone's picture or name there for their full profile (no more View Full Profile button). _Verified: on screen._
- **Escaped text reads right.** Text Discord escapes, like `Kunai\.` in a link preview, shows as written, without the backslash. _Verified: on screen._
- **Calls look like FaceTime.** People in a call are 16:9 tiles as large as fits, each lit by their own picture blurred behind them, with a ring while they speak; the controls float in one capsule on the same line as your user bar. A call alone no longer fills the whole room with one huge tile. _Verified: on screen._
- **One place for Discord's settings.** Exo's Settings no longer has a Discord row; Discord's settings open from the gear in your Discord bar. The section is now just Music. _Checked in code only._

- **Exo Voice.** Two input profiles now: Exo Voice, everything Exo has tuned together (DeepFilterNet noise suppression with its post-filter for the noise left under speech, echo cancellation, Voice Focus, automatic sensitivity), and Custom for every dial yourself. Voice Isolation and Studio become Exo Voice. Sound that reaches Exo late after a stall (a heavy game) is dropped instead of sent late, so the call stays live and noise suppression never falls behind. _Verified: on screen._
- **Calls no longer turn your game down.** Exo's Discord calls opened the microphone the way Windows treats as a call, which let Windows turn every other sound down by 80% while you talked. Exo now opts out of that; Discord's own attenuation in Voice & Video, your choice, is the only one. _Checked in code only._
- **Voice Tune.** In Voice & Video (Exo Voice), about 45 seconds, guided step by step: a moment of quiet, a passage read normally, a few words loud and a few soft, then typing and clicking while quiet. Exo hears your room (its noise and any mains hum), your voice (its pitch and level, soft to loud) and your mic (its level, whether it clips, whether it only carries up to 8 kHz like a Bluetooth headset), then tunes Exo Voice for that voice on that mic: the low cut (lower for a deep voice, so it keeps its weight), the gate's floor just under your soft voice, how hard noise suppression cuts (lighter in a quiet room, where it sounds more natural), a filter for any hum, and the level, set at the mic itself when Windows' input level is the mic's own hardware gain. Play your reading back before and after first, then keep it; it's saved for that mic. It also knows your microphone (about 90 popular mics, XLR interfaces, mixers and headsets by their USB id or name; Bluetooth and built-in mics by how they connect) and says exactly what to set in its own app: what the device does better (its gain knob, clip protection, a hardware low cut) and what to turn off so it doesn't work twice (built-in noise cancelling, auto gain). Only where a device's noise removal is as good as Exo's (NVIDIA Broadcast, on the GPU) does Exo Voice's own step aside. In a call you're muted while it listens. It also says when Windows has the mic muted, and can stop Windows turning your game down during calls. It never changes the tone of your voice: a natural voice differs from the average as much as a coloured mic does, so a correction would change good voices too. _Verified: on screen._
- **Voice Tune spots double processing.** When another app already processes your microphone before Exo hears it (Lucid Voice, or Equalizer APO on its own), Voice & Video says so under Voice Tune and asks you to turn it off for that mic, since Exo Voice would gate, compress and de-ess your voice a second time. _Verified: on screen._
- **Sharp "s" sounds softened.** Exo Voice turns down only a sustained, sharp "s" by 3 to 4 dB; "t", "k", "ch", "sh" and vowels are never touched, and it adds no delay. _Checked in code only._
- **Voice Check.** In Voice & Video, record a few seconds of yourself and play it back before and after, exactly as the call sends you, with your mic's peak, the room's noise before and after, and your voice's level. A mic that clips or is too quiet is named, and one press sets Microphone Volume right. Nothing is kept. _Verified: on screen._
- **Steadier voice on a bad connection.** Discord reports how much of your voice reaches it; Exo now follows that, adding more recovery data to each packet the moment packets go missing and giving those bits back to your voice when the line is clean (instead of a fixed 5%). _Verified: on screen._
- **Voice Focus: only your voice gets through.** After noise suppression, Exo's own voice stage cuts what suppression lets by: a high-pass for desk bumps, rumble and hum; a gate that listens only where voice lives and opens only for sound that lasts like speech, so keyboard and mouse clicks and knocks never open it, with a look-ahead so no word's start is cut; clicks between words ducked; a gentle leveler and a −1 dB limiter so you sound the same loud or soft. On speech mixed with typing, clicks, knocks, a fan and hum it kept the voice within about 1 dB, cut clicks in silence by about 70 dB (suppression alone: about 20), clicks between words by about 34 dB, and fan and hum in pauses by about 26 dB; three seconds of typing alone sent nothing. It costs about 0.02 ms per 20 ms of sound. Part of Exo Voice. The microphone is also coded as mono at Opus's best complexity, so every bit goes to your voice. _Verified: on screen._
- **Your stream beside the call, not over it.** While you Go Live, your stream is a tile among the people in the call, with Hide Preview and Stop Streaming on it; the Share button's menu shows or hides that preview. _Checked in code only._
- **Go Live keeps its frame rate, exactly.** The stream runs on its own clock: a frame exactly every 1/30 or 1/60 s, each the newest picture, whether the screen is still or the monitor runs at 144 Hz, instead of drifting between 48 and 59. _Verified: on screen._
- **The message box is centered.** "Message #chat" and what you type sit in the middle of the box instead of a little high. _Verified: on screen._
- **Settings use the whole window.** Discord's settings pages fill the width, as Exo's Settings do. _Verified: on screen._
- **Your preview plays smoothly.** Your stream's tile plays the very frames your viewers get, at the stream's own rate, instead of a still picture twice a second. _Checked in code only._
- **More room for a sharp stream.** Go Live may now use about 14 Mbit/s at 1080p 60 (Discord's own reference is 9) and up to 25 at higher sizes, and the encoder no longer bursts to four times that when a still screen starts moving and then starves to pay it back. Your tile shows that ceiling ("up to 14.4 Mbps"): a still picture is already perfect at almost nothing, so what it sends then says nothing about quality. _Checked in code only._
- **Threads and search behave like Discord.** A thread opens inside its channel with a breadcrumb back, notifications land on the thread, and Search, Pinned and Inbox have Jump, which opens the message in its conversation with Jump to Present. Search covers the whole server on Enter. _Checked in code only._
- **Every emoji, Discord's way.** The emoji picker has every emoji by category with search and each server by its icon, `:` suggests them as you type, and `:smile:` is sent as the emoji. Mentions read as names in the box and open profiles in messages, and role mentions show the role's name and color. _Verified: on screen._
- **Sending feels instant and safe.** Files show with upload progress the moment you send, drafts stay with each conversation, edits happen in place, and leaving a server or group, removing a friend or deleting a channel now asks first. Sensitive media follows your Content & Social filters. _Verified: on screen._
- **No more reconnect loops.** Exo stops asking Discord about people who aren't in a server (webhooks, people who left), keeps under Discord's rate limit, and waits a minute after Discord closes the connection for asking too fast. _Checked in code only._
- **Server Settings keep unsaved changes the way Discord does.** Changing section, closing or Escape shakes the save bar red instead of throwing your changes away, and Safety Setup waits for Save Changes. A server name must be 2 to 100 characters, said under Name. _Checked in code only._
- **The person menu, fixed.** Roles, Move to and Timeout open on a click and stay open on the way across. Arrow keys move through the menu, quick role ticks no longer undo each other, and Remove Timeout shows only for someone timed out. Bot and Server Booster roles save their color, display and permissions. _Checked in code only._
- **Members on every server.** Members is in Server Settings everywhere and searches and pages through all members. Prune needs Kick Members and Manage Server, Bans need Ban Members and go past 1,000, and stage events are created as stage events. Errors show under the control that failed. _Checked in code only._
- **DM calls like Discord.** A DM's call shows its stage above the conversation, with the call bar, camera, Share Your Screen and Soundboard; Decline tells Discord to stop ringing in your other apps too, and ringing follows Discord's own updates. _Checked in code only._
- **Voice that says why.** Voice Check and Voice Tune play back exactly what the call sends; Voice Tune can be canceled and can forget a mic's tuning; a full channel, a join that never connects, a stream that doesn't start and a camera that won't open each say why. _Checked in code only._
- **Discord rests in the background.** Video, previews and the mic meter rest while Exo isn't in use; a headset unplugged mid-call switches to Windows' device; a dropped or moved voice server no longer ends the call or leaves it hanging. _Checked in code only._
- **Notifications follow your settings.** Exo now notifies as each server and channel is set in Discord (all messages, only mentions or nothing), and respects a server's choice to ignore @everyone and role mentions. _Checked in code only._
- **Lighter on Discord.** Reading a busy channel tells Discord once, a few seconds later, instead of once a message; a server's Interested events are one request; voice changes in a busy server no longer reload your server list or friends. _Checked in code only._
- **Pins, events and files.** Pins load from Discord's current list, Download ICS file never overwrites a file of the same name, and a file too big for your account says so. _Checked in code only._
- **Try Again when Discord drops.** If the connection to Discord stops, the room offers Try Again instead of only saying so. _Checked in code only._
- **Server Settings hold up.** A page that fails to reload keeps what it had and offers Try Again; Delete, Create Role, Add Members, Revoke Ban and the like can't fire twice; Remove from a role takes only that role; Add Members waits for you to stop typing and stops at 30; the Audit Log never mixes two filters; AutoMod's mention limit can be cleared and retyped. _Checked in code only._
- **Settings that stick.** A profile you just saved no longer comes back as unsaved changes, two account switches flipped quickly no longer undo each other, two quick favorite GIFs are both kept, and a privacy change Discord refuses says so. _Checked in code only._
- **Calls hold steady under load.** Exo's voice no longer allocates on its audio threads, closes a call, Voice Check or the mic meter at once, and lets go of your microphone and speakers when it's done. Noise suppression loads in the background. _Checked in code only._
- **No more silent calls.** A speaker who reconnects keeps being heard, a call whose voice link never finished connecting tries again, and a speaker leaving can no longer crash the call. Microphone Volume above 100% no longer clips. _Checked in code only._
- **Video recovers by itself.** A camera or stream that loses a frame asks for a fresh picture until one comes, without flooding the sender, and your own camera no longer smears for viewers when Exo falls behind. _Checked in code only._
- **Deafen means everything.** Deafen silences a Go Live you're watching, a new speaker choice moves the stream's sound too, and a call that rang before you opened Discord shows when you do. _Checked in code only._
- **Private channels stay listed.** In servers that list only the channels you follow, private channels you can see (moderator chats) show again, as in Discord. _Checked in code only._
- **Discord remembers where you were.** A dropped connection no longer sends you back to the server Exo started on; the server, channel and open thread stay. Ctrl+K takes you to a DM or channel even with Discord's settings open, and finds channels too. _Checked in code only._
- **Every channel opens.** Voice and stage channels have their text chat beside the call, and forums list every post, archived ones included, with tags, previews and New Post. _Checked in code only._
- **More of Discord's chat.** Forward, Mark Unread, Create Thread and Copy Message Link in the message menu; forwarded messages, command replies and threads under messages show as in Discord; replies can skip the ping; a new-messages bar and slowmode; the bell mutes for a while and sets what a channel notifies you of, and timed mutes end on time. Chat spacing follows one even rhythm. _Checked in code only._
- **Moderator menus on everyone.** Timeout, Kick, Ban and nickname show on the first open even for people Exo hadn't seen yet, and sit under More on their profile card and full profile. _Checked in code only._
- **Full profiles.** The profile card, the panel beside a DM and the full profile show everything Discord does: banner, decoration, badges, activity, About Me, Member Since, roles, connections, your note, and mutual friends and servers you can press. Badges say what they are on hover. _Checked in code only._
- **Members and roles stay loaded.** A server update or a reconnect no longer leaves a server's member list empty; deleted roles no longer linger. _Checked in code only._
- **Change your banner.** Discord settings > Profiles has Change Banner and Remove Banner with Nitro; without it, your banner color is what people see. _Checked in code only._
- **Voice Check plays back.** Play Before and Play After work again in Voice Check and Voice Tune; the page blocked the take. _Checked in code only._
- **Lucid Voice turned off is off.** Voice & Video no longer says Lucid Voice processes your mic once you turn it off in Lucid, and checks again when you come back to Exo. _Checked in code only._
- **See Exo Voice work.** Voice & Video shows Exo Voice live: your mic's level, what is sent, and every stage running, with the tuning in use. Voice Tune's gate now fits a quiet mic; run it again to apply. _Checked in code only._

### Music
- **Spotify links just work.** Paste any Jam invite (short spotify.link links included) into Jam and you're in, and a Spotify link sent in Discord opens in Exo's Music room: a Jam joins, a song plays, an album, playlist, artist or show opens. _Checked in code only._
- **Spotify, the whole app.** Create playlists and folders, pin, rename, move and delete from Your library; Edit details with a new cover, public or private, collaborative and Download; song and artist radio, credits and copy link from any song. _Checked in code only._
- **Spotify pages like Spotify's.** An artist's full discography, tour dates, Featuring, Appears on, Discovered on and About; album release facts; episode dates, minutes left and Mark as played; Liked Songs' genre and mood chips; audiobooks; playback speed; Spotify's keyboard shortcuts; Jam by link or QR with the host's switches. Sign-in finishes in your browser and Spotify's window never shows. _Checked in code only._
- **YouTube Music, the whole app.** Explore with New releases, Charts and Moods & genres, search filters, full artist pages, Start mix and Shuffle play, Play next and Add to queue, an editable Up next with Autoplay, thumbs down, the Song/Video switch, Related, and its keyboard keys. _Checked in code only._
- **Your playlists everywhere.** One Edit details sheet for every service (name, description, cover on Spotify, privacy on YouTube Music), Move up and down, and Delete with a confirm; the library adds YouTube's Subscriptions, Podcasts, Uploads and History. _Checked in code only._
- **One way each service is set up.** Audio quality, Autoplay, keyboard shortcuts and the needs-a-subscription screen work the same in Spotify, Apple Music and YouTube Music, each at that service's best. _Checked in code only._
- **Apple Music, all of it.** New and Radio (live Apple Music 1, Hits and Country, stations and top charts), search by kind, and a library with Recently added, Made for you, Music videos and Apple's own sort orders. _Checked in code only._
- **Your Apple Music playlists in Exo.** Make, rename, describe, reorder and delete them; from any song, Suggest less, Add to library, Create station, View credits and Copy link. _Checked in code only._
- **Apple Music pages and Up Next.** Artist pages carry the station, every shelf and About; album pages show their formats, release facts and more from the artist; Up Next has AutoPlay, Playing next and History; lyrics light word by word. It plays at the web player's best, AAC 256 kbps. _Checked in code only._
- **The player holds still.** Music's floating player keeps one size and place: opening Now playing, Queue or Sound no longer squeezes and moves it, the panels float above it at a roomier width, and Lossless sits under the artist instead of eating the title. Lyrics are bigger and bolder. _Verified: on screen._
- **Sound settings checked.** AutoMix and the rest are Spotify's own settings, read back after each change (AutoMix only applies to playlists Spotify allows it on). _Verified: on screen._
- **Ready when you are.** The music service you chose starts with Exo, hidden, instead of when Music first opens: Spotify was ready about 5 seconds after launch instead of up to a minute. An idle player is frozen, so it costs memory, not processor time. While the first page loads, Music says so instead of showing black. _Verified: on screen._
- **Sound.** A new panel in the player bar. On Spotify: audio quality up to Lossless (a Lossless badge shows by the song), exclusive mode for bit-perfect sound, crossfade and its length, gapless, AutoMix, Smart Shuffle, volume normalization, mono audio and Spotify's six-band equalizer, all Spotify's own settings. For every service: a sleep timer. _Verified: on screen._
- **Your queue, editable.** Play next in every song's menu, and the songs you queued can be moved up or down, removed, or cleared. _Verified: on screen._
- **Apple Music stays on the web.** The Windows Apple Music app can't be controlled from outside (no way to reach its library, queue or quality), so lossless on Apple Music isn't possible in Exo yet; the web player stays. _Checked in code only._
- **Cider is no longer a music choice.** Apple Music plays in Exo's own room through Apple's web player, so Cider isn't needed; if you had chosen Cider, Exo moves you to Apple Music. _Checked in code only._

- **The top bar's cover stays put, and is always there.** The mini player keeps the song's cover the whole song, and the next song's cover fades in over the last only once it has loaded, never over black. Covers no longer go missing: Spotify's 640 px picture was downloaded but then dropped while being prepared, and a song restored paused now finds its cover from Spotify too. _Verified: on screen._
- **The song keeps its size.** Skipping songs no longer makes the mini player grow and shrink with each title. _Verified: on screen._
- **The top bar holds still.** One row of evenly spaced capsules: Search, the call (or a download), the rooms, the song, the mouse, then Tools. The room tabs never move; each live capsule slides down into its own place when it starts and back up when it ends. The song is shorter: its line alone, no numbers. _Checked in code only._
- **The call, from anywhere.** Outside Discord, the call's capsule shows the server's icon, the channel over the server, the faces of who is in it (a green ring while someone speaks), LIVE while you stream, how long the call has run, and mute, deafen and leave. _Verified: on screen._
- **AutoMix everywhere, and Smart Shuffle that works.** Spotify's AutoMix only mixes playlists it picks; turning it on in Exo now also crossfades every other song, so every change is smooth. The Smart Shuffle switch now really turns Smart Shuffle on or off for what is playing (it was only Spotify's "list it as a play mode" setting); where Spotify doesn't offer it (playlists you don't own), it says so. _Verified: on screen._
- **Discord's server list lines up.** The Direct messages logo's top meets the top of the channel panel beside it. _Verified: on screen._
- **Your mouse's profile in the top bar.** A capsule right of the song shows the mouse profile on now (from Exo Mouse; the mouse's name when no saved profile matches) and its battery, red when low. It opens Devices. _Checked in code only._
- **Settings look like Discord's.** A plain list of sections, and each section a title over rows on the black, split by hairlines and using the window's width, as in Discord's settings. _Checked in code only._
- **Lyrics keep time, and Music keeps your place.** Lyrics follow the song, a page that fails says so instead of showing the last one, coming back to Music keeps the page you were on, and song menus say when something didn't work. _Checked in code only._
- **More of each service.** YouTube Music can save an album and follow an artist; on Apple Music, clicking a song in the queue plays it and Play next works. _Checked in code only._
- **Spotify when it was open first.** The room works when Spotify was already running before Exo, Home loads after Exo's theme rewrite, and hidden players rest instead of working in the background. _Checked in code only._
- **Music says when it didn't work.** A play, skip, shuffle, repeat or like the service turns down reads in red on the player strip, and so do failed queue edits and Ctrl+K's music commands. _Checked in code only._
- **Apple Music wakes on the way in.** Pointing at the mini player or the Music tab wakes a sleeping Apple Music page, as it already did for YouTube Music. Spotify covers that couldn't be found are tried again a minute later. _Checked in code only._
- **Reduce motion means Reduce motion.** Exo's own Reduce motion setting now also stops the top bar capsules' slide, the Ctrl+K palette's scale and the loading spinners. Pressing Ctrl+K again closes the palette with its fade. _Checked in code only._
- **Music comes back by itself.** When Spotify was closed or lost Exo's connection (it handed itself to an older Spotify still closing), Music said to close Spotify and Open again did nothing; Exo now starts it again on its own. _Verified: on screen._
- **One size for every tab bar and toolbar.** The Store, System and Discord's Friends and Shop headers use the same filled tab group (28px tabs in a 32px pill), and the Store's search, refresh and arrows match Exo's 32px controls. _Verified: on screen._

### System
- **NVIDIA updates wait for the game.** A driver update waits until you stop playing, and Exo checks for one at most once a day. _Checked in code only._
- **Display settings that tell the truth.** GPU > Display no longer hangs, Best only reports a color the screen took, Default color resets, and a scaling error says why. _Checked in code only._
- **Display changes you can trust.** A refresh rate on trial is saved only once you keep it, a revert puts things back in the right order, and HDR and your calibration profile come back after one. _Checked in code only._
- **System never waits on Windows.** Shader status, the DLSS indicator's administrator prompt and the upscaler folder check run off Exo's main thread, and Exo Shaders and Exo Capture start once even when two parts of Exo ask for them together. _Checked in code only._
- **Shaders tells you how long.** Before you press Compile, the whole queue's time and finish show, from how fast this PC compiled each game before; while compiling, the time left follows the live rate. _Checked in code only._
- **Drag games into the order you want.** Drag a poster to the front, or move it with Ctrl and the arrow keys, and Shaders compiles it first. The compiling poster is never cut off, and compiling keeps full speed in other rooms. _Checked in code only._
- **Hotfix drivers are found again.** System > Graphics offers NVIDIA's newest GeForce hotfix when it's newer than Game Ready, including ones NVIDIA announces only on its forums. _Checked in code only._

### Capture
- **Captures holds still.** The page stays where it is, shows whether the add-on and instant replay are on, Esc keeps your snip edits, and trims work from the keyboard. _Checked in code only._
- **Captures, even when empty.** With nothing saved yet, Captures shows the three ways to capture (Save replay, Record, Snip) with their keys, one press away, instead of one line of text. _Checked in code only._
- **Snip says how.** The snip screen says how it works at the top of the screen you're on (drag, click a window, Enter for the whole screen, Esc to cancel), and the selection's edge stays visible over bright pictures. _Checked in code only._
- **Exo Capture stays installed.** Updating Exo no longer removes the Capture add-on. _Checked in code only._

### Devices
- **Tests on the page.** The polling test and the click test sit beside your mouse's settings, ready to use: click the pad and move the mouse in circles for five seconds to see the average, peak, lowest 5%, stability and dropouts against what is set on the mouse; click the other pad to count each button's clicks and catch a switch that bounces. Nothing on the page moves while a test runs. _Verified: on screen._
- **No empty space.** Settings panels keep their own height and a wide window uses another column instead of an outsized picture, so the room fills the window. _Verified: on screen._

### Fixes
- **Steam stops asking which account.** Add account in Settings clears Steam's automatic sign-in so it can show its own; if no other account signs in, Steam now goes back to signing you in as before instead of asking at its next start. _Checked in code only._
- **Your Discord roles stick.** When Discord sent a server after sign-in, Exo didn't know your roles there, so the moderator menu (Timeout, Kick, Ban, Roles) and role-only channels disappeared. Exo now takes your roles from your member wherever Discord sends it. And Roles, Timeout and Apps stay open while you move into them: crossing the menu's edge or another row on the way no longer closes them at once. _Verified: on screen._
- **Driver installs finish cleanly.** After a new NVIDIA driver goes in, Exo connects to it again before putting your profile and color back (they failed against the removed driver), System says the driver is being installed instead of showing read errors, and Exo no longer promises a Windows permission prompt that may not come. _Checked in code only._
- **Exo opens lighter.** Discord's sticker player, its settings, Server Settings and Edit Channel load when first used, and the top bar no longer pulls the whole Discord client into startup: the script Exo needs before its first paint fell from about 1.6 MB to under 700 KB. _Verified: on screen._
- **A crashed window says so.** If the WebView2 browser behind Exo's window stops, Exo says its window stopped and offers Restart, instead of a message about installing WebView2. _Checked in code only._
- **Notices when you're away.** A download or update that finishes, fails or is canceled while Exo is in the background shows as a Windows notification; never while a game runs. The downloads queue floats in every room again. _Checked in code only._
- **Music's floating player.** Music's player floats over the page as one capsule, and Your library's filters and sort are one menu. _Verified: on screen._
- **Pages open while Exo is in the background.** A room opened while another window had focus could stay blank until you clicked Exo; only spinners and other loops pause in the background now. _Verified: on screen._
- **Music opens without an error.** Right after Exo starts, Spotify could answer Home with an error. Exo now remembers the query Spotify's Home needs for your Spotify version, finds it faster when it must, and asks again for a few seconds before saying anything. _Verified: on screen._
- **Music opens without a sign-in flash.** If you were signed in last time, Music says Opening while the service starts instead of briefly asking you to sign in. _Checked in code only._
- **Room shortcuts work everywhere.** Ctrl+1 to Ctrl+5 and Ctrl+, now switch rooms while you're typing too, so they work from Discord's message box. _Checked in code only._

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
