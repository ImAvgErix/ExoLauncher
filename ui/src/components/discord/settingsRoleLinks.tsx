import { useState } from 'react'
import { avatarUrl, type DiscordUser } from '../../lib/discord'
import { Switch } from '../Switch'
import { Menu, type MenuItem } from '../ui/Menu'
import { Field, ReadError, useAdmin } from './admin'
import { ConnectionMark, connectionLabel } from './profileParts'
import { Avatar } from './presence'
import { Skeleton } from '../ui/Skeleton'

/**
 * Discord's Links tab of the role editor (Linked Roles): the accounts members must connect before they get the role, and
 * for the accounts Discord reads numbers or checks from (or an app's own), how much of each. Two accounts or more are
 * needed all together or any one of them. Requirements start only while the role has no members, as in Discord; the
 * changes wait for the role editor's Save.
 */

/** One requirement as Discord stores it: an account to connect alone, or one of its numbers or checks. */
export type RoleLink = { connection_type: string; connection_metadata_field: string | null; operator: number | null; value: string | null; application_id?: string | null }
type Check = { field: string; label: string; detail: (n: number) => string; flag?: boolean; op?: number }
type AppField = { key: string; name: string; description?: string | null; type: number }
type Integration = { application?: { id: string; name: string; bot?: DiscordUser | null; role_connections_verification_url?: string | null } | null; role_connections_metadata?: AppField[] | null }

// Discord's operators. Its counts read "at least n" (stored greater than n − 1) or "at most n" (stored less than n + 1).
const EQUAL = 1, NOT_EQUAL = 2, LESS = 3, MORE = 4
const APP = 'application'
const MAX = 10
const plural = (n: number, one: string) => `${n} ${n === 1 ? one : `${one}s`}`
const age: Check = { field: 'created_at', label: 'Account age', detail: n => `Account must be at least ${plural(n, 'day')} old` }
const atLeast = (field: string, label: string, one: string): Check => ({ field, label, detail: n => `Must have at least ${plural(n, one)}` })
const flag = (field: string, label: string, detail: string): Check => ({ field, label, detail: () => detail, flag: true })
// The accounts Discord reads numbers or checks from, in its own words.
const CHECKS: Record<string, Check[]> = {
  steam: [age, atLeast('game_count', 'Games', 'game'), atLeast('item_count_dota2', 'DOTA 2 items', 'DOTA 2 item'), atLeast('item_count_tf2', 'Team Fortress 2 items', 'TF2 item')],
  twitter: [age, atLeast('followers_count', 'Followers', 'follower'), atLeast('statuses_count', 'Posts', 'post'), flag('verified', 'Verified', 'Must be verified')],
  reddit: [age, { field: 'total_karma', label: 'Karma', detail: n => `Must have at least ${n} Karma` }, flag('mod', 'Mod', 'Must be a verified moderator'), flag('gold', 'Gold', 'Must have Reddit Gold')],
  bluesky: [age, atLeast('followers_count', 'Followers', 'follower'), atLeast('statuses_count', 'Posts', 'post')],
  paypal: [age, flag('verified', 'Verified bank account', 'Must have verified bank account')],
  ebay: [age, { field: 'positive_feedback_percentage', label: 'Percentage of positive ratings', detail: n => `Must have at least ${n} percent positive ratings` },
    atLeast('unique_positive_feedback_count', 'Positive feedback reviews', 'positive feedback review'),
    { field: 'unique_negative_feedback_count', label: 'Negative feedback reviews', detail: n => `Must have at most ${plural(n, 'negative feedback review')}`, op: LESS },
    flag('top_rated_seller', 'Top Rated Seller', 'Must be Top Rated Seller')],
  tiktok: [flag('verified', 'Verified', 'Must be verified'), atLeast('follower_count', 'Followers', 'follower'),
    { field: 'following_count', label: 'Following', detail: n => `Must follow at least ${plural(n, 'user')}` }, atLeast('likes_count', 'Likes', 'like')],
}
// The accounts Discord offers for a linked role (the ones people can link today), by name.
const ACCOUNTS = ['amazon-music', 'bluesky', 'bungie', 'crunchyroll', 'domain', 'ebay', 'epicgames', 'facebook', 'github', 'letterboxd', 'paypal', 'playstation', 'reddit',
  'roblox', 'spotify', 'steam', 'twitch', 'twitter', 'xbox', 'youtube'].sort((a, b) => connectionLabel(a).localeCompare(connectionLabel(b)))

// An app's own numbers and checks: its kinds 1–6 compare a number or date, 7–8 a yes or no (Discord asks for yes).
function appCheck(f: AppField): Check | null {
  const detail = () => f.description ?? ''
  if (f.type === 7 || f.type === 8) return { field: f.key, label: f.name, detail, flag: true }
  const op = ({ 1: LESS, 5: LESS, 2: MORE, 6: MORE, 3: EQUAL, 4: NOT_EQUAL } as Record<number, number>)[f.type]
  return op ? { field: f.key, label: f.name, detail, op } : null
}
const shown = (value: string | null, op: number) => {
  const n = Math.round(Number(value ?? 0))
  return op === MORE ? Math.max(1, n + 1) : op === LESS ? Math.max(0, n - 1) : n
}
const stored = (n: number, op: number) => String(op === MORE ? Math.max(0, n - 1) : op === LESS ? Math.max(1, n + 1) : n)
const keyOf = (l: RoleLink) => `${l.connection_type}:${l.application_id ?? ''}`

/** Discord's groups from the requirements: one group for all of them, or one per account for any one. */
function linkGroups(links: RoleLink[], any: boolean): RoleLink[][] {
  if (!links.length) return []
  if (!any) return [links]
  const by = new Map<string, RoleLink[]>()
  for (const link of links) by.set(keyOf(link), [...by.get(keyOf(link)) ?? [], link])
  return [...by.values()]
}

export function RoleLinks({ guildId, roleId, members, editable, draft, onChange }: {
  guildId: string
  roleId: string
  members: number
  editable: boolean
  /** The unsaved requirements, or null while they're Discord's. */
  draft: RoleLink[][] | null
  onChange: (groups: RoleLink[][]) => void
}) {
  const saved = useAdmin<RoleLink[][]>('roleLinks', { guildId, roleId })
  // The server's apps that link roles; without Manage Server there are none to offer.
  const linked = useAdmin<{ integrations?: Integration[] | null }>('integrations', { guildId })
  if (!draft && !saved.data) return saved.error ? <ReadError error={saved.error} onRetry={saved.reload} /> : <Skeleton label="Loading linked roles" />
  const groups = draft ?? saved.data ?? []
  const links = groups.flat()
  const any = groups.length > 1
  const apps = (linked.data?.integrations ?? []).filter(i => i.application?.role_connections_verification_url)
  const appOf = (id?: string | null) => apps.find(i => i.application?.id === id)
  const accounts = [...new Map(links.map(l => [keyOf(l), l])).values()]
  const set = (next: RoleLink[], nextAny = any) => onChange(linkGroups(next, nextAny))
  const add = (id: string) => {
    const app = id.startsWith('app:') ? id.slice(4) : null
    set([...links, { connection_type: app ? APP : id, connection_metadata_field: null, operator: null, value: null, application_id: app }])
  }
  const put = (base: RoleLink, check: Check, on: boolean, n: number) => {
    const rest = links.filter(l => !(keyOf(l) === keyOf(base) && l.connection_metadata_field === check.field))
    const op = check.flag ? EQUAL : check.op ?? MORE
    set(on ? [...rest, { connection_type: base.connection_type, application_id: base.application_id ?? null, connection_metadata_field: check.field, operator: op, value: check.flag ? '1' : stored(n, op) }] : rest)
  }
  const used = new Set(accounts.map(keyOf))
  const offers: MenuItem[] = [
    ...ACCOUNTS.filter(a => !used.has(`${a}:`)).map(a => ({ id: a, label: connectionLabel(a), section: 'Connections', icon: <ConnectionMark type={a} /> })),
    ...apps.filter(i => !used.has(`${APP}:${i.application?.id}`)).map(i => ({ id: `app:${i.application?.id}`, label: i.application?.name ?? 'App', section: 'Apps',
      icon: <Avatar src={i.application?.bot ? avatarUrl(i.application.bot, 16) : null} name={i.application?.name ?? 'App'} status={null} size={16} /> })),
  ]
  return (
    <section className="exo-dc-perm-group" aria-label="Create linked role">
      <div className="exo-dc-link-top">
        <h3>Create linked role</h3>
        {links.length ? <button type="button" className="exo-set-btn is-compact" disabled={!editable} onClick={() => onChange([])}>Clear all</button> : null}
      </div>
      <p className="exo-dc-note">Linking a connection or app requires members to connect their accounts before being added to the role.</p>
      {members > 0 ? <p className="exo-dc-note">Adding connection or app requirements to an existing role is only possible if the role has no members. Consider creating a new role instead.</p> : null}
      {links.length ? (
        <Field label="Requirements">
          <div className="exo-shelf-chips" role="radiogroup" aria-label="Requirements">
            {[{ v: true, label: 'Must connect at least one' }, { v: false, label: 'Must connect all' }].map(o => (
              <button key={o.label} type="button" role="radio" className="exo-shelf-chip" disabled={!editable || accounts.length < 2} aria-checked={any === o.v} aria-pressed={any === o.v}
                onClick={() => set(links, o.v)}>{o.label}</button>
            ))}
          </div>
        </Field>
      ) : null}
      {accounts.map(base => {
        const app = base.connection_type === APP ? appOf(base.application_id) : undefined
        const name = base.connection_type === APP ? app?.application?.name ?? 'an app' : connectionLabel(base.connection_type)
        const checks = base.connection_type === APP ? (app?.role_connections_metadata ?? []).map(appCheck).filter(c => c !== null) : CHECKS[base.connection_type] ?? []
        return (
          <div key={keyOf(base)}>
            <div className="exo-dc-settings-row exo-dc-link-head">
              {base.connection_type === APP
                ? <Avatar src={app?.application?.bot ? avatarUrl(app.application.bot, 24) : null} name={name} status={null} size={24} />
                : <ConnectionMark type={base.connection_type} />}
              <span className="exo-dc-settings-copy"><strong>{`Must connect ${name} account`}</strong></span>
              <button type="button" className="exo-set-btn is-compact" disabled={!editable} onClick={() => set(links.filter(l => keyOf(l) !== keyOf(base)))}>Remove</button>
            </div>
            <div className="exo-dc-link-checks">
              {checks.map(check => <CheckRow key={check.field} check={check} link={links.find(l => keyOf(l) === keyOf(base) && l.connection_metadata_field === check.field)}
                editable={editable} onChange={(on, n) => put(base, check, on, n)} />)}
            </div>
          </div>
        )
      })}
      {links.length < MAX ? (
        <span className="exo-dc-settings-buttons">
          <Menu label="Add requirement" anchor="--exo-dc-add-link" offset={4} triggerClassName={`exo-set-btn${links.length ? '' : ' is-primary'}`} trigger="Add requirement"
            disabled={!editable || (members > 0 && !links.length) || !offers.length} items={offers} filter={{ section: 'Connections', label: 'Search connections' }} onAction={add} />
        </span>
      ) : null}
    </section>
  )
}

// A number or check of the account: its switch, and for a number how much (the text stays as typed until it leaves).
function CheckRow({ check, link, editable, onChange }: { check: Check; link: RoleLink | undefined; editable: boolean; onChange: (on: boolean, n: number) => void }) {
  const op = check.op ?? MORE
  const least = op === MORE ? 1 : op === LESS ? 0 : -Infinity
  const n = shown(link?.value ?? null, op)
  const [text, setText] = useState<string | null>(null)
  const clamp = (raw: string) => Math.max(least, Math.round(Number(raw) || 0))
  return (
    <Field label={check.label} detail={check.detail(text !== null && /^-?\d+$/.test(text) ? clamp(text) : n)}>
      <span className="exo-dc-settings-buttons">
        {check.flag ? null : (
          <input type="number" className="exo-dc-modal-input exo-dc-field is-number" min={Number.isFinite(least) ? least : undefined} step={1} value={text ?? n}
            disabled={!editable || !link} aria-label={`${check.label}, count`}
            onChange={event => { setText(event.target.value); if (/^-?\d+$/.test(event.target.value)) onChange(true, clamp(event.target.value)) }}
            onBlur={() => { if (text !== null) onChange(true, clamp(text)); setText(null) }} />
        )}
        <Switch checked={!!link} disabled={!editable} label={check.label} onChange={on => onChange(on, n)} />
      </span>
    </Field>
  )
}
