# Exo's own Discord client

The Discord room is Exo's client; Exo no longer hosts, installs or patches the Discord desktop app, and there is no setting to switch between them. Text first: servers, channels, DMs, messages (send, reply, edit, delete), read marks and typing. Calls, video and screen share stay in the Discord app until a later stage.

## Rules it keeps

- Signs in only by QR code (Discord's phone app confirms); Exo never takes a password.
- The token is DPAPI-wrapped in `discord-client.bin` (current user only), sent only to discord.com and the gateway, never to the UI, never logged. Sign out calls `POST /auth/logout`, then deletes the file.
- Acts only on what the user does in the room. No automation, nothing on a timer for anyone else, no captcha solving: a captcha reply becomes a plain line telling the user to use the Discord app once.
- Channel visibility is computed in the host (`DiscordCache`) with Discord's permission rules: base from @everyone plus the user's roles, Administrator or owner sees all, then the channel's @everyone, role and member overwrites. A channel without View Channel never reaches the UI, nor do its messages or typing (`DiscordCacheTests`). No deleted-message logging: a delete removes the message.
- Messages are held only in the open room's memory; nothing is written to disk.
- No Nitro entitlement spoofing.

## Protocol (as Discord's web client)

- Identity: Chrome on Windows (`DiscordRest.Properties`), the User-Agent from the installed WebView2's major version, and `client_build_number` read from discord.com's own scripts at start (fallback constant).
- QR sign-in: `wss://remote-auth-gateway.discord.gg/?v=2`. Exo sends `init` with an RSA-2048 SPKI key, answers `nonce_proof` with base64url(SHA-256(decrypted nonce)), shows `https://discord.com/ra/<fingerprint>`, then trades the `pending_login` ticket at `POST /users/@me/remote-auth/login` for `encrypted_token` (RSA-OAEP SHA-256). The key exists only for that sign-in.
- Gateway: `wss://gateway.discord.gg/?encoding=json&v=9`, no compression, capabilities `DEDUPE_USER_OBJECTS` (1<<4) so READY carries `users`, `recipient_ids` and `merged_members`. Heartbeat with jitter, zombie detection (no ACK means reconnect), resume through `resume_gateway_url`, a fresh identify after op 9 with `d: false` or close 4007/4009, sign-out on 4004. Opening a server channel sends op 37 (bulk guild subscriptions) for live messages in large servers; if the gateway answers with 4002 right after, Exo stops sending it.
- REST: `discord.com/api/v9` for messages (50 per page), send (`nonce` snowflake, `message_reference` for replies), edit, delete, ack (`last_viewed` as days since 2015-01-01) and typing. 429s under 10 s are waited out once or twice.
- Notifications (`DiscordCache.Notifies`, after the web client's `shouldNotify` in its NotificationStore): never your own message, a `SUPPRESS_NOTIFICATIONS` (@silent) one, a blocked person's, a message request's, or anything under Do Not Disturb, and each message once (a resume or a message older than the read mark never notifies). DMs notify of every message unless muted; servers follow mutes (server, category, channel, a thread's channel), opt-in channels and the resolved level, with a voice channel's chat at All Messages only while in that call. `DiscordNotifier.Route`: nothing for the conversation on screen while Exo is in front, Exo's banner (`discord.notify`) while in front, a Windows notification (tag = message id, group = channel id, the avatar as a local PNG) in the background. MESSAGE_ACK, a local ack, opening the conversation and Exo coming to the front clear the channel (`discord.notifyClear` and `RemoveByGroupAsync`), as Discord does on MESSAGE_ACK, CHANNEL_SELECT and WINDOW_FOCUS; MESSAGE_DELETE removes that one message's.

## Checked

- 2026-10-17: the QR handshake ran against Discord live and showed a code. The signed-in parts were checked in code and unit tests only (no account signed in while this was built).

## Voice (built 2026-10-17)

- Joining: main gateway op 4 `{guild_id, channel_id, self_mute, self_deaf, self_video: false, flags: 2}`; Discord answers `VOICE_STATE_UPDATE` (this session's voice `session_id`) and `VOICE_SERVER_UPDATE` (`token`, `endpoint`; for a DM call `channel_id` stands in for the guild). DM calls ring with `POST /channels/{id}/call/ring`; `CALL_CREATE` with this user in `ringing` notifies.
- Voice gateway v8 (`Voice/VoiceConnection.cs`): identify with `max_dave_protocol_version` from libdave, heartbeats `{t, seq_ack}`, resume `{server_id, session_id, token, seq_ack}`, binary DAVE messages `[seq u16][op u8][payload]`. Close 4014/4021/4022 end the call; 4006/4009 start over.
- UDP (`Voice/VoiceTransport.cs`): IP discovery, then RTP (payload type 120, Opus 48 kHz stereo, 20 ms) under `aead_aes256_gcm_rtpsize` (preferred) or `aead_xchacha20_poly1305_rtpsize` (required; XChaCha20 as HChaCha20 plus .NET's ChaCha20-Poly1305). The RTP header, CSRCs and extension preamble are the additional data; a big-endian 32-bit counter is appended as the nonce.
- DAVE (`Voice/DaveSession.cs`): a C# port of libdave's reference `DaveSessionManager` over its C API (`dave.dll`); only users announced by op 11 are recognized for MLS adds.
- Sound (`Voice/VoiceAudio.cs`): shared-mode WASAPI on the default communications devices, event driven, Windows converting to 48 kHz stereo float. Voice detection follows a noise floor (12 dB above it, at least -55 dBFS, 300 ms hold). Five Opus silence frames before speaking stops. Each speaker gets a jitter buffer that starts at 60 ms and drops past 200 ms.
- Noise suppression (`Voice/NoiseSuppressor.cs`): DeepFilterNet 3 through the project's own Windows LADSPA plugin, mono at 48 kHz.
- Natives: `voice\build.ps1` builds `dave.dll` (libdave at a pinned commit, mlspp and OpenSSL 3 static, Visual Studio's vcpkg in manifest mode at libdave's baseline) and `opus.dll`, and fetches `deep_filter.dll` pinned by SHA-256.
- Checked offline: transport encryption round trips and tamper rejection in both modes, the HChaCha20 vector, Opus, libdave key packages, microphone capture at 50 frames a second, DeepFilterNet taking white noise down by more than 10 dB faster than real time. A real call has not been made: that needs a signed-in account and a second person.

## Video and screen share (not built yet)

Discord accepts video only over WebRTC (a voice gateway `select_protocol` with `protocol: "webrtc"`, the local SDP in `data`/`sdp`, and an op 4 answer whose `sdp` gives only the ICE ufrag, password, fingerprint, candidate and port; clients build the remote answer themselves). Go Live is its own media connection: main gateway op 18 `STREAM_CREATE {type, guild_id, channel_id, preferred_region}` to share, op 20 `STREAM_WATCH {stream_key}` to watch, then `STREAM_CREATE` (`rtc_server_id`) and `STREAM_SERVER_UPDATE` (`endpoint`, `token`) for a voice-gateway connection whose `server_id` is `rtc_server_id` and whose DAVE group id is `rtc_server_id - 1`. Video identify adds `video: true` and a `streams` entry (`type: "video"`, `rid: "100"`, `quality: 100`); senders announce op 12 `{audio_ssrc, video_ssrc, rtx_ssrc, streams}`. Reference: dank074/Discord-video-stream (ISC), `BaseMediaConnection.ts` and `Streamer.ts`.

The plan: keep calls on the native path above, and run Go Live (sharing and watching) and camera in Exo's page over Chromium's WebRTC (hardware H.264, Windows' screen picker through `getDisplayMedia`, `<video>` playback). Frames go through DAVE with `RTCRtpScriptTransform` and libdave's WebAssembly build (`libdave/js`, built with Emscripten; Discord does not publish it on npm). The host keeps the gateways and relays the SDP and DAVE messages. Build it once text and voice have been seen working with a real account, so each step can be tried against Discord.
