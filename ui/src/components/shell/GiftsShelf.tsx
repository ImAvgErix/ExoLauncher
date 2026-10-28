import { memo, useEffect, useState } from 'react'
import { Gift } from '../../brand/icons'
import { host, onHostEvent, type StoreGift } from '../../lib/host'
import { notify } from '../../lib/notify'
import { report } from '../../lib/report'
import { confirm } from '../ui/Dialog'
import { Spinner } from '../ui/Spinner'
import { ErrorNote } from '../ui/ErrorNote'

type Busy = 'accept' | 'decline'

const keyOf = (gift: StoreGift) => `${gift.store}:${gift.id}`

/**
 * Gifts (DESIGN.md › Library): games someone sent you on Steam or Epic, waiting to be accepted. Shows only while
 * one waits. Accept adds the game to your library (the store is read again, so it appears); Decline asks first,
 * since the sender is refunded. How it went is said in the notice stack, and the gift leaves.
 */
export const GiftsShelf = memo(function GiftsShelf() {
  const gifts = useGifts()
  // An answered gift leaves at once, before the store's next read drops it.
  const [answered, setAnswered] = useState<ReadonlySet<string>>(() => new Set())
  const shown = gifts.filter(gift => !answered.has(keyOf(gift)))
  if (shown.length === 0) return null
  return (
    <section className="exo-gifts" aria-labelledby="exo-gifts-title">
      <h2 className="exo-section-title" id="exo-gifts-title">Gifts</h2>
      <ul className="exo-gifts-row">
        {shown.map(gift => (
          <GiftCard key={keyOf(gift)} gift={gift} onDone={() => setAnswered(all => new Set(all).add(keyOf(gift)))} />
        ))}
      </ul>
    </section>
  )
})

function GiftCard({ gift, onDone }: { gift: StoreGift; onDone: () => void }) {
  const [busy, setBusy] = useState<Busy | null>(null)
  const [error, setError] = useState<string | null>(null)
  async function answer(accept: boolean) {
    const sure = accept || await confirm({
      title: `Decline ${gift.title}?`,
      body: `${gift.sender ?? 'The sender'} gets a refund, and the gift is gone.`,
      action: 'Decline',
      danger: true,
    })
    if (sure !== true) return
    setBusy(accept ? 'accept' : 'decline')
    setError(null)
    try {
      const result = await host.giftsAnswer(gift.store, gift.id, accept)
      if (result.ok) {
        notify({ title: gift.title, text: result.message ?? (accept ? 'Added to your library.' : 'Declined.'), tone: 'good' })
        onDone()
      } else setError(result.message ?? 'That did not finish. Try again.')
    } catch (cause) {
      report(cause, 'GiftsShelf.answer')
      setError('That did not finish. Try again.')
    } finally {
      setBusy(null)
    }
  }
  return (
    <li className="exo-gift">
      <div className="exo-gift-art">
        {gift.image ? <img src={gift.image} alt="" draggable={false} loading="lazy" /> : <Gift size={28} aria-hidden="true" />}
      </div>
      <div className="exo-gift-text">
        <strong>{gift.title}</strong>
        {gift.sender ? <span>From {gift.sender}</span> : null}
        {gift.message ? <p className="exo-gift-message">{gift.message}</p> : null}
        {error ? <ErrorNote className="exo-gift-line is-bad" message={error} /> : null}
      </div>
      <div className="exo-gift-actions">
        <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void answer(false)}>
          {busy === 'decline' ? <Spinner label={`Declining ${gift.title}`} /> : null}
          Decline
        </button>
        <button type="button" className="exo-set-btn is-primary" disabled={busy !== null} onClick={() => void answer(true)}>
          {busy === 'accept' ? <Spinner label={`Accepting ${gift.title}`} /> : null}
          Accept
        </button>
      </div>
    </li>
  )
}

// What Exo last read, then each read after it (Exo reads both stores every half hour and on Library › Refresh).
function useGifts(): StoreGift[] {
  const [gifts, setGifts] = useState<StoreGift[]>([])
  useEffect(() => {
    let live = true
    host.giftsList().then(result => { if (live) setGifts(result.gifts) }).catch(() => { })
    const off = onHostEvent('gifts.changed', result => setGifts(result.gifts))
    return () => { live = false; off() }
  }, [])
  return gifts
}
