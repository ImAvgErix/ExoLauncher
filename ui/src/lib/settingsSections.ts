/**
 * Settings' sections in the order its section list shows them. Settings is for Exo itself; a tool's options live with
 * the tool (Captures has its own, Lucid is in Devices). `keywords` name the rows each section holds, so a search
 * (Ctrl+K) can land on the section that has them.
 */
export const SETTINGS_SECTIONS = [
  {
    section: 'general',
    label: 'General',
    keywords: ['appearance', 'theme', 'match windows', 'light', 'dark', 'void', 'material', 'glass', 'solid', 'text size', 'reduce motion', 'animation', 'tools and soundtracks'],
  },
  {
    section: 'downloads',
    label: 'Downloads',
    keywords: ['install folder', 'download hours', 'pause downloads while playing', 'update games automatically', 'chipset driver'],
  },
  {
    section: 'accounts',
    label: 'Accounts and keys',
    keywords: [
      'game stores', 'sign in', 'account', 'switch account', 'steam', 'epic', 'gog', 'amazon', 'riot', 'ea', 'ubisoft', 'battle.net', 'xbox',
      'steam overlay', 'notifications and sounds', 'hours from all my accounts', 'get another store',
      'music', 'music service', 'spotify', 'apple music', 'youtube music',
      'optional keys', 'api key', 'steam web api key', 'itch.io', 'steamgriddb', 'riot id', 'xbox time played',
    ],
  },
  {
    section: 'about',
    label: 'About',
    keywords: ['version', 'check for updates', 'install update', 'clear caches', "what's new", 'report a problem', 'privacy', 'support exo'],
  },
] as const

export type SettingsSectionId = (typeof SETTINGS_SECTIONS)[number]['section']
