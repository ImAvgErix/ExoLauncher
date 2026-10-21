/**
 * Discord's permissions as its own settings show them: each bit with Discord's title and
 * description, grouped the way Server Settings > Roles groups them, and the channel wording
 * Edit Channel > Permissions uses. Permissions are 64-bit strings; the math is BigInt calls
 * (inline BigInt literals were miscompiled once, see eslint.config.js).
 */

export const P = {
  CREATE_INSTANT_INVITE: 0, KICK_MEMBERS: 1, BAN_MEMBERS: 2, ADMINISTRATOR: 3, MANAGE_CHANNELS: 4, MANAGE_GUILD: 5,
  ADD_REACTIONS: 6, VIEW_AUDIT_LOG: 7, PRIORITY_SPEAKER: 8, STREAM: 9, VIEW_CHANNEL: 10, SEND_MESSAGES: 11,
  SEND_TTS_MESSAGES: 12, MANAGE_MESSAGES: 13, EMBED_LINKS: 14, ATTACH_FILES: 15, READ_MESSAGE_HISTORY: 16,
  MENTION_EVERYONE: 17, USE_EXTERNAL_EMOJIS: 18, VIEW_GUILD_ANALYTICS: 19, CONNECT: 20, SPEAK: 21, MUTE_MEMBERS: 22,
  DEAFEN_MEMBERS: 23, MOVE_MEMBERS: 24, USE_VAD: 25, CHANGE_NICKNAME: 26, MANAGE_NICKNAMES: 27, MANAGE_ROLES: 28,
  MANAGE_WEBHOOKS: 29, MANAGE_GUILD_EXPRESSIONS: 30, USE_APPLICATION_COMMANDS: 31, REQUEST_TO_SPEAK: 32,
  MANAGE_EVENTS: 33, MANAGE_THREADS: 34, CREATE_PUBLIC_THREADS: 35, CREATE_PRIVATE_THREADS: 36,
  USE_EXTERNAL_STICKERS: 37, SEND_MESSAGES_IN_THREADS: 38, USE_EMBEDDED_ACTIVITIES: 39, MODERATE_MEMBERS: 40,
  USE_SOUNDBOARD: 42, CREATE_GUILD_EXPRESSIONS: 43, CREATE_EVENTS: 44, USE_EXTERNAL_SOUNDS: 45,
  SEND_VOICE_MESSAGES: 46, SET_VOICE_CHANNEL_STATUS: 48, SEND_POLLS: 49, USE_EXTERNAL_APPS: 50, PIN_MESSAGES: 51,
  BYPASS_SLOWMODE: 52,
} as const

const ZERO = BigInt(0)
const bit = (n: number) => BigInt(1) << BigInt(n)
const big = (perms: string | null | undefined) => { try { return BigInt(perms || '0') } catch { return ZERO } }

/** Whether a permission string has this bit. */
export function hasPermission(perms: string | null | undefined, n: number) {
  return (big(perms) & bit(n)) !== ZERO
}

/** The permission string with this bit on or off. */
export function withPermission(perms: string | null | undefined, n: number, on: boolean) {
  return (on ? big(perms) | bit(n) : big(perms) & ~bit(n)).toString()
}

/** An overwrite's state for one bit: allowed, denied, or left to the roles. */
export type Tri = 'allow' | 'neutral' | 'deny'
export function triOf(allow: string, deny: string, n: number): Tri {
  return hasPermission(allow, n) ? 'allow' : hasPermission(deny, n) ? 'deny' : 'neutral'
}
export function withTri(allow: string, deny: string, n: number, state: Tri) {
  return { allow: withPermission(allow, n, state === 'allow'), deny: withPermission(deny, n, state === 'deny') }
}

export type PermissionItem = { bit: number; title: string; description: string }
export type PermissionGroup = { title: string; items: PermissionItem[] }

const ROLE: Record<number, [string, string]> = {
  [P.VIEW_CHANNEL]: ['View Channels', 'Allows members to view channels by default (excluding private channels).'],
  [P.MANAGE_CHANNELS]: ['Manage Channels', 'Allows members to create, edit or delete channels.'],
  [P.MANAGE_ROLES]: ['Manage Roles', 'Allows members to create new roles and edit or delete roles lower than their highest role. Also allows members to change permissions of individual channels that they have access to.'],
  [P.CREATE_GUILD_EXPRESSIONS]: ['Create Expressions', 'Allows members to add custom emojis, stickers and sounds in this server.'],
  [P.MANAGE_GUILD_EXPRESSIONS]: ['Manage Expressions', 'Allows members to edit or remove custom emojis, stickers and sounds in this server.'],
  [P.VIEW_AUDIT_LOG]: ['View Audit Log', 'Allows members to view a record of who made which changes in this server.'],
  [P.VIEW_GUILD_ANALYTICS]: ['View Server Insights', 'Allows members to view Server Insights, which shows data on community growth, engagement and more. This will allow them to see certain data about channel activity, even for channels they cannot access.'],
  [P.MANAGE_WEBHOOKS]: ['Manage Webhooks', 'Allows members to create, edit or delete webhooks, which can post messages from other apps or sites into this server.'],
  [P.MANAGE_GUILD]: ['Manage Server', 'Allow members to change this server’s name, switch regions, view all invites, add apps to this server and create and update AutoMod rules.'],
  [P.CREATE_INSTANT_INVITE]: ['Create Invite', 'Allows members to invite new people to this server.'],
  [P.CHANGE_NICKNAME]: ['Change Nickname', 'Allows members to change their own nickname, a custom name just for this server.'],
  [P.MANAGE_NICKNAMES]: ['Manage Nicknames', 'Allows members to change the nicknames of other members.'],
  [P.KICK_MEMBERS]: ['Kick Members', 'Allows members to remove other members from this server. Kicked members will be able to re-join if they have another invite.'],
  [P.BAN_MEMBERS]: ['Ban Members', 'Allows members to permanently ban and delete the message history of other members from this server.'],
  [P.MODERATE_MEMBERS]: ['Time out members', 'When you put a user in time-out they will not be able to send messages in chat, reply within threads, react to messages or speak in voice or stage channels.'],
  [P.SEND_MESSAGES]: ['Send Messages and Create Posts', 'Allow members to send messages in text channels and create posts in forum channels.'],
  [P.SEND_MESSAGES_IN_THREADS]: ['Send Messages in Threads and Posts', 'Allow members to send messages in threads and in posts on forum channels.'],
  [P.CREATE_PUBLIC_THREADS]: ['Create Public Threads', 'Allow members to create threads that everyone in a channel can view.'],
  [P.CREATE_PRIVATE_THREADS]: ['Create Private Threads', 'Allow members to create invite-only threads.'],
  [P.EMBED_LINKS]: ['Embed Links', 'Allows links that members share to show embedded content in text channels.'],
  [P.ATTACH_FILES]: ['Attach Files', 'Allows members to upload files or media in text channels.'],
  [P.ADD_REACTIONS]: ['Add Reactions', 'Allows members to add new emoji reactions to a message. If this permission is disabled, members can still react using any existing reactions on a message.'],
  [P.USE_EXTERNAL_EMOJIS]: ['Use External Emojis', 'Allows members to use emojis from other servers if they\'re a Discord Nitro member.'],
  [P.USE_EXTERNAL_STICKERS]: ['Use External Stickers', 'Allows members to use stickers from other servers if they\'re a Discord Nitro member.'],
  [P.MENTION_EVERYONE]: ['Mention @everyone, @here and All Roles', 'Allows members to use @everyone (everyone in the server) or @here (only online members in that channel). They can also @mention all roles, even if the role\'s \'Allow anyone to mention this role\' permission is disabled.'],
  [P.MANAGE_MESSAGES]: ['Manage Messages', 'Allows members to delete or remove embeds from messages by other members.'],
  [P.PIN_MESSAGES]: ['Pin Messages', 'Allows members to pin or unpin any message.'],
  [P.BYPASS_SLOWMODE]: ['Bypass Slowmode', 'Allows members to send messages without being affected by slowmode.'],
  [P.MANAGE_THREADS]: ['Manage Threads and Posts', 'Allows members to rename, delete, close and turn on slow mode for threads and posts. They can also view private threads.'],
  [P.READ_MESSAGE_HISTORY]: ['Read Message History', 'Allows members to read previous messages sent in channels. If this permission is disabled, members only see messages sent when they are online. This does not fully apply to threads and forum posts.'],
  [P.SEND_TTS_MESSAGES]: ['Send Text-to-speech Messages', 'Allows members to send text-to-speech messages by starting a message with /tts. These messages can be heard by anyone focused on the channel.'],
  [P.SEND_VOICE_MESSAGES]: ['Send Voice Messages', 'Allows members to send voice messages.'],
  [P.SEND_POLLS]: ['Create Polls', 'Allows members to create polls.'],
  [P.CONNECT]: ['Connect', 'Allows members to join voice channels and hear others.'],
  [P.SPEAK]: ['Speak', 'Allows members to talk in voice channels. If this permission is disabled, members are default muted until somebody with the \'Mute Members\' permission unmutes them.'],
  [P.STREAM]: ['Video', 'Allows members to share their video, Screen Share or stream a game in this server.'],
  [P.USE_SOUNDBOARD]: ['Use Soundboard', 'Allows members to send sounds from server soundboard.'],
  [P.USE_EXTERNAL_SOUNDS]: ['Use External Sounds', 'Allows members to use sounds from other servers if they’re a Discord Nitro member.'],
  [P.USE_VAD]: ['Use Voice Activity', 'Allows members to speak in voice channels by simply talking. If this permission is disabled, members are required to use Push-to-talk. Good for controlling background noise or noisy members.'],
  [P.PRIORITY_SPEAKER]: ['Priority Speaker', 'Allows members to be more easily heard in voice channels. When activated, the volume of others without this permission will be automatically lowered.'],
  [P.MUTE_MEMBERS]: ['Mute Members', 'Allows members to mute other members in voice channels for everyone.'],
  [P.DEAFEN_MEMBERS]: ['Deafen Members', 'Allows members to deafen other members in voice channels, which means they won\'t be able to speak or hear others.'],
  [P.MOVE_MEMBERS]: ['Move Members', 'Allows members to disconnect or move other members between voice channels that the member with this permission has access to.'],
  [P.SET_VOICE_CHANNEL_STATUS]: ['Set Voice Channel Status', 'Allows members to create and edit voice channel status.'],
  [P.USE_APPLICATION_COMMANDS]: ['Use Application Commands', 'Allows members to use commands from applications, including slash commands and context menu commands.'],
  [P.USE_EMBEDDED_ACTIVITIES]: ['Use Activities', 'Allows members to use Activities.'],
  [P.USE_EXTERNAL_APPS]: ['Use External Apps', 'Allows apps that members have added to their account to post messages. When disabled, the messages will be private.'],
  [P.CREATE_EVENTS]: ['Create Events', 'Allows members to create events.'],
  [P.MANAGE_EVENTS]: ['Manage Events', 'Allows members to edit and cancel events.'],
  [P.ADMINISTRATOR]: ['Administrator', 'Members with this permission will have every permission and will also bypass all channel specific permissions or restrictions (for example, these members would get access to all private channels). This is a dangerous permission to grant.'],
}

// Edit Channel's wording where it differs from the server's (a voice channel's own where that differs again).
const CHANNEL: Record<number, [string, string, string?]> = {
  [P.VIEW_CHANNEL]: ['View Channel', 'Allows members to view this channel by default. Disabling this for @everyone will make this channel private.',
    'Allows members to view this channel by default. Disabling this and the \'Connect\' permission for @everyone will make this channel private.'],
  [P.MANAGE_CHANNELS]: ['Manage Channel', 'Allows members to change this channel\'s name, description and text settings. They can also delete the channel.',
    'Allows members to change this channel\'s name, description and voice settings. They can also delete the channel.'],
  [P.MANAGE_ROLES]: ['Manage Permissions', 'Allows members to change this channel\'s permissions.'],
  [P.MANAGE_WEBHOOKS]: ['Manage Webhooks', 'Allows members to create, edit or delete webhooks, which can post messages from other apps or sites, in this channel.'],
  [P.CREATE_INSTANT_INVITE]: ['Create Invite', 'Allows members to invite new people to this server via a direct invite link to this channel.',
    'Allows members to invite new people to this server via a direct invite link to this channel. The recipient will automatically join the voice channel if they have permission to connect.'],
  [P.SEND_MESSAGES]: ['Send Messages', 'Allows members to send messages in this channel.'],
  [P.EMBED_LINKS]: ['Embed Links', 'Allows links that members share to show embedded content in this channel.'],
  [P.ATTACH_FILES]: ['Attach Files', 'Allows members to upload files or media in this channel.'],
  [P.ADD_REACTIONS]: ['Add Reactions', 'Allows members to add new emoji reactions to a message in this channel. If this permission is disabled, members can still react using any existing reactions on a message.'],
  [P.USE_EXTERNAL_EMOJIS]: ['Use External Emojis', 'Allows members to use emojis from other servers in this channel if they\'re a Discord Nitro member.'],
  [P.USE_EXTERNAL_STICKERS]: ['Use External Stickers', 'Allows members to use stickers from other servers in this channel if they\'re a Discord Nitro member.'],
  [P.MENTION_EVERYONE]: ['Mention @everyone, @here and All Roles', 'Allows members to use @everyone or @here (only online members) in this channel. They can also @mention all roles in this channel, even if the role\'s \'Allow anyone to mention this role\' permission is disabled.'],
  [P.MANAGE_MESSAGES]: ['Manage Messages', 'Allows members to delete or remove embeds from messages by other members in this channel.'],
  [P.PIN_MESSAGES]: ['Pin Messages', 'Allows members to pin or unpin any message in this channel.'],
  [P.BYPASS_SLOWMODE]: ['Bypass Slowmode', 'Allows members to send messages in this channel without being affected by slowmode.'],
  [P.READ_MESSAGE_HISTORY]: ['Read Message History', 'Allows members to read previous messages sent in this channel. If this permission is disabled, members only see messages sent when they are online.'],
  [P.SEND_TTS_MESSAGES]: ['Send Text-to-speech Messages', 'Allows members to send text-to-speech messages in this channel by starting a message with /tts. These messages can be heard by anyone focused on this channel.'],
  [P.SEND_VOICE_MESSAGES]: ['Send Voice Messages', 'Allows members to send voice messages in this channel. Requires the ‘Attach Files’ permission as well.'],
  [P.SEND_POLLS]: ['Create Polls', 'Allows members to create polls in this channel.'],
  [P.USE_APPLICATION_COMMANDS]: ['Use Application Commands', 'Allows members to use commands from applications in this channel, including slash commands and context menu commands.'],
  [P.USE_EMBEDDED_ACTIVITIES]: ['Use Activities', 'Allows members to use Activities in this channel.'],
  [P.USE_EXTERNAL_APPS]: ['Use External Apps', 'Allows apps that members have added to their account to post messages in this channel. When disabled, the messages will be private.'],
  [P.CONNECT]: ['Connect', 'Allows members to join this voice channel and hear others.'],
  [P.SPEAK]: ['Speak', 'Allows members to talk in this voice channel. If this permission is disabled, members are default muted until somebody with the \'Mute Members\' permission unmutes them.'],
  [P.STREAM]: ['Video', 'Allows members to share their video, Screen Share or stream a game in this voice channel.'],
  [P.USE_SOUNDBOARD]: ['Use Soundboard', 'Allows members to send sounds from server soundboard in this voice channel.'],
  [P.USE_VAD]: ['Use Voice Activity', 'Allows members to speak in this voice channel by simply talking. If this permission is disabled, members are required to use Push-to-talk. Good for controlling background noise or noisy members.'],
}

const TEXT = [P.SEND_MESSAGES, P.SEND_MESSAGES_IN_THREADS, P.CREATE_PUBLIC_THREADS, P.CREATE_PRIVATE_THREADS, P.EMBED_LINKS, P.ATTACH_FILES,
  P.ADD_REACTIONS, P.USE_EXTERNAL_EMOJIS, P.USE_EXTERNAL_STICKERS, P.MENTION_EVERYONE, P.MANAGE_MESSAGES, P.PIN_MESSAGES, P.BYPASS_SLOWMODE,
  P.MANAGE_THREADS, P.READ_MESSAGE_HISTORY, P.SEND_TTS_MESSAGES, P.SEND_VOICE_MESSAGES, P.SEND_POLLS]
const VOICE = [P.CONNECT, P.SPEAK, P.STREAM, P.USE_SOUNDBOARD, P.USE_EXTERNAL_SOUNDS, P.USE_VAD, P.PRIORITY_SPEAKER, P.MUTE_MEMBERS,
  P.DEAFEN_MEMBERS, P.MOVE_MEMBERS, P.SET_VOICE_CHANNEL_STATUS]
// A voice channel's own text chat, as Edit Channel lists it.
const VOICE_TEXT = [P.SEND_MESSAGES, P.EMBED_LINKS, P.ATTACH_FILES, P.ADD_REACTIONS, P.USE_EXTERNAL_EMOJIS, P.USE_EXTERNAL_STICKERS,
  P.MENTION_EVERYONE, P.MANAGE_MESSAGES, P.BYPASS_SLOWMODE, P.READ_MESSAGE_HISTORY, P.SEND_TTS_MESSAGES, P.SEND_VOICE_MESSAGES, P.SEND_POLLS]
const APPS = [P.USE_APPLICATION_COMMANDS, P.USE_EMBEDDED_ACTIVITIES, P.USE_EXTERNAL_APPS]

const roleItems = (bits: number[]) => bits.map(n => ({ bit: n, title: ROLE[n][0], description: ROLE[n][1] }))

/** Server Settings > Roles > Permissions, in Discord's groups and order. */
export const ROLE_GROUPS: PermissionGroup[] = [
  { title: 'General Server Permissions', items: roleItems([P.VIEW_CHANNEL, P.MANAGE_CHANNELS, P.MANAGE_ROLES, P.CREATE_GUILD_EXPRESSIONS, P.MANAGE_GUILD_EXPRESSIONS, P.VIEW_AUDIT_LOG, P.VIEW_GUILD_ANALYTICS, P.MANAGE_WEBHOOKS, P.MANAGE_GUILD]) },
  { title: 'Membership Permissions', items: roleItems([P.CREATE_INSTANT_INVITE, P.CHANGE_NICKNAME, P.MANAGE_NICKNAMES, P.KICK_MEMBERS, P.BAN_MEMBERS, P.MODERATE_MEMBERS]) },
  { title: 'Text Channel Permissions', items: roleItems(TEXT) },
  { title: 'Voice Channel Permissions', items: roleItems(VOICE) },
  { title: 'Apps Permissions', items: roleItems(APPS) },
  { title: 'Events Permissions', items: roleItems([P.CREATE_EVENTS, P.MANAGE_EVENTS]) },
  { title: 'Advanced Permissions', items: roleItems([P.ADMINISTRATOR]) },
]

/** Edit Channel > Permissions for a text (or forum) channel or a voice one, in Discord's groups. */
export function channelGroups(voice: boolean): PermissionGroup[] {
  const items = (bits: number[]) => bits.map(n => {
    const own = CHANNEL[n]
    return { bit: n, title: own?.[0] ?? ROLE[n][0], description: (voice ? own?.[2] : undefined) ?? own?.[1] ?? ROLE[n][1] }
  })
  const general = { title: 'General Channel Permissions', items: items([P.VIEW_CHANNEL, P.MANAGE_CHANNELS, P.MANAGE_ROLES, P.MANAGE_WEBHOOKS]) }
  const membership = { title: 'Membership Permissions', items: items([P.CREATE_INSTANT_INVITE]) }
  const apps = { title: 'Apps Permissions', items: items(APPS) }
  return voice
    ? [general, membership, { title: 'Voice Channel Permissions', items: items(VOICE) }, { title: 'Text Channel Permissions', items: items(VOICE_TEXT) }, apps,
      { title: 'Events Permissions', items: items([P.CREATE_EVENTS, P.MANAGE_EVENTS]) }]
    : [general, membership, { title: 'Text Channel Permissions', items: items(TEXT) }, apps]
}
