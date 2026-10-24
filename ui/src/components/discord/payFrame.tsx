import { useEffect, useMemo, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { ChevronDown } from '../../brand/icons'
import { Menu } from '../ui/Menu'
import type { DiscordPaymentChoice } from '../../lib/discord'

// The card form's own origin (wwwroot/pay): the only page that loads Stripe. Exo hears back only what it says.
const PAY = 'https://pay.exo-launcher.local'
type PayMessage =
  | { type: 'loaded' | 'ready' | 'saved' | 'paid'; paymentMethod?: string }
  | { type: 'change'; complete: boolean; message: string | null }
  | { type: 'wide'; on: boolean }
  | { type: 'error'; message: string }

/**
 * Stripe's card field in its own frame. While Stripe may show the bank's check (3-D Secure) the frame covers the
 * window, as Stripe's own popup would, and comes back after.
 */
function usePayFrame(onMessage: (message: PayMessage) => void, hidden = false) {
  const frame = useRef<HTMLIFrameElement>(null)
  const [wide, setWide] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const latest = useRef(onMessage)
  useEffect(() => { latest.current = onMessage })
  useEffect(() => {
    const listen = (event: MessageEvent) => {
      if (event.origin !== PAY || event.source !== frame.current?.contentWindow) return
      const message = event.data as PayMessage
      if (message.type === 'wide') setWide(message.on)
      if (message.type === 'loaded') setLoaded(true)
      latest.current(message)
    }
    window.addEventListener('message', listen)
    return () => window.removeEventListener('message', listen)
  }, [])
  const send = (message: object) => frame.current?.contentWindow?.postMessage(message, PAY)
  const element = (
    <iframe ref={frame} className={`exo-dc-pay-frame${wide ? ' is-wide' : hidden ? ' is-hidden' : ''}`} src={`${PAY}/index.html`} title="Card"
      allow="payment" />
  )
  return { element, send, loaded }
}

// Exo's field look (DESIGN.md › Controls), for Stripe's field to wear: the frame can't read Exo's styles itself.
function fieldLook() {
  const root = getComputedStyle(document.documentElement)
  const token = (name: string, fallback: string) => root.getPropertyValue(name).trim() || fallback
  const scale = Number.parseFloat(root.getPropertyValue('--exo-scale')) || 1
  return { fill: token('--exo-fill', '#121212'), text: token('--color-fg', '#ffffff'), faint: token('--color-faint', '#747474'), bad: token('--exo-red', '#ff6b6b'), size: `${Math.round(13 * scale)}px` }
}

type Address = { name: string; line1: string; line2: string; city: string; state: string; postalCode: string; country: string }
const EMPTY: Address = { name: '', line1: '', line2: '', city: '', state: '', postalCode: '', country: 'US' }

// Every country Stripe takes cards from, by its two-letter code, named in the user's language.
const COUNTRIES = 'AD AE AF AG AI AL AM AO AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BW BY BZ CA CD CF CG CH CI CK CL CM CN CO CR CV CW CY CZ DE DJ DK DM DO DZ EC EE EG ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GT GU GW GY HK HN HR HT HU ID IE IL IM IN IQ IS IT JE JM JO JP KE KG KH KI KM KN KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NG NI NL NO NP NR NZ OM PA PE PF PG PH PK PL PM PR PS PT PW PY QA RE RO RS RW SA SB SC SE SG SI SK SL SM SN SO SR ST SV SX SZ TC TD TG TH TJ TL TM TN TO TR TT TV TW TZ UA UG US UY UZ VA VC VE VG VI VN VU WF WS XK YE YT ZA ZM ZW'.split(' ')

/**
 * Discord's Add Payment Method, in Exo: the billing address Discord checks, then the card in Stripe's own field.
 * Discord saves the card to your account (Stripe holds the number); Exo and Discord never see it.
 */
export function AddCard({ onSaved, onCancel }: { onSaved: (choices: DiscordPaymentChoice[]) => void; onCancel?: () => void }) {
  const [address, setAddress] = useState<Address>(EMPTY)
  const [complete, setComplete] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const pending = useRef<{ addressToken: string } | null>(null)
  const names = useMemo(() => {
    const display = new Intl.DisplayNames(undefined, { type: 'region' })
    return COUNTRIES.map(code => ({ code, name: display.of(code) ?? code })).sort((a, b) => a.name.localeCompare(b.name))
  }, [])
  const pay = usePayFrame(message => {
    if (message.type === 'change') { setComplete(message.complete); setError(message.message) }
    else if (message.type === 'error') { setBusy(false); setError(message.message) }
    else if (message.type === 'saved' && message.paymentMethod && pending.current) {
      void host.discordShop('cardSave', { paymentMethod: message.paymentMethod, addressToken: pending.current.addressToken, ...address }).then(result => {
        setBusy(false)
        if (result.ok) onSaved(result.payment ?? [])
        else setError(result.message ?? "Discord didn't save the card.")
      }, () => { setBusy(false); setError("Exo couldn't reach Discord. Check your payment methods before trying again.") })
    }
  })
  // The field mounts once the frame has loaded; the key is Discord's own Stripe key.
  const key = useRef<string | null>(null)
  useEffect(() => {
    if (!pay.loaded || key.current) return
    void host.discordShop('stripeKey').then(result => { if (result.key) { key.current = result.key; pay.send({ type: 'mount', key: result.key, look: fieldLook() }) } })
  }, [pay.loaded, pay])
  const set = (key: keyof Address) => (event: React.ChangeEvent<HTMLInputElement>) => setAddress(a => ({ ...a, [key]: event.target.value }))
  const ready = complete && address.name.trim() && address.line1.trim() && address.city.trim() && address.postalCode.trim() && address.country
  const save = () => {
    setBusy(true); setError(null)
    void host.discordShop('card', { ...address }).then(result => {
      if (!result.ok || !result.clientSecret || !result.addressToken) { setBusy(false); setError(result.message ?? "Discord couldn't start adding a card."); return }
      pending.current = { addressToken: result.addressToken }
      pay.send({
        type: 'save', clientSecret: result.clientSecret,
        billing: { name: address.name.trim(), address: { line1: address.line1.trim(), line2: address.line2.trim() || undefined, city: address.city.trim(), state: address.state.trim() || undefined, postal_code: address.postalCode.trim(), country: address.country } },
      })
    }, () => { setBusy(false); setError("Exo couldn't reach Discord. Try again.") })
  }
  const country = names.find(c => c.code === address.country)?.name ?? address.country
  const field = (key: keyof Address, label: string, auto: string, span: 2 | 3 | 4 | 6) => (
    <label className={`exo-dc-pay-field is-${span}`}><span>{label}</span><input className="exo-field" value={address[key]} onChange={set(key)} autoComplete={auto} /></label>
  )
  return (
    <div className="exo-dc-add-card">
      <header className="exo-dc-pay-head">
        <strong>Add a card</strong>
        <span>Saved to your Discord account by Stripe, Discord's payment service</span>
      </header>
      <div className="exo-dc-pay-grid">
        <label className="exo-dc-pay-field is-6"><span>Card</span>{pay.element}</label>
        {field('name', 'Name on card', 'cc-name', 3)}
        <div className="exo-dc-pay-field is-3"><span>Country</span>
          <Menu label="Country" anchor="--exo-dc-pay-country" offset={4} triggerClassName="exo-field exo-dc-pay-select"
            trigger={<><span>{country}</span><ChevronDown size={14} /></>} checked={address.country} filter={{ section: 'Countries', label: 'Find a country' }}
            items={names.map(c => ({ id: c.code, label: c.name, section: 'Countries' }))} onAction={code => setAddress(a => ({ ...a, country: code }))} />
        </div>
        {field('line1', 'Address', 'address-line1', 4)}
        {field('line2', 'Apt (optional)', 'address-line2', 2)}
        {field('city', 'City', 'address-level2', 2)}
        {field('state', 'State or region', 'address-level1', 2)}
        {field('postalCode', 'Postal code', 'postal-code', 2)}
      </div>
      <p className="exo-dc-pay-note">Exo never sees your card number: it goes straight to Stripe.</p>
      {error ? <p className="exo-dc-pay-note is-bad" role="alert">{error}</p> : null}
      <div className="exo-dc-shop-actions">
        {onCancel ? <button type="button" className="exo-set-btn" disabled={busy} onClick={onCancel}>Back</button> : null}
        <button type="button" className="exo-set-btn is-primary" disabled={busy || !ready} onClick={save}>{busy ? 'Saving card' : 'Save card'}</button>
      </div>
    </div>
  )
}

/** A purchase your bank must confirm: Stripe's own check over the window, then Discord completes the purchase. */
export function useConfirmPayment(onDone: (error: string | null) => void) {
  const pay = usePayFrame(message => {
    if (message.type === 'paid') onDone(null)
    else if (message.type === 'error') onDone(message.message)
  }, true)
  const confirm = (paymentId: string) => void host.discordShop('paymentSecret', { paymentId }).then(result => {
    if (!result.ok || !result.clientSecret || !result.key) { onDone(result.message ?? "Discord didn't send the payment to confirm."); return }
    pay.send({ type: 'confirmPayment', key: result.key, clientSecret: result.clientSecret })
  }, () => onDone("Exo couldn't reach Discord. Check Your Collection before trying again."))
  return { frame: pay.element, confirm, ready: pay.loaded }
}
