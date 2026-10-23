import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { useGlide } from '../../lib/glide'
import { avatarHashOf, avatarUrl, type DiscordEffectItem, type DiscordHome, type DiscordPaymentChoice, type DiscordShopCategory, type DiscordShopItem, type DiscordShopPrice, type DiscordShopProduct } from '../../lib/discord'
import { Menu } from '../ui/Menu'
import { useLayer } from './admin'
import { Icons } from './icons'
import { Avatar } from './presence'
import { effectSkuOf, lookOf, Nameplate, primeEffect, StyledName } from './collectibles'
import { ProfileCard, profileName, profileOf, useProfile, type Loaded } from './profileParts'
import { AddCard, useConfirmPayment } from './payFrame'
import { ModalBack } from './SettingsLayer'

/**
 * Discord's Shop in the Discord room: its catalog by category, one kind of collectible, or what you own, each
 * shown on your own profile, picture or name, with its price and Nitro price. What you own you put on or take off
 * here, as Discord's profile settings do. Buying uses a card already saved on your Discord account, after a
 * confirm step with Discord's exact price; Exo never takes or sees card numbers, and a payment the bank must
 * confirm (or any other payment method) finishes in Discord's own shop.
 */

type Me = NonNullable<DiscordHome['me']>
export type Kind = 'decoration' | 'effect' | 'nameplate'
type Tab = 'featured' | Kind | 'collection'
type Shop = { categories: DiscordShopCategory[]; owned: DiscordShopProduct[] }

export const KINDS: Record<number, Kind> = { 0: 'decoration', 1: 'effect', 2: 'nameplate' }
const KIND_NAMES: Record<Kind, string> = { decoration: 'Avatar Decoration', effect: 'Profile Effect', nameplate: 'Nameplate' }
const TABS: [Tab, string][] = [['featured', 'Featured'], ['decoration', 'Avatar Decorations'], ['effect', 'Profile Effects'], ['nameplate', 'Nameplates'], ['collection', 'Your Collection']]

let shopLoad: Promise<Shop> | null = null
function loadShop(again = false) {
  if (!shopLoad || again) {
    shopLoad = host.discordShop('catalog').then(result => {
      if (!result.ok || !result.catalog) throw new Error(result.message ?? "Discord didn't send the Shop. Try again.")
      return { categories: result.catalog.categories, owned: result.owned ?? [] }
    })
    shopLoad.catch(() => { shopLoad = null })
  }
  return shopLoad
}

/** The Shop's catalog and what the account owns, read once per session (again on reload). */
export function useShop() {
  const [shop, setShop] = useState<Shop | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback((again = false) => {
    setError(null)
    loadShop(again).then(setShop, (err: unknown) => { report(err, 'Shop.load'); setError(err instanceof Error ? err.message : "Discord didn't send the Shop. Try again.") })
  }, [])
  useEffect(() => load(), [load])
  return { shop, error, reload: () => load(true) }
}

/** The collectibles a product holds: its own items, or its first variant's. */
export function itemsOf(product: DiscordShopProduct): DiscordShopItem[] {
  return product.items?.length ? product.items : product.variants?.[0]?.items ?? []
}

const shown = (product: DiscordShopProduct) => itemsOf(product).some(item => KINDS[item.type])

/** What you wear now (decoration, nameplate, profile effect), from your profile; read again when your account changes. */
export function useWorn(meId: string | null | undefined) {
  const [worn, setWorn] = useState<Record<Kind, string | null> | null>(null)
  useEffect(() => {
    if (!meId) return
    let live = true
    const load = () => host.discordProfile(meId, null).then(result => {
      const profile = result.profile
      if (!live || !profile) return
      const look = lookOf(profile.user)
      setWorn({ decoration: look?.decoration?.skuId ?? null, nameplate: look?.nameplate?.skuId ?? null, effect: effectSkuOf([profile.user_profile?.collectibles]) })
    }).catch((err: unknown) => report(err, 'Shop.worn'))
    void load()
    const off = onHostEvent('discord.changed', event => { if (!event.guildId && !event.voice) void load() })
    return () => { live = false; off() }
  }, [meId])
  const put = useCallback((kind: Kind, skuId: string | null) => setWorn(current => current && { ...current, [kind]: skuId }), [])
  return { worn: worn ?? { decoration: null, nameplate: null, effect: null }, put }
}

/** Puts an owned collectible on (skuId) or takes it off (null); Discord's reason when it refuses. */
export async function wear(kind: Kind, skuId: string | null) {
  const result = await host.discordShop('equip', { kind, skuId }).catch(() => ({ ok: false, message: null }))
  return result.ok ? null : result.message ?? "Discord didn't take that. Try again."
}

/** Your own profile as previews show it: Discord's, or (until it arrives) your name and picture. */
export function useMine(me: Me | null): Loaded | null {
  const { data } = useProfile(me?.id ?? '', null)
  if (!me) return null
  return data ?? { profile: profileOf({ id: me.id, username: me.name, global_name: me.name, avatar: avatarHashOf(me.avatar) }), extras: null, note: null }
}

/** Your profile with these collectibles on, in place of what you wear of the same kind. */
export function wearing(mine: Loaded, items: DiscordShopItem[]): Loaded {
  const decoration = items.find(i => i.type === 0)
  const effect = items.find(i => i.type === 1)
  const plate = items.find(i => i.type === 2)
  if (effect?.effects) primeEffect(effect as DiscordEffectItem)
  const { profile } = mine
  return {
    ...mine,
    profile: {
      ...profile,
      user: {
        ...profile.user,
        avatar_decoration_data: decoration?.asset ? { asset: decoration.asset, sku_id: decoration.sku_id } : profile.user.avatar_decoration_data,
        collectibles: plate ? { nameplate: { sku_id: plate.sku_id, asset: plate.asset, palette: plate.palette } } : profile.user.collectibles,
      },
      user_profile: effect
        ? { ...profile.user_profile, collectibles: [...(profile.user_profile?.collectibles ?? []).filter(c => c.type !== 1), { sku_id: effect.sku_id, type: 1 }] }
        : profile.user_profile,
    },
  }
}

export function ShopPage({ me }: { me: Me | null }) {
  const { shop, error, reload } = useShop()
  const { worn, put } = useWorn(me?.id)
  const mine = useMine(me)
  const nitro = useNitro()
  const [tab, setTab] = useState<Tab>('featured')
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState<DiscordShopProduct | null>(null)
  const tabs = useGlide<HTMLSpanElement>()
  const owned = useMemo(() => new Set(shop?.owned.flatMap(p => [p.sku_id, ...itemsOf(p).map(i => i.sku_id)]) ?? []), [shop])
  const q = query.trim().toLowerCase()
  const match = (product: DiscordShopProduct) => shown(product) && (!q || product.name.toLowerCase().includes(q))
  const card = (product: DiscordShopProduct) => (
    <ProductCard key={product.sku_id} product={product} mine={mine} nitro={nitro} owned={owned.has(product.sku_id)} worn={isWorn(product, worn)} onOpen={() => setOpen(product)} />
  )
  return (
    <section className="exo-dc-chat exo-dc-shop" aria-label="Shop">
      <header className="exo-dc-chat-head">
        <Icons.Shop size={22} />
        <strong>Shop</strong>
        <span className="exo-dc-head-line" aria-hidden="true" />
        <span ref={tabs} className="exo-dc-tabs exo-glide" role="group" aria-label="Shop">
          {TABS.map(([id, label]) => <button key={id} type="button" className="exo-dc-tab" aria-pressed={tab === id} onClick={() => setTab(id)}>{label}</button>)}
        </span>
        <label className="exo-dc-friend-search exo-dc-shop-search">
          <Icons.Search size={16} />
          <input placeholder="Search the Shop" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search the Shop" />
        </label>
      </header>
      <div className="exo-dc-shop-body">
        {error ? <p className="exo-dc-note" role="alert">{error} <button type="button" className="exo-dc-link" onClick={reload}>Try again</button></p> : null}
        {!shop && !error ? <p className="exo-dc-note" role="status">Loading the Shop</p> : null}
        {shop && tab === 'featured' ? shop.categories.map(category => {
          const products = category.products.filter(match)
          return products.length ? (
            <section key={category.sku_id} className="exo-dc-shop-category" aria-label={category.name}>
              <CategoryHead category={category} />
              <div className="exo-dc-shop-grid">{products.map(card)}</div>
            </section>
          ) : null
        }) : null}
        {shop && (tab === 'decoration' || tab === 'effect' || tab === 'nameplate') ? (
          <div className="exo-dc-shop-grid">{unique(shop.categories.flatMap(c => c.products)).filter(p => match(p) && kindOf(p) === tab).map(card)}</div>
        ) : null}
        {shop && tab === 'collection' ? <Collection products={shop.owned.filter(match)} card={card} /> : null}
      </div>
      {open && shop && mine ? (
        <ProductDialog product={open} mine={mine} nitro={nitro} owned={owned.has(open.sku_id)} worn={worn} onClose={() => setOpen(null)} onWorn={put} onBought={reload} />
      ) : null}
    </section>
  )
}

// Whether the account has Nitro, for which price to lead with.
function useNitro() {
  const [nitro, setNitro] = useState(false)
  useEffect(() => {
    let live = true
    host.discordAccount('me').then(result => { if (live) setNitro(!!result.me?.premium) }).catch(() => { })
    return () => { live = false }
  }, [])
  return nitro
}

function Collection({ products, card }: { products: DiscordShopProduct[]; card: (product: DiscordShopProduct) => React.ReactNode }) {
  const groups: [Kind, string][] = [['decoration', 'Avatar Decorations'], ['effect', 'Profile Effects'], ['nameplate', 'Nameplates']]
  if (!products.length) return <p className="exo-dc-note">Collectibles you own show here.</p>
  return (
    <>
      {groups.map(([kind, label]) => {
        const mine = products.filter(p => kindOf(p) === kind)
        return mine.length ? (
          <section key={kind} className="exo-dc-shop-category" aria-label={label}>
            <h2 className="exo-dc-shop-heading">{label}</h2>
            <div className="exo-dc-shop-grid">{mine.map(card)}</div>
          </section>
        ) : null
      })}
    </>
  )
}

// A category's art (its name is in the art, as in Discord's Shop), or its name and words without any.
function CategoryHead({ category }: { category: DiscordShopCategory & { catalog_banner_url?: string | null } }) {
  if (category.catalog_banner_url) return <div className="exo-dc-shop-banner" role="img" aria-label={category.name} style={{ backgroundImage: `url("${category.catalog_banner_url}")` }} />
  return (
    <div className="exo-dc-shop-banner">
      <h2 className="exo-dc-shop-heading">{category.name}</h2>
      {category.summary ? <p>{category.summary}</p> : null}
    </div>
  )
}

function unique(products: DiscordShopProduct[]) {
  const seen = new Set<string>()
  return products.filter(p => !seen.has(p.sku_id) && seen.add(p.sku_id))
}

// A single collectible's kind; a bundle (several kinds) has none.
function kindOf(product: DiscordShopProduct): Kind | null {
  const kinds = new Set(itemsOf(product).map(i => KINDS[i.type]).filter(Boolean))
  return kinds.size === 1 ? [...kinds][0] : null
}

function isWorn(product: DiscordShopProduct, worn: Record<Kind, string | null>) {
  return itemsOf(product).some(item => KINDS[item.type] && worn[KINDS[item.type]] === item.sku_id)
}

function money(amount: number, currency: string, exponent = 2) {
  return new Intl.NumberFormat(undefined, { style: 'currency', currency: currency.toUpperCase() }).format(amount / 10 ** exponent)
}

// Its price without Nitro and with it (Discord's premium price, key 4), in money (not Orbs).
function pricesOf(product: DiscordShopProduct) {
  const one = (key: string) => product.prices?.[key]?.country_prices.prices.find(p => p.currency !== 'discord_orb')
  return { regular: one('0'), member: one('4') }
}

/** The price you pay (with Nitro, the Nitro price, the usual one struck through), and the Nitro price when it's lower. */
function Price({ product, nitro, large = false }: { product: DiscordShopProduct; nitro: boolean; large?: boolean }) {
  const { regular, member } = pricesOf(product)
  if (!regular) return null
  const cheaper = member && member.amount < regular.amount ? member : null
  const pay = nitro && cheaper ? cheaper : regular
  return (
    <span className={`exo-dc-shop-price${large ? ' is-large' : ''}`}>
      <span className="exo-dc-shop-amount">
        <strong>{money(pay.amount, pay.currency, pay.exponent)}</strong>
        {pay !== regular ? <s>{money(regular.amount, regular.currency, regular.exponent)}</s> : null}
      </span>
      {cheaper ? (
        <span className="exo-dc-shop-nitro">
          <Icons.Nitro size={12} />
          {pay === cheaper ? 'Your Nitro price' : `${money(cheaper.amount, cheaper.currency, cheaper.exponent)} with Nitro`}
        </span>
      ) : null}
    </span>
  )
}

// Discord's backdrop for a product (its own two colors, top to bottom), behind its preview as in Discord's Shop.
const hex = (color: number) => `#${color.toString(16).padStart(6, '0')}`
function stage(product: DiscordShopProduct): React.CSSProperties | undefined {
  const [from, to = from] = product.styles?.background_colors ?? product.variants?.[0]?.styles?.background_colors ?? []
  return from == null ? undefined : { background: `linear-gradient(${hex(from)}, ${hex(to)})` }
}

/** Your name on its nameplate, as a member list row shows it. */
export function PlateRow({ data, scope }: { data: Loaded; scope?: string }) {
  const look = lookOf(data.profile.user)
  const name = profileName(data)
  return (
    <span className="exo-dc-shop-plate has-nameplate">
      <Nameplate look={look} scope={scope ?? '.exo-dc-shop-plate'} />
      <Avatar src={avatarUrl(data.profile.user, 32)} name={name} status={null} size={32} look={look} scope={scope ?? '.exo-dc-shop-plate'} />
      <span className="exo-dc-shop-plate-name"><StyledName name={name} look={look} scope={scope} /></span>
    </span>
  )
}

/** The product on you: an effect (or a bundle) on your profile card, a decoration on your picture, a nameplate on your name. */
function Preview({ product, mine, scope }: { product: DiscordShopProduct; mine: Loaded; scope: string }) {
  const items = itemsOf(product)
  const kinds = new Set(items.map(i => KINDS[i.type]).filter(Boolean))
  const worn = wearing(mine, items)
  if (kinds.has('effect') || kinds.size > 1) {
    return (
      <span className="exo-dc-shop-preview is-card" style={stage(product)}>
        <ProfileCard data={worn} compact scope={scope} />
        {kinds.has('nameplate') ? <PlateRow data={worn} scope={scope} /> : null}
      </span>
    )
  }
  if (kinds.has('decoration')) {
    return <span className="exo-dc-shop-preview" style={stage(product)}><Avatar src={avatarUrl(worn.profile.user, 96)} name={profileName(worn)} status={null} size={96} look={lookOf(worn.profile.user)} scope={scope} /></span>
  }
  return <span className="exo-dc-shop-preview" style={stage(product)}><PlateRow data={worn} scope={scope} /></span>
}

function ProductCard({ product, mine, nitro, owned, worn, onOpen }: { product: DiscordShopProduct; mine: Loaded | null; nitro: boolean; owned: boolean; worn: boolean; onOpen: () => void }) {
  return (
    <button type="button" className="exo-dc-shop-card" onClick={onOpen}>
      {mine ? <Preview product={product} mine={mine} scope=".exo-dc-shop-card" /> : <span className="exo-dc-shop-preview" />}
      <span className="exo-dc-shop-card-text">
        <strong>{product.name}</strong>
        {worn ? <span className="exo-dc-shop-state">Equipped</span> : owned ? <span className="exo-dc-shop-state">Owned</span> : <Price product={product} nitro={nitro} />}
      </span>
    </button>
  )
}

// What it is: one collectible's own description, or what a bundle holds.
function About({ product }: { product: DiscordShopProduct }) {
  const items = itemsOf(product).filter(i => KINDS[i.type])
  if (items.length === 1) {
    const words = items[0].label ?? items[0].accessibilityLabel
    return <>{<span className="exo-dc-shop-kind">{KIND_NAMES[KINDS[items[0].type]]}</span>}{words ? <p>{words}</p> : null}</>
  }
  const parts = product.bundled_products?.filter(b => KINDS[b.type]) ?? []
  return (
    <>
      <span className="exo-dc-shop-kind">Bundle</span>
      <ul className="exo-dc-shop-includes">
        {parts.map(part => <li key={part.sku_id}><span>{KIND_NAMES[KINDS[part.type]]}</span>{part.name}</li>)}
      </ul>
    </>
  )
}

/**
 * A product's page, as Discord opens one: the product on your full profile card beside what it is and its
 * price, then Use or Remove for what you own, or Buy, which checks out with a card on your Discord account.
 */
function ProductDialog({ product, mine, nitro, owned, worn, onClose, onWorn, onBought }: {
  product: DiscordShopProduct
  mine: Loaded
  nitro: boolean
  owned: boolean
  worn: Record<Kind, string | null>
  onClose: () => void
  onWorn: (kind: Kind, skuId: string | null) => void
  onBought: () => void
}) {
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [step, setStep] = useState<'view' | 'checkout' | 'bought'>('view')
  const items = itemsOf(product).filter(i => KINDS[i.type])
  const dressed = wearing(mine, items)
  const put = (item: DiscordShopItem, on: boolean) => {
    const kind = KINDS[item.type]
    setBusy(true); setError(null)
    void wear(kind, on ? item.sku_id : null).then(problem => {
      setBusy(false)
      if (problem) setError(problem)
      else onWorn(kind, on ? item.sku_id : null)
    })
  }
  const yours = owned || step === 'bought'
  return (
    <ModalBack onClose={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-shop-dialog" role="dialog" aria-modal="true" aria-label={product.name} tabIndex={-1} onClick={event => event.stopPropagation()}>
        <div className="exo-dc-shop-dialog-preview" style={stage(product)}>
          <ProfileCard data={dressed} />
          {items.some(i => i.type === 2) ? <PlateRow data={dressed} /> : null}
        </div>
        <div className="exo-dc-shop-dialog-side">
          <h2 className="exo-dc-shop-heading">{product.name}</h2>
          {step === 'checkout' ? null : <About product={product} />}
          {step === 'bought' ? <p className="exo-dc-shop-done">It's yours. Put it on below.</p> : null}
          {yours ? null : <Price product={product} nitro={nitro} large />}
          {step === 'checkout' ? <Checkout product={product} onBack={() => setStep('view')} onBought={() => { setStep('bought'); onBought() }} /> : null}
          {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
          {step === 'checkout' ? null : (
            <div className="exo-dc-shop-actions">
              <button type="button" className="exo-set-btn" onClick={onClose}>Close</button>
              {yours ? items.map(item => {
                const on = worn[KINDS[item.type]] === item.sku_id
                const what = items.length > 1 ? ` ${KIND_NAMES[KINDS[item.type]]}` : ''
                return on
                  ? <button key={item.sku_id} type="button" className="exo-set-btn" disabled={busy} onClick={() => put(item, false)}>Remove{what}</button>
                  : <button key={item.sku_id} type="button" className="button button--primary" disabled={busy} onClick={() => put(item, true)}>Use{what}</button>
              }) : <button type="button" className="button button--primary" onClick={() => { setError(null); setStep('checkout') }}>Buy</button>}
            </div>
          )}
        </div>
      </div>
    </ModalBack>
  )
}

// Discord's price for it: before tax, the tax, and the total that Buy charges.
function Bill({ price, currency }: { price: DiscordShopPrice; currency: string }) {
  return (
    <dl className="exo-dc-shop-bill">
      {price.subtotal != null ? <><dt>Price</dt><dd>{money(price.subtotal, currency)}</dd></> : null}
      {price.tax ? <><dt>{price.taxInclusive ? 'Tax (included)' : 'Tax'}</dt><dd>{money(price.tax, currency)}</dd></> : null}
      <dt>Total</dt><dd><strong>{money(price.amount ?? 0, currency)}</strong></dd>
    </dl>
  )
}

// The card checkout starts on: the account's default, else the first one Exo can pay with.
const firstCard = (list: DiscordPaymentChoice[]) => (list.find(c => c.usable && c.default) ?? list.find(c => c.usable))?.id ?? null

/**
 * Checkout with a card on your Discord account: pick it (or add one, in Stripe's own field), see Discord's own price
 * for it (tax included), and confirm. Buy charges exactly that price, or Discord refuses it. A payment your bank must
 * confirm shows the bank's check over the window, then Discord completes it.
 */
function Checkout({ product, onBack, onBought }: { product: DiscordShopProduct; onBack: () => void; onBought: () => void }) {
  const [choices, setChoices] = useState<DiscordPaymentChoice[] | null>(null)
  const [source, setSource] = useState<string | null>(null)
  const [quote, setQuote] = useState<{ source: string; price: DiscordShopPrice } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const bank = useConfirmPayment(problem => {
    setBusy(false)
    if (problem) setError(problem)
    else onBought()
  })
  useEffect(() => {
    let live = true
    host.discordShop('payment').then(result => {
      if (!live) return
      if (!result.ok) { setError(result.message ?? "Discord didn't send your payment methods. Try again."); setChoices([]); return }
      const list = result.payment ?? []
      setChoices(list)
      setSource(firstCard(list))
    }).catch(() => { if (live) { setChoices([]); setError("Discord didn't send your payment methods. Try again.") } })
    return () => { live = false }
  }, [])
  useEffect(() => {
    if (!source) return
    let live = true
    host.discordShop('preview', { skuId: product.sku_id, paymentSourceId: source }).then(result => {
      if (!live) return
      if (result.ok && result.price?.amount != null && result.price.currency) setQuote({ source, price: result.price })
      else setError(result.message ?? "Discord didn't give a price. Try again.")
    }).catch(() => { if (live) setError("Discord didn't give a price. Try again.") })
    return () => { live = false }
  }, [source, product.sku_id])
  const usable = choices?.filter(c => c.usable) ?? []
  const card = usable.find(c => c.id === source)
  const price = quote?.source === source ? quote.price : null
  const buy = () => {
    if (!card || !price?.amount || !price.currency) return
    setBusy(true); setError(null)
    void host.discordShop('buy', { skuId: product.sku_id, paymentSourceId: card.id, amount: price.amount, currency: price.currency }).then(result => {
      setBusy(false)
      if (result.ok) { onBought(); return }
      // Your bank wants to confirm it: its check shows over the window, then Discord completes the purchase.
      if (result.confirm && result.paymentId) { setBusy(true); bank.confirm(result.paymentId); return }
      setError(result.message ?? "Discord didn't complete the purchase. Nothing was charged.")
    }, () => { setBusy(false); setError("Exo couldn't reach Discord. Check Your Collection before trying again.") })
  }
  const added = (list: DiscordPaymentChoice[]) => {
    setAdding(false); setError(null); setChoices(list)
    setSource(firstCard(list))
  }
  if (choices === null) return <p className="exo-dc-pay-note" role="status">Loading your payment methods</p>
  if (!usable.length || adding) {
    return (
      <div className="exo-dc-shop-checkout">
        <AddCard onSaved={added} onCancel={() => { if (usable.length) setAdding(false); else onBack() }} />
      </div>
    )
  }
  const total = price?.amount != null && price.currency ? money(price.amount, price.currency) : null
  return (
    <div className="exo-dc-shop-checkout">
      <div className="exo-dc-shop-line">
        <span>Pay with</span>
        <Menu label="Pay with" anchor="--exo-dc-pay-with" offset={4} triggerClassName="exo-set-btn" trigger={card ? card.label : 'Choose a card'}
          items={usable.map(c => ({ id: c.id, label: c.expires ? `${c.label} · ${c.expires}` : c.label }))} checked={source ?? ''} disabled={busy}
          onAction={id => { setError(null); setSource(id) }} />
        <button type="button" className="exo-set-btn" disabled={busy} onClick={() => setAdding(true)}>Add a card</button>
      </div>
      {price?.currency ? <Bill price={price} currency={price.currency} /> : <p className="exo-dc-pay-note" role="status">Getting Discord's price</p>}
      {error ? <p className="exo-dc-pay-note is-bad" role="alert">{error}</p> : null}
      <div className="exo-dc-shop-actions">
        <button type="button" className="exo-set-btn" disabled={busy} onClick={onBack}>Back</button>
        <button type="button" className="button button--primary" disabled={busy || !total || !bank.ready} onClick={buy}>{busy ? 'Buying' : total ? `Buy for ${total}` : 'Buy'}</button>
      </div>
      {bank.frame}
    </div>
  )
}
