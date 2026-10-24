import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { type DiscordEmojis, type DiscordGuildEmoji } from '../../lib/discord'
import { emojiUrl } from './markdown'
import { Icons } from './icons'
import { findUnicodeEmoji, loadUnicodeEmoji, type UnicodeEmojiSet } from './unicodeEmoji'
import { useDismiss } from '../../lib/dismiss'

export type PickedEmoji = { text: string; reaction: string }

// Cached per server for the run: emoji change rarely, and GUILD_EMOJIS_UPDATE is not worth a refetch here.
const cache = new Map<string, Promise<DiscordEmojis>>()

export function loadEmojis(guildId: string | null) {
  const key = guildId ?? '@me'
  let entry = cache.get(key)
  if (!entry) {
    entry = host.discordEmojis(guildId)
    entry.catch(() => cache.delete(key))
    cache.set(key, entry)
  }
  return entry
}

/** The account's favorites and frequently used emoji, as Discord keeps them: a custom emoji's id, a Unicode one's Discord name. */
type Saved = { favorites: string[]; frequent: string[]; reactions: string[] }
let saved: Promise<Saved> | null = null
const savedOf = (r: { favorites?: string[] | null; frequent?: string[] | null; reactions?: string[] | null }): Saved => ({ favorites: r.favorites ?? [], frequent: r.frequent ?? [], reactions: r.reactions ?? [] })
function loadSaved(): Promise<Saved> {
  saved ??= host.discordEmojiFavorites().then(r => r.ok ? savedOf(r) : Promise.reject(new Error('Discord did not send your favorite emoji.')))
  saved.catch(() => { saved = null })
  return saved
}

/** A reaction offered in one press: a server emoji usable here, or a Unicode one. */
export type QuickReaction = { key: string; name: string; reaction: string; custom?: DiscordGuildEmoji; emoji?: string }

// Discord's own picks when the account has used too few: the hover bar's three, and the menu's start list.
const BAR_PICKS = ['100', 'laughing', 'sparkling_heart']
const MENU_PICKS = ['100', 'thumbsup', 'thumbsdown', 'heart', 'point_up', 'eyes', 'weary', 'laughing', 'white_check_mark', 'x']

/**
 * Discord's quick reactions here: on the hover bar the 3 reactions used most, in the message menu favorites and then
 * those (up to 12). Only emoji usable here count, as in Discord, and Discord's own picks fill the rest.
 */
export function useQuickReactions(guildId: string | null, menu: boolean): QuickReaction[] {
  const [sources, setSources] = useState<[Saved, DiscordEmojis, UnicodeEmojiSet] | null>(null)
  useEffect(() => {
    let live = true
    Promise.all([loadSaved(), loadEmojis(guildId), loadUnicodeEmoji()]).then(all => { if (live) setSources(all) })
      .catch((error: unknown) => report(error, 'EmojiPicker.quick'))
    return () => { live = false }
  }, [guildId])
  return useMemo(() => {
    if (!sources) return []
    const [mine, emojis, unicode] = sources
    const custom = new Map(emojis.guilds.flatMap(g => g.emojis.map(e => [e.id, e] as const)))
    const usable = (key: string): QuickReaction | null => {
      const e = custom.get(key)
      if (e) return e.native ? { key, name: e.name, reaction: `${e.name}:${e.id}`, custom: e } : null
      const char = unicode.byName.get(key)
      return char ? { key, name: key, reaction: char, emoji: char } : null
    }
    const seen = new Set<string>()
    return [...(menu ? mine.favorites : []), ...mine.reactions, ...(menu ? MENU_PICKS : BAR_PICKS)].map(usable)
      .filter((q): q is QuickReaction => q !== null && !seen.has(q.reaction) && !!seen.add(q.reaction)).slice(0, menu ? 12 : 3)
  }, [sources, menu])
}

/** Counts a reaction in the account's frequently used ones, as Discord's picker does. */
export function noteReaction(key: string) {
  void host.discordEmojiFavorites('use', key, true).catch(() => { })
}

/** A quick reaction's emoji at a reaction's size. */
export function QuickEmoji({ quick }: { quick: QuickReaction }) {
  return quick.custom
    ? <img src={emojiUrl(quick.custom.id, quick.custom.animated, 48)} alt="" width={20} height={20} draggable={false} />
    : <span className="exo-dc-quick-char" aria-hidden="true">{quick.emoji}</span>
}

/** A server emoji in a message, as Discord sends it. */
export function emojiText(emoji: DiscordGuildEmoji): string {
  return `<${emoji.animated ? 'a' : ''}:${emoji.name}:${emoji.id}>`
}

const CATEGORY_ICONS: Record<string, (props: { size?: number }) => React.ReactNode> = {
  people: Icons.People, nature: Icons.Nature, food: Icons.Food, activities: Icons.Activities,
  travel: Icons.Travel, objects: Icons.Objects, symbols: Icons.Symbols, flags: Icons.Flags,
}

/** One tile: a Unicode emoji (its character and Discord name) or a server emoji. key is how Discord's favorites name it. */
type Cell = { key: string; name: string; emoji?: string; custom?: DiscordGuildEmoji }

/**
 * Discord's emoji picker: Favorites, Frequently Used, each server's emoji, then every Unicode emoji by category, a search
 * over all, and the one pointed at. A right-click favorites or unfavorites an emoji in the account's own list.
 */
export function EmojiPicker({ guildId, reacting, tabs, onPick, onClose }: {
  guildId: string | null
  /** The composer's GIFs, Stickers and Emoji tabs along the top (Composer.tsx › ExpressionTabs). */
  tabs?: ReactNode
  /** Picking for a reaction: a server emoji works there only where it would in a message (without Nitro, this server's still ones). */
  reacting?: boolean
  onPick: (emoji: PickedEmoji) => void
  onClose: () => void
}) {
  const [data, setData] = useState<DiscordEmojis | null>(null)
  const [unicode, setUnicode] = useState<UnicodeEmojiSet | null>(null)
  const [mine, setMine] = useState<Saved | null>(null)
  const [query, setQuery] = useState('')
  const [hovered, setHovered] = useState<Cell | null>(null)
  const [menu, setMenu] = useState<{ cell: Cell; x: number; y: number } | null>(null)
  const box = useRef<HTMLDivElement>(null)
  const list = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let live = true
    loadEmojis(guildId).then(result => { if (live) setData(result) }).catch((error: unknown) => report(error, 'EmojiPicker.load'))
    loadUnicodeEmoji().then(result => { if (live) setUnicode(result) }).catch((error: unknown) => report(error, 'EmojiPicker.unicode'))
    loadSaved().then(result => { if (live) setMine(result) }).catch((error: unknown) => report(error, 'EmojiPicker.favorites'))
    return () => { live = false }
  }, [guildId])

  useDismiss(box, onClose)
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [onClose])

  const q = query.trim().toLowerCase()
  const groups = useMemo(() => (data?.guilds ?? [])
    .map(g => ({ ...g, emojis: g.emojis.filter(e => !q || e.name.toLowerCase().includes(q)) }))
    .filter(g => g.emojis.length > 0), [data, q])
  const found = useMemo(() => unicode && q ? findUnicodeEmoji(unicode, q, 160) : null, [unicode, q])
  // A saved key as a tile; one from a server you're not in (or Discord no longer has) is left out, as Discord does.
  const cellOf = useMemo(() => {
    const custom = new Map((data?.guilds ?? []).flatMap(g => g.emojis.map(e => [e.id, e] as const)))
    return (key: string): Cell | null => {
      const e = custom.get(key)
      if (e) return { key, name: e.name, custom: e }
      const char = unicode?.byName.get(key)
      return char ? { key, name: key, emoji: char } : null
    }
  }, [data, unicode])
  const resolve = (keys: string[]) => keys.map(cellOf).filter((c): c is Cell => c !== null)
  const own = mine ?? { favorites: [], frequent: [], reactions: [] }
  const favorites = resolve(own.favorites)
  const frequent = resolve(reacting ? own.reactions : own.frequent)

  // A server emoji that can't be used here (another server's, or animated, without Nitro) shows locked, as in Discord.
  const locked = (c: Cell) => !!c.custom && !c.custom.native
  const pick = (c: Cell) => {
    if (locked(c)) return
    void host.discordEmojiFavorites('use', c.key, !!reacting).catch(() => { })
    if (c.custom) onPick({ text: emojiText(c.custom), reaction: `${c.custom.name}:${c.custom.id}` })
    else onPick({ text: c.emoji!, reaction: c.emoji! })
  }
  const setFavorite = (c: Cell, add: boolean) => {
    setMenu(null)
    host.discordEmojiFavorites(add ? 'add' : 'remove', c.key).then(r => {
      if (!r.ok) throw new Error(add ? 'Discord did not save the favorite.' : 'Discord did not remove the favorite.')
      const next = savedOf(r)
      saved = Promise.resolve(next)
      setMine(next)
    }).catch((error: unknown) => report(error, 'EmojiPicker.favorite'))
  }
  const jump = (id: string) => list.current?.querySelector(`[data-section="${id}"]`)?.scrollIntoView({ block: 'start' })

  const tile = (c: Cell) => (
    <button key={c.key} type="button" className={`exo-dc-picker-emoji${locked(c) ? ' is-locked' : ''}`}
      aria-label={`:${c.name}:`} aria-disabled={locked(c) || undefined}
      onPointerEnter={() => setHovered(c)} onClick={() => pick(c)}
      onContextMenu={event => {
        event.preventDefault()
        event.stopPropagation()
        const at = box.current!.getBoundingClientRect()
        setMenu({ cell: c, x: event.clientX - at.left, y: event.clientY - at.top })
      }}>
      <EmojiArt cell={c} />
    </button>
  )
  const section = (id: string, name: string, cells: Cell[], className?: string) => cells.length ? (
    <section key={id} data-section={id} className={className}>
      <p className="exo-dc-picker-head">{name}</p>
      <div className="exo-dc-picker-grid">{cells.map(tile)}</div>
    </section>
  ) : null
  const serverCells = (emojis: DiscordGuildEmoji[]) => emojis.map(e => ({ key: e.id, name: e.name, custom: e }))
  const unicodeCells = (emoji: { emoji: string; name: string }[]) => emoji.map(e => ({ key: e.name, name: e.name, emoji: e.emoji }))

  return (
    <div className={`exo-dc-picker is-emoji ${reacting ? 'is-below' : 'is-above'}`} ref={box} role="dialog" aria-label="Emoji" onPointerLeave={() => setHovered(null)}>
      {tabs}
      <input className="exo-dc-picker-search" placeholder="Find the perfect emoji" value={query} autoFocus
        onChange={event => setQuery(event.target.value)} aria-label="Find emoji" />
      <div className="exo-dc-picker-body">
        {q ? null : <PickerNav favorites={favorites.length > 0} frequent={frequent.length > 0} groups={groups} unicode={unicode} onJump={jump} />}
        <div className="exo-dc-picker-list" ref={list}>
          {q ? null : section('favorites', 'Favorites', favorites)}
          {q ? null : section('frequent', 'Frequently used', frequent)}
          {groups.map(g => section(g.id, g.name, serverCells(g.emojis)))}
          {found ? (
            <div className="exo-dc-picker-grid">{unicodeCells(found).map(tile)}</div>
          ) : unicode?.categories.map(c => section(c.id, c.name, unicodeCells(c.emoji), 'exo-dc-picker-section'))}
          {!unicode && !q ? <p className="exo-dc-note">Loading emoji</p> : null}
          {q && unicode && data && groups.length === 0 && found?.length === 0 ? <p className="exo-dc-note">No emoji called that.</p> : null}
        </div>
      </div>
      <div className="exo-dc-picker-foot" aria-live="polite">
        {hovered ? <><EmojiArt cell={hovered} /><span>:{hovered.name}:</span></> : null}
      </div>
      {menu ? (
        <FavoriteMenu at={menu} favorite={own.favorites.includes(menu.cell.key)} onClose={() => setMenu(null)} onFavorite={add => setFavorite(menu.cell, add)} />
      ) : null}
    </div>
  )
}

// The rail down the side: Favorites, Frequently Used, each server, then each category, as Discord's.
function PickerNav({ favorites, frequent, groups, unicode, onJump }: { favorites: boolean; frequent: boolean; groups: DiscordEmojis['guilds']; unicode: UnicodeEmojiSet | null; onJump: (id: string) => void }) {
  return (
    <nav className="exo-dc-picker-nav" aria-label="Emoji categories">
      {favorites ? <button type="button" aria-label="Favorites" onClick={() => onJump('favorites')}><Icons.Star size={18} /></button> : null}
      {frequent ? <button type="button" aria-label="Frequently used" onClick={() => onJump('frequent')}><Icons.Recent size={18} /></button> : null}
      {groups.map(g => (
        <button key={g.id} type="button" aria-label={g.name} onClick={() => onJump(g.id)}>
          {g.icon ? <img className="exo-dc-picker-guild" src={g.icon} alt="" draggable={false} /> : <span>{[...g.name][0]}</span>}
        </button>
      ))}
      {unicode?.categories.map(c => {
        const Icon = CATEGORY_ICONS[c.id]
        return <button key={c.id} type="button" aria-label={c.name} onClick={() => onJump(c.id)}>{Icon ? <Icon size={18} /> : null}</button>
      })}
    </nav>
  )
}

/** An emoji the size of a server emoji's picture: the Unicode character drawn as messages draw it, or the picture. */
function EmojiArt({ cell }: { cell: Cell }) {
  return cell.custom
    ? <img src={emojiUrl(cell.custom.id, cell.custom.animated, 96)} alt="" loading="lazy" draggable={false} />
    : <span className="exo-dc-picker-char">{cell.emoji}</span>
}

/** Favorite or Unfavorite, on a right-click in the emoji and sticker pickers. */
export function FavoriteMenu({ at, favorite, onClose, onFavorite }: { at: { x: number; y: number }; favorite: boolean; onClose: () => void; onFavorite: (add: boolean) => void }) {
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  return (
    <div ref={box} className="exo-dc-menu exo-dc-picker-menu" role="menu" style={{ left: at.x, top: at.y }}>
      <button type="button" role="menuitem" autoFocus onClick={() => onFavorite(!favorite)}>
        <Icons.Star size={16} />{favorite ? 'Unfavorite' : 'Favorite'}
      </button>
    </div>
  )
}
