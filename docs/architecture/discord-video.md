# Discord video: cameras, Go Live and screen share

Exo's Discord client already joins calls the way Discord's desktop app does: the voice gateway (v8),
the UDP leg with Discord's transport encryption, and DAVE end-to-end encryption through Discord's
libdave (`voice/`, `ExoLauncher/Services/DiscordClient/Voice/`). Video uses the same pieces.

## Split

- **Host (C#)**: everything on the wire. Video RTP in and out on the call's UDP leg, H.264
  packetization (RFC 6184: single NAL, STAP-A, FU-A), frame assembly, DAVE encryption and
  decryption of whole frames (libdave handles H.264's unencrypted ranges itself), the voice
  gateway's video ops, and Go Live's own connections.
- **Exo Capture (`capture/`, its own GPL program)**: Go Live's pixels. Exo itself never captures the
  screen (`BannedSymbols.txt`), so the add-on lists pictures for the picker and streams the chosen
  window or screen (below).
- **Page (WebView2, Chromium)**: decoding with WebCodecs `VideoDecoder` (hardware H.264) and drawing
  into the call's tiles; the camera (`getUserMedia`, WebCodecs `VideoEncoder`, hardware H.264, Annex B,
  frames to the host as base64 messages); Go Live's picker, quality and preview
  (`ui/src/components/discord/GoLivePicker.tsx`).

## Go Live: picker, capture and encoder

Discord's own flow, in Exo's look: Share Your Screen opens Exo's picker (Applications and Screens, each
a picture with its title and app icon, the running library game first as the Game shortcut), then the
quality (Resolution 720p, 1080p, 1440p or Source; Frame rate 15, 30 or 60; Stream sound) beside a live
preview, then Go Live. While live the button opens the stream's own picture with its real resolution,
frame rate and bit rate, and Stop Streaming.

- **Listing** (host, `GoLiveSources`): screens by `EnumDisplayMonitors`, windows by `EnumWindows` with
  Alt+Tab's rule (visible, not minimized, not a tool window or another window's dialog unless it asks
  for a taskbar button, not cloaked on another desktop, titled, on a screen), never Exo's own or Exo
  Capture's. A Store app's ApplicationFrameHost frame is named, iconed and heard as the app inside it.
  Icons are the executable's (32 px PNG, cached). Ids are `window:<hwnd>` and `screen:<device>` and are
  looked up again before every use; paths stay native.
- **Pictures** (add-on `stream.thumbs`): one Windows.Graphics.Capture frame per source, all at once
  (about 40 ms for a screen and a window), no cursor and no yellow border (borderless access, which a
  desktop app gets without a prompt), scaled by WIC and sent as JPEG. The picker refreshes them every
  four seconds and the chosen source's preview four times a second. Minimized windows give Windows
  nothing to capture, so they are left out until restored.
- **Streaming** (add-on `stream.start`): Windows.Graphics.Capture of the window or monitor
  (`IGraphicsCaptureItemInterop` CreateForWindow/CreateForMonitor, cursor on, border off), the GPU's
  video processor scales and converts to NV12 on the same D3D11 device, and the GPU's own H.264
  encoder (NVENC, AMF or Quick Sync through Media Foundation) encodes with low latency, no B-frames,
  CBR with a one-second buffer, a key frame every five seconds and on request. The host's
  `GoLivePlan` picks the size (the chosen height, never above the source's, 4096 a side and 4K's
  pixels at most) and the rate (6 Mbit/s at 1080p 30, a third more at 60, within 1 to 10 Mbit/s);
  op 12 announces both. A content resize keeps the stream's size (fitted, black around it).
- **Frames to Discord**: Exo makes an anonymous pipe and duplicates its write end into the add-on
  (`CaptureAddon.ShareHandle`; still only the two processes hold it). Each frame is a 16-byte header
  (u32 length, u32 flags with 1 for a key frame, i64 capture time in 100 ns) and the Annex B frame,
  SPS and PPS in front of every key frame. `GoLiveCapture` reads them and hands them straight to the
  stream's `VoiceConnection` (DAVE, RTP, RTX), timed on the 90 kHz clock from the capture time.
  Discord's PLI (`KeyframeRequested`) becomes `stream.keyframe` (at most four a second). The window
  closing or the add-on stopping ends the stream with that reason.
- **Sound**: a window sends its app's sound only (process loopback INCLUDE_TARGET_PROCESS_TREE on the
  window's process), as Discord does; a screen sends this PC's sound except Exo's (EXCLUDE on Exo).

Why native, not the page: with `getDisplayMedia` and WebCodecs every frame was captured by Chromium,
copied into a `VideoFrame`, encoded in the GPU process, copied back, base64-encoded in JavaScript,
posted as a JSON message to the host and decoded there, on the page's thread and through the
WebView's message pipe; the page could only offer Windows' own picker, and frames stopped when the
page was busy or throttled. In Exo Capture the picture stays on the GPU from capture to encoder (no
CPU copy before the encoder), the encoder is set for streaming (CBR, buffer, key frames on request
without waiting for the next page frame) and the encoded bytes cross one pipe, binary, to the
connection that sends them. It also reuses the capture pipeline Exo Capture already runs for instant
replay. The camera stays in the page: WebView2 already lists the cameras and asks Windows for one,
and a camera frame is small.

## Protocol (seen in the open-source Discord-video-stream, ISC, before its move to WebRTC in December 2025, and in Discord's web client)

- Voice gateway IDENTIFY: `video: true`, `streams: [{ type: "video", rid: "100", quality: 100 }]`.
  READY answers with the streams' `ssrc` and `rtx_ssrc`.
- SELECT_PROTOCOL: `codecs` lists Opus (payload type 120) and H.264 (101, RTX 102), each with
  `encode`/`decode`. Exo offers only H.264 for video, which every Discord client has; SESSION
  DESCRIPTION says the call's `video_codec`.
- Op 12 VIDEO: who sends video on which SSRC (`user_id`, `audio_ssrc`, `video_ssrc`, `rtx_ssrc`,
  `streams`). Sent by Exo to start or stop its own video (`streams: []` to stop).
- Op 15 MEDIA_SINK_WANTS: which video this client wants (`{ "any": 100 }` for all of it).
- Main gateway Go Live: op 18 STREAM_CREATE `{ type, guild_id, channel_id, preferred_region }`,
  op 22 STREAM_SET_PAUSED, op 19 STREAM_DELETE, op 20 STREAM_WATCH `{ stream_key }`. Discord answers
  with STREAM_CREATE (`stream_key`, `rtc_server_id`) and STREAM_SERVER_UPDATE (`endpoint`, `token`).
  The stream is its own voice connection: `server_id` is `rtc_server_id`, the session is the call's,
  and DAVE's group id is `rtc_server_id - 1`. Stream keys are `guild:{guild}:{channel}:{user}` or
  `call:{channel}:{user}`.
- Voice states carry `self_video` (camera on) and `self_stream` (live).
- Video RTP: payload type 101, 90 kHz clock, marker bit on a frame's last packet.

## Phases

1. Watch (built): cameras in a call and Go Live streams (receive, decrypt, decode, draw). Live and camera
   marks on people in voice.
2. Go Live (built): share a screen, window or game from Exo's own picker (capture and encode in Exo
   Capture, encrypt, packetize, send), with Discord's quality choices (resolution, frame rate, sound).
3. Camera (built): the same path from `getUserMedia`.

## Not yet

- Simulcast (several qualities at once) and Discord's own bandwidth estimate; the encoder keeps the chosen bitrate.
- A game in exclusive full screen that gives Windows.Graphics.Capture no picture (instant replay falls
  back to its game hook there; Go Live doesn't yet).
- Changing the source or quality while live (stop and go live again).
