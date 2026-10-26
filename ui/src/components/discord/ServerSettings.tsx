import { useEffect, useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import { useGlide } from '../../lib/glide'
import type { DiscordChannel, DiscordModeration } from '../../lib/discord'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { guildBannerUrl, requestDiscordSettings } from '../../lib/discord'
import { admin, Confirm, dropUpload, Field, SaveBar, pickFile, previewOf, ReadError, UnsavedProvider, useAdmin, useLayer, useUnsaved } from './admin'
import { uploadFile } from './Chat'
import { Roles } from './settingsRoles'
import { Emoji, Stickers } from './settingsExpressions'
import { AuditLog, Bans, Integrations, Invites, type Incidents } from './settingsModeration'
import { AutoMod } from './settingsAutomod'
import { SettingsLayer } from './SettingsLayer'
import { BoostPerks, Community, CustomInvite, OnDiscord, ServerInsights, ServerTag, ServerTemplate, Soundboard, WelcomeScreen, Widget } from './settingsPages'
import { Access } from './settingsAccess'
import { Onboarding } from './settingsOnboarding'
import { Members } from './serverPages'
import { pushPage } from '../../lib/viewTransition'
import { Skeleton } from '../ui/Skeleton'

export type Guild = {
  id: string; name: string; icon: string | null; banner: string | null; description: string | null; owner_id: string
  afk_channel_id: string | null; afk_timeout: number; system_channel_id: string | null; system_channel_flags: number
  verification_level: number; default_message_notifications: number; explicit_content_filter: number; mfa_level: number
  premium_tier: number; features: string[]; premium_subscription_count?: number; rules_channel_id?: string | null
  safety_alerts_channel_id?: string | null; public_updates_channel_id?: string | null; incidents_data?: Incidents
  owner_configured_content_level?: number; preferred_locale?: string | null; approximate_member_count?: number
}

type Section = 'profile' | 'tag' | 'engagement' | 'boost' | 'emoji' | 'stickers' | 'soundboard' | 'members' | 'roles' | 'invites' | 'access'
  | 'integrations' | 'directory' | 'safety' | 'automod' | 'audit' | 'bans' | 'community' | 'onboarding' | 'insights' | 'welcome' | 'subscriptions' | 'template'
type Item = { id: Section; label: string; shown: (m: DiscordModeration, g: Guild | null) => boolean }

const community = (g: Guild | null) => !!g?.features.includes('COMMUNITY')

// Discord's Server Settings list (its generateSections): its groups and order, each section with the permission Discord
// asks for, in Exo's sentence case.
const NAV: { heading: string | null; items: Item[] }[] = [
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

/**
 * Discord's Server Settings in Exo's look: each section only with the permission Discord shows it for.
 * Members opens the server's Members page, as Discord does for a Community server; elsewhere it is a section here.
 * Unsaved changes hold the page: changing section, closing or Escape shakes the save bar instead.
 * The owner gets Delete Server at the bottom, as Discord offers it (with 2FA on their account when the server requires it).
 */
export function ServerSettings({ guildId, name, moderation, channels, onClose, onMembers }: {
  guildId: string
  name: string
  moderation: DiscordModeration
  channels: DiscordChannel[]
  onClose: () => void
  onMembers?: () => void
}) {
  const [section, setSection] = useState<Section>('profile')
  const [deleting, setDeleting] = useState(false)
  const glide = useGlide<HTMLDivElement>()
  const guild = useAdmin<Guild>('guild', { guildId })
  const unsaved = useUnsaved()
  const close = () => unsaved.guard(onClose)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, close)
  // Discord's own settings opened from here (Safety Setup's My Account) take the room's place.
  const leave = useRef(close)
  useEffect(() => { leave.current = close })
  useEffect(() => {
    const away = () => leave.current()
    window.addEventListener('exo-discord-settings', away)
    return () => window.removeEventListener('exo-discord-settings', away)
  }, [])
  const pick = (id: Section) => unsaved.guard(() => { if (id === 'members' && onMembers) { onClose(); onMembers() } else void pushPage(() => setSection(id), '.exo-dc-settings-page', '.exo-dc-settings-nav [aria-current="true"]') })
  return (
    <UnsavedProvider value={unsaved}>
    <SettingsLayer onClose={close}>
    <div ref={box} className="exo-dc-settings" role="dialog" aria-modal="true" aria-label={`${name} settings`} tabIndex={-1}>
      <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
      <nav className="exo-dc-settings-nav" aria-label="Server settings sections">
        <div ref={glide} className="exo-glide">
          {NAV.map(group => {
            const items = group.items.filter(item => item.shown(moderation, guild.data))
            if (!items.length) return null
            return (
              <section key={group.heading ?? items[0].id} aria-label={group.heading ?? (items[0].id === 'profile' ? name : items[0].label)}>
                {group.heading || items[0].id === 'profile' ? <h2>{group.heading ?? name}</h2> : null}
                <ul>
                  {items.map(item => (
                    <li key={item.id}>
                      <button type="button" aria-current={section === item.id ? 'true' : undefined} className={section === item.id ? 'is-current' : undefined}
                        onClick={() => pick(item.id)}>{item.label}</button>
                    </li>
                  ))}
                </ul>
              </section>
            )
          })}
          {moderation.owner && guild.data && (guild.data.mfa_level !== 1 || moderation.mfa) ? (
            <section aria-label="Delete server">
              <ul><li><button type="button" className="is-danger" onClick={() => unsaved.guard(() => setDeleting(true))}>Delete server</button></li></ul>
            </section>
          ) : null}
        </div>
      </nav>
      <div className="exo-dc-settings-body">
        <button type="button" className="exo-game-close exo-dc-settings-close" aria-label="Close server settings" onClick={close}><Close size={16} /></button>
        <div key={section} className="exo-dc-settings-page exo-motion-switch">
          <SectionPage section={section} guildId={guildId} moderation={moderation} channels={channels} guild={guild.data} error={guild.error} onSaved={guild.reload} />
        </div>
      </div>
    </div>
    {deleting ? <DeleteServer guildId={guildId} name={name} confirmByName={!moderation.mfa} onClose={() => setDeleting(false)} onDeleted={onClose} /> : null}
    </SettingsLayer>
    </UnsavedProvider>
  )
}

type PageProps = { guildId: string; moderation: DiscordModeration; channels: DiscordChannel[]; guild: Guild | null; onSaved: () => void }

// Each section's page; the server's own (Server Profile, Engagement, Safety Setup) are the Overview below.
const PAGES: Partial<Record<Section, (p: PageProps) => React.ReactNode>> = {
  roles: p => <Roles guildId={p.guildId} />,
  emoji: p => <Emoji guildId={p.guildId} tier={p.guild?.premium_tier ?? 0} />,
  stickers: p => <Stickers guildId={p.guildId} tier={p.guild?.premium_tier ?? 0} />,
  invites: p => <><Invites guildId={p.guildId} channels={p.channels} incidents={p.guild?.incidents_data ?? null} onChanged={p.onSaved} />{p.guild ? <CustomInvite guild={p.guild} /> : null}</>,
  integrations: p => <Integrations guildId={p.guildId} moderation={p.moderation} channels={p.channels} />,
  automod: p => <AutoMod guildId={p.guildId} channels={p.channels} />,
  audit: p => <AuditLog guildId={p.guildId} />,
  bans: p => <Bans guildId={p.guildId} />,
  members: p => <><h1>Members</h1><Members guildId={p.guildId} canPrune={p.moderation.prune} /></>,
  directory: p => <OnDiscord title="App directory" detail="Find apps and bots for this server in Discord's App Directory; the ones you add show in Integrations." url={`https://discord.com/application-directory?guild_id=${p.guildId}`} action="Open app directory" />,
  subscriptions: p => <OnDiscord title="Server subscriptions" detail="Offer paid membership tiers with their own roles and perks. Discord sets them up, takes payments and pays you out." url={`https://discord.com/channels/${p.guildId}/role-subscriptions`} action="Set up in Discord" />,
  tag: p => p.guild && <ServerTag guild={p.guild} />,
  boost: p => p.guild && <BoostPerks guild={p.guild} />,
  soundboard: p => p.guild && <Soundboard guild={p.guild} />,
  access: p => p.guild && <Access guild={p.guild} administrator={p.moderation.administrator === true || p.moderation.owner} onSaved={p.onSaved} />,
  insights: p => p.guild && <ServerInsights guild={p.guild} />,
  template: p => p.guild && <ServerTemplate guild={p.guild} />,
  community: p => p.guild && <Community key={p.guild.rules_channel_id ?? ''} guild={p.guild} channels={p.channels} onSaved={p.onSaved} />,
  onboarding: p => p.guild && <Onboarding guild={p.guild} channels={p.channels} />,
  welcome: p => p.guild && <WelcomeScreen guild={p.guild} channels={p.channels} />,
}

function SectionPage({ section, error, ...props }: PageProps & { section: Section; error: string | null }) {
  const { guild, moderation, channels, onSaved } = props
  const page = PAGES[section]
  // A page that needs the server waits for it like the server's own pages.
  if (page && (guild || !['tag', 'boost', 'soundboard', 'access', 'template', 'community', 'onboarding', 'insights', 'welcome'].includes(section))) return page(props)
  // A failed read keeps what the page had, with Try Again.
  if (!guild) return error ? <ReadError error={error} onRetry={onSaved} /> : <Skeleton label="Loading the server" />
  return (
    <>
      <Overview key={section} section={section} guild={guild} moderation={moderation} channels={channels} onSaved={onSaved} />
      {section === 'engagement' && moderation.manageGuild ? <Widget guild={guild} channels={channels} /> : null}
      <ReadError error={error} onRetry={onSaved} />
    </>
  )
}

type Draft = Partial<Pick<Guild, 'name' | 'description' | 'afk_channel_id' | 'afk_timeout' | 'system_channel_id' | 'system_channel_flags' | 'default_message_notifications'
  | 'verification_level' | 'explicit_content_filter'>>
type Picture = { file: File; preview: string } | null

// Server Profile, Engagement and Safety Setup: changes wait in a draft until Save Changes, as in Discord.
function Overview({ section, guild, moderation, channels, onSaved }: { section: Section; guild: Guild; moderation: DiscordModeration; channels: DiscordChannel[]; onSaved: () => void }) {
  const canEdit = moderation.manageGuild
  const [draft, setDraft] = useState<Draft>({})
  const [icon, setIcon] = useState<Picture | undefined>(undefined)
  const [banner, setBanner] = useState<Picture | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [nameError, setNameError] = useState<string | null>(null)
  const value = <K extends keyof Draft>(key: K) => (key in draft ? draft[key] : guild[key]) as Guild[K]
  const set = (change: Draft) => { setError(null); setNameError(null); setDraft(d => ({ ...d, ...change })) }
  const dirty = Object.keys(draft).length > 0 || icon !== undefined || banner !== undefined
  async function save() {
    // Discord's rule for a server name, said under Name rather than dropped.
    const name = draft.name?.trim()
    if (name !== undefined && (name.length < 2 || name.length > 100)) { setNameError('Server name must be between 2 and 100 in length.'); return }
    setBusy(true)
    const changes: Record<string, unknown> = { ...draft }
    // Pictures sent for a save that didn't happen are dropped, so they stop counting toward the upload limit.
    const sent: string[] = []
    for (const [key, picture] of [['icon', icon], ['banner', banner]] as const) {
      if (picture === undefined) continue
      if (picture === null) { changes[key] = null; continue }
      const uploaded = await uploadFile(picture.file)
      if (typeof uploaded !== 'string') { sent.forEach(dropUpload); setError(uploaded.message); setBusy(false); return }
      sent.push(uploaded)
      changes[key] = uploaded
    }
    const result = await admin('saveGuild', { guildId: guild.id, changes })
    setBusy(false)
    if (!result.ok) { sent.forEach(dropUpload); setError(result.message ?? 'Discord did not save that.'); return }
    setDraft({}); setIcon(undefined); setBanner(undefined); onSaved()
  }
  const reset = () => { setDraft({}); setIcon(undefined); setBanner(undefined); setError(null); setNameError(null) }
  return (
    <>
      {section === 'profile' ? <Profile guild={guild} canEdit={canEdit} value={value} set={set} nameError={nameError} icon={icon} setIcon={setIcon} banner={banner} setBanner={setBanner} /> : null}
      {section === 'engagement' ? <Engagement canEdit={canEdit} value={value} set={set} channels={channels} /> : null}
      {section === 'safety' ? <Safety guild={guild} moderation={moderation} channels={channels} value={value} set={set} onSaved={onSaved} /> : null}
      <SaveBar shown={dirty} busy={busy} error={error} onReset={reset} onSave={() => void save()} />
    </>
  )
}

type Value = <K extends keyof Draft>(key: K) => Guild[K]

function Profile({ guild, canEdit, value, set, nameError, icon, setIcon, banner, setBanner }: {
  guild: Guild
  canEdit: boolean
  value: Value
  set: (change: Draft) => void
  nameError: string | null
  icon: Picture | undefined
  setIcon: (picture: Picture | undefined) => void
  banner: Picture | undefined
  setBanner: (picture: Picture | undefined) => void
}) {
  const iconUrl = icon === undefined ? (guild.icon ? `https://cdn.discordapp.com/icons/${guild.id}/${guild.icon}.webp?size=256&quality=lossless` : null) : icon?.preview ?? null
  const bannerUrl = banner === undefined ? (guild.banner ? guildBannerUrl(guild.id, guild.banner, false) : null) : banner?.preview ?? null
  const choose = async (apply: (picture: Picture) => void) => {
    const file = await pickFile('image/png,image/jpeg,image/gif,image/webp')
    if (file) apply({ file, preview: await previewOf(file) })
  }
  return (
    <>
      <h1>Server profile</h1>
      <Field label="Name" error={nameError}>
        <input className="exo-dc-modal-input exo-dc-field" value={value('name')} maxLength={100} disabled={!canEdit} aria-label="Server name"
          onChange={event => set({ name: event.target.value })} />
      </Field>
      <Field label="Icon" detail="We recommend an image of at least 512x512.">
        <span className="exo-dc-picture">
          {iconUrl ? <img src={iconUrl} alt="" draggable={false} /> : <span className="exo-dc-picture-empty">{guild.name.slice(0, 1)}</span>}
          {canEdit ? <button type="button" className="exo-dc-tab is-raised" onClick={() => void choose(setIcon)}>Change icon</button> : null}
          {canEdit && iconUrl ? <button type="button" className="exo-dc-tab" onClick={() => setIcon(null)}>Remove icon</button> : null}
        </span>
      </Field>
      {guild.features.includes('BANNER') ? (
        <Field label="Banner" detail="Shown at the top of your channel list. At least 960x540, 16:9.">
          <span className="exo-dc-picture is-banner">
            {bannerUrl ? <img src={bannerUrl} alt="" draggable={false} /> : null}
            {canEdit ? <button type="button" className="exo-dc-tab is-raised" onClick={() => void choose(setBanner)}>Change banner</button> : null}
            {canEdit && bannerUrl ? <button type="button" className="exo-dc-tab" onClick={() => setBanner(null)}>Remove</button> : null}
          </span>
        </Field>
      ) : null}
      {guild.features.includes('COMMUNITY') ? (
        <Field label="Description" detail="How did your server get started? Why should people join?" stacked>
          <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={3} maxLength={120} value={value('description') ?? ''} disabled={!canEdit} aria-label="Server description"
            onChange={event => set({ description: event.target.value || null })} />
        </Field>
      ) : null}
    </>
  )
}

const AFK = [{ value: '60', label: '1 minute' }, { value: '300', label: '5 minutes' }, { value: '900', label: '15 minutes' }, { value: '1800', label: '30 minutes' }, { value: '3600', label: '1 hour' }]
// System channel flags (Discord's SUPPRESS_* bits), each switch on while its bit is off.
const SYSTEM_FLAGS = [
  { bit: 1, label: 'Send a random welcome message when someone joins this server.' },
  { bit: 8, label: 'Prompt members to reply to welcome messages with a sticker.' },
  { bit: 2, label: 'Send a message when someone boosts this server.' },
  { bit: 4, label: 'Send helpful tips for server setup.' },
]

function Engagement({ canEdit, value, set, channels }: { canEdit: boolean; value: Value; set: (change: Draft) => void; channels: DiscordChannel[] }) {
  const text = channels.filter(c => c.kind === 'text' && c.type === 0)
  const voice = channels.filter(c => c.kind === 'voice' && c.type === 2)
  const system = value('system_channel_id')
  const flags = value('system_channel_flags')
  const notify = value('default_message_notifications')
  return (
    <>
      <h1>Engagement</h1>
      <Field label="System messages channel" detail="This is the channel we send system event messages to.">
        <Choice label="System messages channel" value={system ?? 'none'} disabled={!canEdit}
          choices={[{ value: 'none', label: 'No system messages' }, ...text.map(c => ({ value: c.id, label: `#${c.name}` }))]}
          onChange={id => set({ system_channel_id: id === 'none' ? null : id })} />
      </Field>
      {SYSTEM_FLAGS.map(flag => (
        <Field key={flag.bit} label={flag.label}>
          <Switch checked={(flags & flag.bit) === 0} disabled={!canEdit || !system} label={flag.label}
            onChange={on => set({ system_channel_flags: on ? flags & ~flag.bit : flags | flag.bit })} />
        </Field>
      ))}
      <Field label="Inactive channel" detail="Automatically move members to this channel and mute them when they have been idle for longer than the inactive time-out. This does not affect browsers.">
        <Choice label="Inactive channel" value={value('afk_channel_id') ?? 'none'} disabled={!canEdit}
          choices={[{ value: 'none', label: 'No inactive channel' }, ...voice.map(c => ({ value: c.id, label: c.name }))]}
          onChange={id => set({ afk_channel_id: id === 'none' ? null : id })} />
      </Field>
      <Field label="Inactive timeout">
        <Choice label="Inactive timeout" value={String(value('afk_timeout'))} disabled={!canEdit || !value('afk_channel_id')} choices={AFK}
          onChange={seconds => set({ afk_timeout: Number(seconds) })} />
      </Field>
      <Field label="Default notification settings" detail="This will determine whether members who have not explicitly set their notification settings receive a notification for every message sent in this server or not.">
        <div className="exo-shelf-chips" role="radiogroup" aria-label="Default notification settings">
          {[{ v: 0, label: 'All messages' }, { v: 1, label: 'Only @mentions' }].map(option => (
            <button key={option.v} type="button" role="radio" className="exo-shelf-chip" disabled={!canEdit} aria-checked={notify === option.v} aria-pressed={notify === option.v}
              onClick={() => set({ default_message_notifications: option.v })}>{option.label}</button>
          ))}
        </div>
      </Field>
    </>
  )
}

const VERIFICATION = [
  { level: 0, name: 'None', detail: 'Unrestricted' },
  { level: 1, name: 'Low', detail: 'Must have a verified email on their Discord account.' },
  { level: 2, name: 'Medium', detail: 'Must also be registered on Discord for longer than 5 minutes.' },
  { level: 3, name: 'High', detail: 'Must also be a member of this server for longer than 10 minutes.' },
  { level: 4, name: 'Highest', detail: 'Must have a verified phone on their Discord account.' },
]
const CONTENT_FILTER = [
  { level: 2, name: 'Filter messages from all members', detail: 'All messages will be filtered for sensitive image-based media.' },
  { level: 1, name: 'Filter messages from server members without roles', detail: 'Messages from server members without a role will be filtered for sensitive image-based media.' },
  { level: 0, name: 'Do not filter', detail: 'Messages will not be filtered for sensitive image-based media.' },
]

// Discord's Safety Setup: verification level and the sensitive content filter wait for Save Changes; the owner's
// switches (2FA for moderators, prune for admins only) save at once, as Discord's do, the 2FA one after the owner verifies.
function Safety({ guild, moderation, channels, value, set, onSaved }: { guild: Guild; moderation: DiscordModeration; channels: DiscordChannel[]; value: Value; set: (change: Draft) => void; onSaved: () => void }) {
  const community = guild.features.includes('COMMUNITY')
  const discoverable = guild.features.includes('DISCOVERABLE')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const required = guild.mfa_level === 1
  // Discord's rule: only an owner with 2FA on their own account, and never off while the server is in Server Discovery.
  const canMfa = moderation.owner && moderation.mfa && !(required && discoverable)
  const why = required && discoverable ? '2FA is required while this server is in Server Discovery.'
    : !moderation.owner ? 'Only the server owner can change this setting if they have 2FA enabled.'
      : moderation.mfa ? null : 'To change this setting, first enable 2FA on your account.'
  async function owner(action: 'guildMfa' | 'pruneAdmins' | 'raidAlerts', on: boolean) {
    setBusy(true); setError(null)
    const result = await admin(action, { guildId: guild.id, on })
    setBusy(false)
    if (result.ok) onSaved()
    else if (result.message !== MFA_CANCELLED) setError(result.message ?? 'Discord did not save that.')
  }
  return (
    <>
      <h1>Safety setup</h1>
      <Field label="Verification level" detail="Members of the server must meet the following criteria before they can send messages in text channels or initiate a direct message conversation. If a member has an assigned role and server onboarding is not enabled, this does not apply." stacked>
        <Options options={VERIFICATION.map(v => ({ ...v, disabled: community && v.level === 0, detail: community && v.level === 0 ? 'This option is not available for community servers.' : v.detail }))}
          value={value('verification_level')} label="Verification level" onPick={level => set({ verification_level: level })} />
      </Field>
      <Field label="Require 2FA for moderator actions" error={error}
        detail={`Moderators must have two-factor authentication enabled to ban, kick or time-out members and delete messages.${why ? ' ' + why : ''}`}>
        <Switch checked={required} disabled={!canMfa || busy} label="Require 2FA for moderator actions" onChange={on => void owner('guildMfa', on)} />
      </Field>
      {moderation.owner && !moderation.mfa ? (
        <p className="exo-dc-note">Turn on two-factor authentication in <button type="button" className="exo-dc-tab" onClick={() => requestDiscordSettings('account')}>My account</button></p>
      ) : null}
      {community ? null : (
        <Field label="Restrict member prune to admins" detail="Only administrators and the server owner can prune inactive members when this setting is enabled.">
          <Switch checked={guild.features.includes('PRUNE_REQUIRES_ADMIN')} disabled={!moderation.owner || busy} label="Restrict member prune to admins" onChange={on => void owner('pruneAdmins', on)} />
        </Field>
      )}
      <Field label="Sensitive content filters" detail="Choose if server members can share image-based media detected by discord’s sensitive content filters." stacked>
        <Options options={CONTENT_FILTER.map(v => ({ ...v, disabled: community && v.level !== 2, detail: community && v.level !== 2 ? 'This option is not available for community servers.' : v.detail }))}
          value={value('explicit_content_filter')} label="Sensitive content filters" onPick={level => set({ explicit_content_filter: level })} />
      </Field>
      <SafetyAlerts guild={guild} channels={channels} busy={busy} onAlerts={on => void owner('raidAlerts', on)} onSaved={onSaved} />
    </>
  )
}

// Discord's Activity Alerts (DM or join activity beyond the server's usual) and the channel they go to; both save at once.
function SafetyAlerts({ guild, channels, busy, onAlerts, onSaved }: { guild: Guild; channels: DiscordChannel[]; busy: boolean; onAlerts: (on: boolean) => void; onSaved: () => void }) {
  const community = guild.features.includes('COMMUNITY')
  const alerts = community ? !guild.features.includes('RAID_ALERTS_DISABLED') : guild.features.includes('NON_COMMUNITY_RAID_ALERTS')
  const channel = guild.safety_alerts_channel_id ?? guild.public_updates_channel_id ?? null
  const [error, setError] = useState<string | null>(null)
  const pick = (id: string) => void admin('saveGuild', { guildId: guild.id, changes: { safety_alerts_channel_id: id } }).then(result => {
    if (result.ok) { setError(null); onSaved() } else setError(result.message ?? 'Discord did not save that.')
  })
  return (
    <>
      <Field label="Activity alerts" detail="Receive notifications for DM or join activity that exceeds usual numbers for your server.">
        <Switch checked={alerts} disabled={busy || !channel} label="Activity alerts" onChange={onAlerts} />
      </Field>
      <Field label="Safety notifications channel" detail="Anyone with access to this text channel will be able to see the notifications." error={error}>
        <Choice label="Safety notifications channel" value={channel ?? ''} choices={channels.filter(c => c.kind === 'text' && c.type === 0).map(c => ({ value: c.id, label: `#${c.name}` }))} onChange={pick} />
      </Field>
    </>
  )
}

// What the verify dialog answers when the person closes it: no error to show, the setting just stays.
const MFA_CANCELLED = 'Multi-factor authentication cancelled'

/**
 * Discord's Delete Server: an owner with 2FA confirms and then verifies; one without types the server's name, as
 * Discord asks. The room leaves the server when Discord says it is gone.
 */
function DeleteServer({ guildId, name, confirmByName, onClose, onDeleted }: { guildId: string; name: string; confirmByName: boolean; onClose: () => void; onDeleted: () => void }) {
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function remove() {
    if (confirmByName && typed !== name) { setError('You didn’t enter the server name correctly'); return }
    setBusy(true); setError(null)
    const result = await admin('deleteGuild', { guildId })
    setBusy(false)
    if (result.ok) { onClose(); onDeleted() } else if (result.message !== MFA_CANCELLED) setError(result.message ?? 'Discord did not delete it.')
  }
  return (
    <Confirm title={`Delete '${name}'?`} action="Delete server" busy={busy} error={error} form={confirmByName} onClose={onClose} onConfirm={() => void remove()}>
      <p>This action cannot be undone.</p>
      {confirmByName ? (
        <input className="exo-dc-modal-input" value={typed} autoFocus aria-label="Enter server name" placeholder="Enter server name"
          onChange={event => { setTyped(event.target.value); setError(null) }} />
      ) : null}
    </Confirm>
  )
}

function Options({ options, value, label, onPick }: { options: { level: number; name: string; detail: string; disabled?: boolean }[]; value: number; label: string; onPick: (level: number) => void }) {
  return (
    <div className="exo-dc-options" role="radiogroup" aria-label={label}>
      {options.map(option => (
        <button key={option.level} type="button" role="radio" aria-checked={value === option.level} disabled={option.disabled}
          className={`exo-dc-option${value === option.level ? ' is-current' : ''}`} onClick={() => { if (value !== option.level) onPick(option.level) }}>
          <span className="exo-dc-option-dot" aria-hidden="true" />
          <span className="exo-dc-settings-copy"><strong>{option.name}</strong><span>{option.detail}</span></span>
        </button>
      ))}
    </div>
  )
}
