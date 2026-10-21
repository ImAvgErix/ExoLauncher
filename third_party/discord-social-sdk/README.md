# Discord Social SDK (drop-in)

Exo does not redistribute Discord’s Social SDK. Download the official C++ package from your application’s **Discord Social SDK → Downloads** page:

https://discord.com/developers/applications

Unpack so this folder looks like:

```
third_party/discord-social-sdk/
  include/discordpp.h
  lib/release/discord_partner_sdk.lib
  bin/release/discord_partner_sdk.dll
```

Also in the Discord Developer Portal:

1. Create an application and enable **Discord Social SDK**.
2. OAuth2 redirect `http://127.0.0.1/callback`.
3. Enable **Public Client** (needed for `Client::GetToken` without a game backend). Review that before any public release.
4. Paste the application id in Exo Settings → Keys & IDs.

Then rebuild Exo. The host compiles `exo_discord_social.dll` against those headers and copies both DLLs next to `ExoLauncher.exe`. Connect Discord in Friends runs the official authorize prompt; DMs and 1:1 voice use communication scopes (`Client::GetDefaultCommunicationScopes`). Discord still rate-limits send/call until they grant production comms.

Sources:

- https://docs.discord.com/developers/discord-social-sdk/getting-started/using-c++
- https://docs.discord.com/developers/discord-social-sdk/development-guides/account-linking-with-discord
- https://docs.discord.com/developers/discord-social-sdk/development-guides/sending-direct-messages
- https://docs.discord.com/developers/discord-social-sdk/development-guides/managing-voice-chat
