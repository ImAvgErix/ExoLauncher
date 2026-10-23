import type { ComponentType } from 'react'
import { Appearance, Chats, Cover, EyeOff, Folder, FolderOpen, Gift, GraphicsCard, Hash, Live, Mic, Mouse, Shaders, Sliders, Volume, type IconProps } from '../brand/icons'

/**
 * What's New: after an update, Exo's first opening shows the running version's items once (WhatsNewSheet), and
 * Settings › About › What's new shows them again. Each release adds its own here, newest first, beside its CHANGELOG
 * entry: a plain-words title and one short sentence on what it does for you.
 */
export type WhatsNewItem = { icon: ComponentType<IconProps>; title: string; detail: string }
export type WhatsNewRelease = { version: string; items: WhatsNewItem[] }

/** The window event Settings › About sends to open the sheet. */
export const WHATS_NEW = 'exo-whats-new'

export const RELEASES: WhatsNewRelease[] = [
  {
    version: '3.14.0',
    items: [
      { icon: Mic, title: 'Clearer voice', detail: "Fans, desk knocks and keyboard clicks are taken out of your mic, and your words aren't cut off." },
      { icon: Volume, title: 'Everyone at one volume', detail: "Each person in a call comes through at about the same loudness, so one loud mic doesn't drown out the rest." },
      { icon: Mic, title: 'Noise suppression like Discord’s', detail: 'Pick Exo AI, Standard or None in Voice & Video, and Exo tells you when your mic sends nothing.' },
      { icon: GraphicsCard, title: 'Driver updates on Radeon and Intel', detail: 'System keeps your AMD or Intel graphics driver up to date in one click, as it does on a GeForce.' },
      { icon: Appearance, title: 'Clearer glass', detail: 'Menus, sheets and buttons are clear glass now: the colors behind them show through, and their edges catch the light.' },
      { icon: Sliders, title: 'Your PC, checked', detail: "System shows what's slowing your PC down, gives your PC a power plan tuned for its processor, and keeps your AMD or Intel chipset driver up to date." },
      { icon: Sliders, title: 'Interrupts in one press', detail: "System › This PC can move your graphics card, USB and network to MSI mode, so they don't wait on each other. Undo puts them back." },
      { icon: GraphicsCard, title: "Exo's profile on Radeon and Intel", detail: 'An AMD Radeon or an Intel Arc gets the same one-press tuning as a GeForce, FSR 4 in FSR 3.1 games on a Radeon, and frame generation for the games you pick.' },
      { icon: Chats, title: 'Discord stays fast', detail: 'Messages and profiles load instantly, even right after you switch windows.' },
      { icon: Live, title: 'Go Live your friends can watch', detail: "Your stream plays smoothly on Discord's app, in sharper H.265 when everyone watching can play it." },
      { icon: Live, title: "Watch friends' streams", detail: "Open a friend's Go Live and watch it right in Exo." },
      { icon: Sliders, title: 'Lighter on your PC', detail: 'Calls with clips on and the streams you watch take far less of your processor and graphics card.' },
      { icon: Hash, title: 'Discord, done right', detail: 'Every channel opens, chats open at the newest message, and links show as previews.' },
      { icon: Appearance, title: 'Light, Dark and Void', detail: 'Choose your look in Settings › General. Void is pure black, for OLED screens.' },
      { icon: Gift, title: 'Gifts in your Library', detail: 'Games friends give you on Steam or Epic show in your Library, ready to accept in one click. Sign in to Steam once in Settings › Game stores.' },
      { icon: FolderOpen, title: 'Folders you can peek into', detail: 'Hover a folder and its games rise out of it.' },
      { icon: Folder, title: 'Folders your way', detail: 'Sort each folder its own way, put a game in more than one folder, and give a folder a picture of your own.' },
      { icon: EyeOff, title: 'Hide games', detail: "Right-click a game and choose Hide to keep it out of your Library. Hidden games in the sort menu shows them again." },
      { icon: Cover, title: 'Sharper covers', detail: "Game covers in your Library look as crisp as on the game's own page." },
      { icon: Shaders, title: 'Fewer shader stutters', detail: "Compiled shaders are kept on NVIDIA's newest drivers instead of being thrown away." },
      { icon: Mouse, title: 'More mice', detail: 'Exo Mouse supports 40 more SteelSeries mice and keeps working after your PC sleeps.' },
    ],
  },
]

/** The running version's notes while they are unseen; none for a version without notes. */
export function unseenRelease(version: string, seen: string | null | undefined): WhatsNewRelease | null {
  return seen === version ? null : RELEASES.find(release => release.version === version) ?? null
}
