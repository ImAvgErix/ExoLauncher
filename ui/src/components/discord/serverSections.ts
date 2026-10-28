import type { DiscordModeration } from '../../lib/discord'

/** Server Settings' sections, shared by the settings themselves and the server menu's Server Settings submenu. */
export type Section = 'profile' | 'tag' | 'engagement' | 'boost' | 'emoji' | 'stickers' | 'soundboard' | 'members' | 'roles' | 'invites' | 'access'
  | 'integrations' | 'directory' | 'safety' | 'automod' | 'audit' | 'bans' | 'community' | 'onboarding' | 'insights' | 'welcome' | 'subscriptions' | 'template'
/** What a section's check reads of the server: its feature flags (COMMUNITY, GUILD_SERVER_GUIDE, ...). */
export type Features = { features: string[] } | null
type Item = { id: Section; label: string; shown: (m: DiscordModeration, g: Features) => boolean }

const community = (g: Features) => !!g?.features.includes('COMMUNITY')

// Discord's Server Settings list (its generateSections): its groups and order, each section with the permission Discord
// asks for, in Exo's sentence case.
export const NAV: { heading: string | null; items: Item[] }[] = [
  {
    heading: null, items: [
      { id: 'profile', label: 'Server profile', shown: () => true },
      { id: 'tag', label: 'Server tag', shown: m => m.manageGuild },
      { id: 'engagement', label: 'Engagement', shown: () => true },
      { id: 'boost', label: 'Boost perks', shown: () => true },
    ],
  },
  {
    heading: 'Expression', items: [
      { id: 'emoji', label: 'Emoji', shown: m => m.manageExpressions },
      { id: 'stickers', label: 'Stickers', shown: m => m.manageExpressions },
      { id: 'soundboard', label: 'Soundboard', shown: m => m.manageExpressions },
    ],
  },
  {
    heading: 'People', items: [
      { id: 'members', label: 'Members', shown: m => m.kick || m.ban || m.timeout || m.manageRoles || m.manageGuild },
      { id: 'roles', label: 'Roles', shown: m => m.manageRoles },
      { id: 'invites', label: 'Invites', shown: m => m.manageGuild },
      { id: 'access', label: 'Access', shown: m => m.manageGuild },
    ],
  },
  {
    heading: 'Apps', items: [
      { id: 'integrations', label: 'Integrations', shown: m => m.manageGuild || m.manageWebhooks },
      { id: 'directory', label: 'App directory', shown: m => m.manageGuild },
    ],
  },
  {
    heading: 'Moderation', items: [
      { id: 'safety', label: 'Safety setup', shown: m => m.manageGuild },
      { id: 'audit', label: 'Audit log', shown: m => m.auditLog },
      { id: 'bans', label: 'Bans', shown: m => m.ban },
      { id: 'automod', label: 'AutoMod', shown: m => m.manageGuild },
    ],
  },
  {
    heading: 'Community', items: [
      { id: 'community', label: 'Overview', shown: m => m.manageGuild },
      { id: 'onboarding', label: 'Onboarding', shown: (m, g) => m.manageGuild && m.manageRoles && community(g) },
      { id: 'insights', label: 'Server insights', shown: (m, g) => (m.insights === true || m.manageGuild) && community(g) },
      // A server with the Server Guide has it instead, as Discord lists them.
      { id: 'welcome', label: 'Welcome screen', shown: (m, g) => m.manageGuild && community(g) && !g?.features.includes('GUILD_SERVER_GUIDE') },
      { id: 'subscriptions', label: 'Server subscriptions', shown: m => m.owner },
    ],
  },
  { heading: null, items: [{ id: 'template', label: 'Server template', shown: m => m.manageGuild }] },
]
