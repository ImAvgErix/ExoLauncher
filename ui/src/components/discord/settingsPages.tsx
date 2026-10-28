import { useState } from 'react'
import type { DiscordChannel } from '../../lib/discord'
import { notify } from '../../lib/notify'
import { Choice } from '../settings/Choice'
import { Switch } from '../Switch'
import { admin, Confirm, dropUpload, Field, pickFile, ReadError, useAdmin } from './admin'
import { uploadFile } from './Chat'
import { BadgeArtwork, tagBadgeUrl, type BadgeArt } from './guildTag'
import { ErrorNote } from '../ui/ErrorNote'

/**
 * Server Settings pages Discord has beyond the server's own fields, each as Discord's (read from its web client) in
 * Exo's look: Server tag, Boost perks, Soundboard, Access, Server template, Widget, Custom invite link, Welcome screen,
 * Community, Server subscriptions and App directory. Discord answers for what a server may have (boost level,
 * features); a page says plainly when Discord doesn't offer it here.
 */

export type PageGuild = {
  id: string; name: string; features: string[]; premium_tier: number; premium_subscription_count?: number
  rules_channel_id?: string | null; public_updates_channel_id?: string | null
}

const textChannels = (channels: DiscordChannel[]) => channels.filter(c => c.kind === 'text' && c.type === 0).map(c => ({ value: c.id, label: `#${c.name}` }))
const has = (guild: PageGuild, feature: string) => guild.features.includes(feature)

// Discord's 41 badges (its preset numbers) and its 13 color pairs, as its Server Tag editor lists them.
const BADGES = ['Sword', 'Water drop', 'Skull', 'Toadstool', 'Moon', 'Lightning', 'Leaf', 'Heart', 'Fire', 'Compass', 'Crosshairs', 'Flower', 'Force', 'Gem', 'Lava',
  'Psychic', 'Smoke', 'Snow', 'Sound', 'Sun', 'Wind', 'Bunny', 'Dog', 'Frog', 'Goat', 'Cat', 'Diamond', 'Crown', 'Trophy', 'Money bag', 'Dollar sign', 'Clover',
  'Blossom', 'Potted plant', 'Maple', 'Wilted flower', 'Butterfly', 'Snail', 'Caterpillar', 'Spider', 'Bee']
// Each pack's badges, which need its pack (a server feature).
const PACKS: Record<string, number[]> = {
  GUILD_TAGS_BADGE_PACK_PETS: [21, 22, 23, 24, 25], GUILD_TAGS_BADGE_PACK_FLEX: [26, 27, 28, 29, 30],
  GUILD_TAGS_BADGE_PACK_PLANT: [31, 32, 33, 34, 35], GUILD_TAGS_BADGE_PACK_CREEPY_CRAWLIES: [36, 37, 38, 39, 40],
}
const COLORS = [['#ff1c90', '#ff7fc0'], ['#ff8f1c', '#ffae77'], ['#eebe1a', '#fffc7f'], ['#32a070', '#57b59e'], ['#32839a', '#71c2d9'], ['#8a43ff', '#bd95ff'],
  ['#9b3fe5', '#cc8dff'], ['#942e8f', '#d46cb5'], ['#d14242', '#ff8989'], ['#814300', '#a88a6c'], ['#717224', '#c3c3c3'], ['#5d1d47', '#c58fbb'], ['#222222', '#cccccc']]

type Profile = { tag?: string | null; badge?: number | null; badge_color_primary?: string | null; badge_color_secondary?: string | null; badge_hash?: string | null }

const savedColors = (profile: Profile | null) => [profile?.badge_color_primary ?? COLORS[0][0], profile?.badge_color_secondary ?? COLORS[0][1]]

/** Discord's Server Tag: the tag members can wear (2 to 4 letters), its badge and colors; Discord gives it with 3 Boosts. */
export function ServerTag({ guild }: { guild: PageGuild }) {
  const profile = useAdmin<Profile>('profile', { guildId: guild.id })
  const [tag, setTag] = useState<string | null>(null)
  const [badge, setBadge] = useState<number | null>(null)
  const [colors, setColors] = useState<string[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [line, setLine] = useState<{ text: string; bad: boolean } | null>(null)
  const available = has(guild, 'GUILD_TAGS')
  const current = profile.data
  const value = { tag: tag ?? current?.tag ?? '', badge: badge ?? current?.badge ?? 0, colors: colors ?? savedColors(current) }
  const unlocked = BADGES.map((name, i) => ({ value: String(i), label: name })).filter((_, i) => Object.entries(PACKS).every(([pack, ids]) => !ids.includes(i) || has(guild, pack)))
  const dirty = tag !== null || badge !== null || colors !== null
  async function save() {
    setBusy(true); setLine(null)
    const result = await admin('saveProfile', { guildId: guild.id, tag: value.tag.trim(), badge: value.badge, primary: value.colors[0], secondary: value.colors[1] })
    setBusy(false)
    if (!result.ok) { setLine({ text: result.message ?? 'Discord did not save the tag.', bad: true }); return }
    setTag(null); setBadge(null); setColors(null); setLine({ text: 'Saved. Members who wear the tag reapply it to show the change.', bad: false }); profile.reload()
  }
  return (
    <>
      <h1>Server tag</h1>
      <p className="exo-dc-note">A short tag members can show beside their name everywhere on Discord, with a badge in your colors.</p>
      <ReadError error={profile.error} onRetry={profile.reload} />
      {!available ? <p className="exo-dc-note">Server tags come with 3 Boosts. This server can set one once it has them.</p> : null}
      {current?.tag && current.badge_hash ? (
        <Field label="Your tag now">
          <span className="exo-dc-guild-tag is-static"><img src={tagBadgeUrl(guild.id, current.badge_hash)} alt="" width={14} height={14} draggable={false} /><span>{current.tag}</span></span>
        </Field>
      ) : null}
      <TagEditor guildId={guild.id} value={value} unlocked={unlocked} locked={!available} dirty={dirty} busy={busy} onTag={setTag} onBadge={setBadge} onColors={setColors} onSave={() => void save()} />
      {line ? <p className={`exo-dc-note${line.bad ? ' is-bad' : ''}`} role="status">{line.text}</p> : null}
    </>
  )
}

// The tag's name (with its badge before it, as Discord's field shows it), badge and color pair, and Discord's warning
// that members reapply a changed tag. Without Server Tags the badges and colors can still be tried, as in Discord, but
// the name can't be set or saved.
function TagEditor({ guildId, value, unlocked, locked, dirty, busy, onTag, onBadge, onColors, onSave }: {
  guildId: string
  value: { tag: string; badge: number; colors: string[] }
  unlocked: { value: string; label: string }[]
  locked: boolean
  dirty: boolean
  busy: boolean
  onTag: (tag: string) => void
  onBadge: (badge: number) => void
  onColors: (colors: string[]) => void
  onSave: () => void
}) {
  const art = useAdmin<{ badges: BadgeArt[] } | null>('badgeArt', { guildId })
  const drawn = new Map((art.data?.badges ?? []).map(b => [b.id, b]))
  const current = drawn.get(value.badge)
  return (
    <>
      <Field label="Tag name" detail="2 to 4 letters or numbers.">
        <span className="exo-dc-tag-field">
          {current ? <BadgeArtwork art={current} primary={value.colors[0]} secondary={value.colors[1]} size={24} /> : null}
          <input className="exo-dc-modal-input exo-dc-field" value={value.tag} maxLength={4} placeholder="WUMP" disabled={locked} aria-label="Tag name" onChange={event => onTag(event.target.value)} />
        </span>
      </Field>
      <BadgePicker drawn={drawn} value={value} unlocked={unlocked} onBadge={onBadge} />
      <Field label="Color" stacked>
        <div className="exo-dc-swatches" role="radiogroup" aria-label="Color">
          {COLORS.map(pair => (
            <button key={pair[0]} type="button" role="radio" aria-checked={value.colors[0] === pair[0]} aria-label={pair[0]} className="exo-dc-swatch"
              style={{ background: `linear-gradient(135deg, ${pair[0]} 50%, ${pair[1]} 50%)` }} onClick={() => onColors(pair)} />
          ))}
        </div>
      </Field>
      <p className="exo-dc-note">Updating your server tag will require all of your members to manually reapply the tag to their profile. We do this to prevent abuse.</p>
      {dirty && !locked ? <button type="button" className="exo-set-btn is-primary" disabled={busy || value.tag.trim().length < 2} onClick={onSave}>Save changes</button> : null}
    </>
  )
}

// The badge, as Discord's editor shows it: each of Discord's own badges in the colors picked (read from Discord's client
// by Exo's host), or their names when Discord's client couldn't be read.
function BadgePicker({ drawn, value, unlocked, onBadge }: {
  drawn: Map<number, BadgeArt>
  value: { badge: number; colors: string[] }
  unlocked: { value: string; label: string }[]
  onBadge: (badge: number) => void
}) {
  if (!drawn.size) return <Field label="Badge"><Choice label="Badge" value={String(value.badge)} choices={unlocked} onChange={b => onBadge(Number(b))} /></Field>
  return (
    <Field label="Badge" stacked>
      <div className="exo-dc-badges" role="radiogroup" aria-label="Badge">
        {unlocked.map(choice => {
          const badge = drawn.get(Number(choice.value))
          return (
            <button key={choice.value} type="button" role="radio" className="exo-dc-badge-pick" aria-checked={value.badge === Number(choice.value)} aria-label={choice.label}
              onClick={() => onBadge(Number(choice.value))}>
              {badge ? <BadgeArtwork art={badge} primary={value.colors[0]} secondary={value.colors[1]} size={32} /> : <span>{choice.label}</span>}
            </button>
          )
        })}
      </div>
    </Field>
  )
}

// Discord's boost levels: how many Boosts each takes and what it gives a server.
const LEVELS = [
  { level: 1, boosts: 2, perks: ['50 server emoji slots (100 total)', '15 sticker slots', '24 soundboard slots', '128 Kbps audio quality', 'Animated server icon', 'Custom server invite background', '720p 60fps Go Live streams'] },
  { level: 2, boosts: 7, perks: ['100 server emoji slots (200 total)', '30 sticker slots', '36 soundboard slots', '256 Kbps audio quality', 'Server banner', '1080p 60fps Go Live streams', '50 MB upload limit for all members', 'Custom role icons'] },
  { level: 3, boosts: 14, perks: ['150 server emoji slots (300 total)', '60 sticker slots', '48 soundboard slots', '384 Kbps audio quality', 'Animated server banner', 'Custom invite link', '100 MB upload limit for all members'] },
]

/** Discord's Boost Perks: the server's level and Boosts, and what each level gives. Boosting happens in Discord. */
export function BoostPerks({ guild }: { guild: PageGuild }) {
  const boosts = guild.premium_subscription_count ?? 0
  const next = LEVELS.find(l => l.level > guild.premium_tier)
  return (
    <>
      <h1>Boost perks</h1>
      <p className="exo-dc-note">{guild.premium_tier ? `Level ${guild.premium_tier}` : 'No level yet'} · {boosts} {boosts === 1 ? 'Boost' : 'Boosts'}{next ? ` · ${Math.max(0, next.boosts - boosts)} more for level ${next.level}` : ''}</p>
      {LEVELS.map(l => (
        <section key={l.level} className="exo-dc-perm-group" aria-label={`Level ${l.level}`}>
          <h3>Level {l.level} · {l.boosts} Boosts{guild.premium_tier >= l.level ? ' · Unlocked' : ''}</h3>
          <ul className="exo-dc-perk-list">{l.perks.map(perk => <li key={perk}>{perk}</li>)}</ul>
        </section>
      ))}
    </>
  )
}

type Sound = { sound_id: string; name: string; volume: number; emoji_id?: string | null; emoji_name?: string | null; user?: { username: string } | null }
const SOUND_SLOTS = [8, 24, 36, 48]

/** Discord's Soundboard: upload a sound (MP3 or OGG, 512 KB), rename it, set its volume and emoji, play or delete it. */
export function Soundboard({ guild }: { guild: PageGuild }) {
  const sounds = useAdmin<{ items: Sound[] }>('sounds', { guildId: guild.id })
  const [editing, setEditing] = useState<Sound | 'new' | null>(null)
  const [removing, setRemoving] = useState<Sound | null>(null)
  const [error, setError] = useState<string | null>(null)
  const items = sounds.data?.items ?? []
  const slots = SOUND_SLOTS[Math.min(guild.premium_tier, 3)]
  const play = (id: string) => void new Audio(`https://cdn.discordapp.com/soundboard-sounds/${id}`).play().catch(() => { })
  const remove = () => {
    if (!removing) return
    void admin('deleteSound', { guildId: guild.id, soundId: removing.sound_id }).then(result => {
      if (result.ok) { setRemoving(null); sounds.reload() } else setError(result.message ?? 'Discord did not delete that sound.')
    })
  }
  return (
    <>
      <h1>Soundboard</h1>
      <p className="exo-dc-note">Upload custom sound reactions that anyone in this server can use in voice.</p>
      <div className="exo-dc-page-bar">
        <strong className="exo-dc-grow">{items.length} of {slots} slots</strong>
        <button type="button" className="exo-set-btn is-primary" disabled={items.length >= slots} onClick={() => setEditing('new')}>Upload sound</button>
      </div>
      <ReadError error={sounds.error} onRetry={sounds.reload} />
      {sounds.data && !items.length ? <p className="exo-dc-note">No sounds yet.</p> : null}
      <ul className="exo-dc-mod-list">
        {items.map(sound => (
          <li key={sound.sound_id}>
            <button type="button" className="exo-set-btn is-compact" aria-label={`Play ${sound.name}`} onClick={() => play(sound.sound_id)}>Play</button>
            <span className="exo-dc-mod-name"><span>{sound.emoji_name ? `${sound.emoji_name} ` : ''}{sound.name}</span><small>{Math.round(sound.volume * 100)}% volume{sound.user ? ` · ${sound.user.username}` : ''}</small></span>
            <button type="button" className="exo-set-btn is-compact" onClick={() => setEditing(sound)}>Edit</button>
            <button type="button" className="exo-set-btn is-compact is-danger" onClick={() => { setError(null); setRemoving(sound) }}>Delete</button>
          </li>
        ))}
      </ul>
      {editing ? <SoundDialog guildId={guild.id} sound={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); sounds.reload() }} /> : null}
      {removing ? (
        <Confirm title={`Delete ${removing.name}?`} action="Delete" error={error} onConfirm={remove} onClose={() => setRemoving(null)}>
          <p>This sound will be gone from this server's soundboard.</p>
        </Confirm>
      ) : null}
    </>
  )
}

function SoundDialog({ guildId, sound, onClose, onSaved }: { guildId: string; sound: Sound | null; onClose: () => void; onSaved: () => void }) {
  const [file, setFile] = useState<File | null>(null)
  const [name, setName] = useState(sound?.name ?? '')
  const [emoji, setEmoji] = useState(sound?.emoji_name ?? '')
  const [volume, setVolume] = useState(Math.round((sound?.volume ?? 1) * 100))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function save() {
    setBusy(true); setError(null)
    let uploadId: string | undefined
    if (!sound) {
      if (!file) { setBusy(false); setError('Pick an MP3 or OGG file.'); return }
      const uploaded = await uploadFile(file)
      if (typeof uploaded !== 'string') { setBusy(false); setError(uploaded.message); return }
      uploadId = uploaded
    }
    const result = await admin(sound ? 'editSound' : 'uploadSound', { guildId, soundId: sound?.sound_id, uploadId, name: name.trim(), volume: volume / 100, emojiName: emoji.trim() || undefined })
    setBusy(false)
    if (result.ok) onSaved(); else { dropUpload(uploadId); setError(result.message ?? 'Discord did not save that sound.') }
  }
  return (
    <Confirm title={sound ? 'Edit sound' : 'Upload a sound'} action={sound ? 'Save' : 'Upload'} danger={false} form busy={busy} disabled={name.trim().length < 2} error={error} onConfirm={() => void save()} onClose={onClose}>
      <div className="exo-dc-form-rows">
        {sound ? null : (
          <label><span>File</span>
            <button type="button" className="exo-set-btn" onClick={() => void pickFile('audio/mpeg,audio/ogg').then(picked => { if (picked) { setFile(picked); if (!name) setName(picked.name.replace(/\.[^.]+$/, '').slice(0, 32)) } })}>{file ? file.name : 'Browse'}</button>
          </label>
        )}
        <label><span>Sound name</span><input className="exo-dc-modal-input" value={name} maxLength={32} onChange={event => setName(event.target.value)} /></label>
        <label><span>Related emoji</span><input className="exo-dc-modal-input" value={emoji} maxLength={8} placeholder="Type an emoji" onChange={event => setEmoji(event.target.value)} /></label>
        <label><span>Sound volume · {volume}%</span><input className="exo-dc-modal-input" type="number" min={0} max={100} value={volume} onChange={event => setVolume(Math.max(0, Math.min(100, Number(event.target.value) || 0)))} /></label>
      </div>
    </Confirm>
  )
}

type Template = { code: string; name: string; description?: string | null; usage_count: number; is_dirty?: boolean | null; updated_at?: string }

/** Discord's Server Template: one copyable layout of this server's channels, roles and settings; make, sync or delete it. */
export function ServerTemplate({ guild }: { guild: PageGuild }) {
  const templates = useAdmin<Template[]>('templates', { guildId: guild.id })
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const template = templates.data?.[0]
  const act = async (action: 'createTemplate' | 'syncTemplate' | 'deleteTemplate', params: Record<string, unknown>) => {
    setBusy(true); setError(null)
    const result = await admin(action, { guildId: guild.id, ...params })
    setBusy(false)
    if (result.ok) templates.reload(); else setError(result.message ?? 'Discord did not do that.')
  }
  const link = template ? `https://discord.new/${template.code}` : ''
  return (
    <>
      <h1>Server template</h1>
      <p className="exo-dc-note">Anyone with the link can make a new server with this server's channels, roles and permissions. Messages and members aren't copied.</p>
      <ReadError error={templates.error} onRetry={templates.reload} />
      {template ? (
        <>
          <Field label={template.name} detail={`${template.description ? `${template.description} · ` : ''}Used ${template.usage_count} ${template.usage_count === 1 ? 'time' : 'times'}${template.is_dirty ? ' · Unsynced changes' : ''}`}>
            <span className="exo-dc-settings-buttons">
              <button type="button" className="exo-set-btn" onClick={() => void navigator.clipboard.writeText(link).then(() => notify({ id: 'dc-copied', text: 'Template link copied', tone: 'good' }))}>Copy link</button>
              {template.is_dirty ? <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={() => void act('syncTemplate', { code: template.code })}>Sync template</button> : null}
              <button type="button" className="exo-set-btn is-danger" disabled={busy} onClick={() => void act('deleteTemplate', { code: template.code })}>Delete template</button>
            </span>
          </Field>
          <p className="exo-dc-note">{link}</p>
        </>
      ) : templates.data ? (
        <>
          <Field label="Template title"><input className="exo-dc-modal-input exo-dc-field" value={name} maxLength={100} aria-label="Template title" onChange={event => setName(event.target.value)} /></Field>
          <Field label="Template description"><input className="exo-dc-modal-input exo-dc-field" value={description} maxLength={120} aria-label="Template description" onChange={event => setDescription(event.target.value)} /></Field>
          <button type="button" className="exo-set-btn is-primary" disabled={busy || !name.trim()} onClick={() => void act('createTemplate', { name: name.trim(), description: description.trim() })}>Generate template</button>
        </>
      ) : null}
      {error ? <ErrorNote className="exo-dc-note is-bad" message={error} /> : null}
    </>
  )
}

/** Discord's Server Widget, on Engagement: on or off, the channel its Join button invites to, and its links. */
export function Widget({ guild, channels }: { guild: PageGuild; channels: DiscordChannel[] }) {
  const widget = useAdmin<{ enabled: boolean; channel_id: string | null }>('widget', { guildId: guild.id })
  const [error, setError] = useState<string | null>(null)
  const save = (enabled: boolean, channelId: string | null) => void admin('saveWidget', { guildId: guild.id, enabled, channelId }).then(result => {
    if (result.ok) { setError(null); widget.reload() } else setError(result.message ?? 'Discord did not save the widget.')
  })
  const data = widget.data
  return (
    <section className="exo-dc-perm-group" aria-label="Server widget">
      <h3>Server widget</h3>
      <ReadError error={widget.error} onRetry={widget.reload} />
      {data ? (
        <>
          <Field label="Enable server widget" error={error}><Switch checked={data.enabled} label="Enable server widget" onChange={on => save(on, data.channel_id)} /></Field>
          <Field label="Invite channel" detail="Where the widget's join button sends people.">
            <Choice label="Invite channel" value={data.channel_id ?? 'none'} choices={[{ value: 'none', label: 'No invite' }, ...textChannels(channels)]} onChange={id => save(data.enabled, id === 'none' ? null : id)} />
          </Field>
          <Field label="JSON API" detail={`https://discord.com/api/guilds/${guild.id}/widget.json`} />
          <Field label="Premade widget" detail={`https://discord.com/widget?id=${guild.id}&theme=dark`} />
        </>
      ) : null}
    </section>
  )
}

/** Discord's Custom Invite Link: discord.gg/your-code, for a server with the feature (Boost level 3, or a partner). */
export function CustomInvite({ guild }: { guild: PageGuild }) {
  const available = has(guild, 'VANITY_URL')
  const vanity = useAdmin<{ code: string | null; uses: number }>(available ? 'vanity' : 'none', { guildId: guild.id })
  const [code, setCode] = useState<string | null>(null)
  const [line, setLine] = useState<{ text: string; bad: boolean } | null>(null)
  const save = () => void admin('saveVanity', { guildId: guild.id, code: (code ?? '').trim() }).then(result => {
    if (result.ok) { setCode(null); setLine({ text: 'Saved.', bad: false }); vanity.reload() } else setLine({ text: result.message ?? 'Discord did not save that link.', bad: true })
  })
  return (
    <section className="exo-dc-perm-group" aria-label="Custom invite link">
      <h3>Custom invite link</h3>
      {!available ? <p className="exo-dc-note">A custom invite link comes with Boost level 3.</p> : (
        <>
          <Field label="discord.gg/" detail={vanity.data ? `Used ${vanity.data.uses} times` : undefined}>
            <input className="exo-dc-modal-input exo-dc-field" value={code ?? vanity.data?.code ?? ''} maxLength={32} aria-label="Custom invite code" onChange={event => setCode(event.target.value.toLowerCase())} />
          </Field>
          {code !== null ? <button type="button" className="exo-set-btn is-primary" onClick={save}>Save changes</button> : null}
          {line ? <p className={`exo-dc-note${line.bad ? ' is-bad' : ''}`} role="status">{line.text}</p> : null}
        </>
      )}
    </section>
  )
}

type Welcome = { description?: string | null; welcome_channels?: { channel_id: string; description: string; emoji_id?: string | null; emoji_name?: string | null }[] }

/** Discord's Welcome Screen (a Community server's): on or off, what the server is about, and up to 5 channels to start in. */
export function WelcomeScreen({ guild, channels }: { guild: PageGuild; channels: DiscordChannel[] }) {
  const welcome = useAdmin<Welcome>('welcome', { guildId: guild.id })
  const [draft, setDraft] = useState<Welcome | null>(null)
  const [busy, setBusy] = useState(false)
  const [line, setLine] = useState<{ text: string; bad: boolean } | null>(null)
  const value = draft ?? welcome.data ?? {}
  const list = value.welcome_channels ?? []
  const enabled = has(guild, 'WELCOME_SCREEN_ENABLED')
  async function save(on: boolean) {
    setBusy(true); setLine(null)
    const result = await admin('saveWelcome', { guildId: guild.id, enabled: on, description: value.description ?? '', channels: list })
    setBusy(false)
    if (result.ok) { setDraft(null); setLine({ text: 'Saved.', bad: false }); welcome.reload() } else setLine({ text: result.message ?? 'Discord did not save the welcome screen.', bad: true })
  }
  const text = textChannels(channels)
  return (
    <>
      <h1>Welcome screen</h1>
      <p className="exo-dc-note">What new members see first: what the server is about and where to start.</p>
      <ReadError error={welcome.error} onRetry={welcome.reload} />
      <Field label="Enable welcome screen"><Switch checked={enabled} disabled={busy || !list.length} label="Enable welcome screen" onChange={on => void save(on)} /></Field>
      <Field label="Server description" stacked>
        <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={2} maxLength={140} value={value.description ?? ''} aria-label="Server description"
          onChange={event => setDraft({ ...value, description: event.target.value })} />
      </Field>
      <section className="exo-dc-perm-group" aria-label="Recommended channels">
        <h3>Recommended channels</h3>
        <ul className="exo-dc-rule-list">
          {list.map((item, i) => (
            <li key={i}>
              <Choice label="Channel" value={item.channel_id} choices={text} onChange={id => setDraft({ ...value, welcome_channels: list.map((c, j) => j === i ? { ...c, channel_id: id } : c) })} />
              <input className="exo-dc-modal-input" value={item.description} maxLength={50} placeholder="What's this channel for?" aria-label="Channel description"
                onChange={event => setDraft({ ...value, welcome_channels: list.map((c, j) => j === i ? { ...c, description: event.target.value } : c) })} />
              <button type="button" className="exo-set-btn is-compact" onClick={() => setDraft({ ...value, welcome_channels: list.filter((_, j) => j !== i) })}>Remove</button>
            </li>
          ))}
        </ul>
        <span className="exo-dc-settings-buttons">
          {list.length < 5 && text.length ? <button type="button" className="exo-set-btn" onClick={() => setDraft({ ...value, welcome_channels: [...list, { channel_id: text[0].value, description: '' }] })}>Add a channel</button> : null}
          {draft ? <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={() => void save(enabled)}>Save changes</button> : null}
        </span>
      </section>
      {line ? <p className={`exo-dc-note${line.bad ? ' is-bad' : ''}`} role="status">{line.text}</p> : null}
    </>
  )
}

/**
 * Discord's Community: set up a Community server (rules and updates channels, safety defaults), or for one already,
 * its overview: the rules and community updates channels.
 */
export function Community({ guild, channels, onSaved }: { guild: PageGuild; channels: DiscordChannel[]; onSaved: () => void }) {
  const community = has(guild, 'COMMUNITY')
  const text = textChannels(channels)
  const choices = [{ value: '1', label: 'Create one for me' }, ...text]
  const [rules, setRules] = useState(guild.rules_channel_id ?? '1')
  const [updates, setUpdates] = useState(guild.public_updates_channel_id ?? '1')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function save() {
    setBusy(true); setError(null)
    const result = community
      ? await admin('saveGuild', { guildId: guild.id, changes: { rules_channel_id: rules, public_updates_channel_id: updates } })
      : await admin('community', { guildId: guild.id, rulesChannelId: rules, updatesChannelId: updates })
    setBusy(false)
    if (result.ok) onSaved(); else setError(result.message ?? 'Discord did not save that.')
  }
  return (
    <>
      <h1>{community ? 'Community overview' : 'Enable community'}</h1>
      <p className="exo-dc-note">{community
        ? 'Your server is a Community server: it can use Welcome Screen, Server Insights, announcement channels and Server Discovery.'
        : 'Community servers get Welcome Screen, Server Insights, announcement channels and can join Server Discovery. Turning it on sets a verification level, filters media from everyone and sets notifications to @mentions only.'}</p>
      <Field label="Rules or guidelines channel" detail="Where you post your rules.">
        <Choice label="Rules or guidelines channel" value={rules} choices={community ? text : choices} onChange={setRules} />
      </Field>
      <Field label="Community updates channel" detail="Where Discord sends updates for moderators.">
        <Choice label="Community updates channel" value={updates} choices={community ? text : choices} onChange={setUpdates} />
      </Field>
      {error ? <ErrorNote className="exo-dc-note is-bad" message={error} /> : null}
      <button type="button" className="exo-set-btn is-primary" disabled={busy || (community && rules === (guild.rules_channel_id ?? '') && updates === (guild.public_updates_channel_id ?? ''))}
        onClick={() => void save()}>{community ? 'Save changes' : 'Enable community'}</button>
    </>
  )
}

type Week = Record<string, number | string | null | undefined>
type Insights = { small: boolean; engagement: Week[] | null; growth: Week[] | null; retention: Week[] | null }

/**
 * Discord's Server Insights page: this week's visitors, communicators, new members and new member retention against
 * last week's, and the full insights on Discord's website. Servers under 500 members (not Partnered or Verified) see
 * Discord's note instead of the numbers.
 */
export function ServerInsights({ guild }: { guild: PageGuild & { approximate_member_count?: number } }) {
  const ready = has(guild, 'PARTNERED') || has(guild, 'VERIFIED') || (guild.approximate_member_count ?? 0) >= 500
  return (
    <>
      <h1>Server insights</h1>
      {ready ? null : <p className="exo-dc-note">{SMALL}</p>}
      <Field label="Server Insights is hosted on a separate website so that it's easier to share links and browse cross platform.">
        <a className="exo-set-btn is-primary" href={`https://discord.com/developers/servers/${guild.id}`} target="_blank" rel="noreferrer">Go to Server Insights</a>
      </Field>
      {ready ? <InsightCards guildId={guild.id} /> : null}
    </>
  )
}

const SMALL = "Looks like your server isn't quite ready to see full insights. You will only have access to Announcement Channels and Welcome Screen analytics."

function InsightCards({ guildId }: { guildId: string }) {
  const insights = useAdmin<Insights>('insights', { guildId })
  const data = insights.data
  // Discord's weeks come newest first: this week against the one before.
  const card = (title: string, weeks: Week[] | null | undefined, key: string, percent = false) => {
    const [now, before] = (weeks ?? []).map(w => typeof w[key] === 'number' ? w[key] : null)
    const change = now != null && before ? (now - before) * 100 / before : null
    return (
      <Field key={title} label={title} detail={change === null ? (now == null ? undefined : 'From the most recent week') : `${Math.abs(change).toFixed(0)}% ${change >= 0 ? 'up' : 'down'} since last week`}>
        <strong className="exo-dc-insight">{now == null ? 'N/A' : `${now.toLocaleString(undefined, { maximumFractionDigits: 1 })}${percent ? '%' : ''}`}</strong>
      </Field>
    )
  }
  if (insights.error) return <ReadError error={insights.error} onRetry={insights.reload} />
  if (!data) return null
  if (data.small) return <p className="exo-dc-note">{SMALL}</p>
  return (
    <>
      {card('Weekly visitors', data.engagement, 'visitors')}
      {card('Weekly communicators', data.engagement, 'communicators')}
      {card('Weekly new members', data.growth, 'new_members')}
      {card('Weekly new member retention', data.retention, 'pct_retained', true)}
    </>
  )
}

/** Pages whose work happens on Discord's own site: Server Subscriptions and App Directory, with where to go. */
export function OnDiscord({ title, detail, url, action }: { title: string; detail: string; url: string; action: string }) {
  return (
    <>
      <h1>{title}</h1>
      <p className="exo-dc-note">{detail}</p>
      <a className="exo-set-btn is-primary" href={url} target="_blank" rel="noreferrer">{action}</a>
    </>
  )
}
