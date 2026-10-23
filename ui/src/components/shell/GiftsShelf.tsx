import { memo, useEffect, useState } from 'react'
import { Gift } from '../../brand/icons'
import { host, onHostEvent, type StoreGift } from '../../lib/host'
import { report } from '../../lib/report'

type Busy = 'accept' | 'decline' | 'confirm'

const keyOf = (gift: StoreGift) => `${gift.store}:${gift.id}`

/**
 * Gifts (DESIGN.md › Library): games someone sent you on Steam or Epic, waiting to be accepted. Shows only while
 * one waits. Accept adds the game to your library (the store is read again, so it appears); Decline asks once,
 * since the sender is refunded.
 */
export const GiftsShelf = memo(function GiftsShelf() {
  const gifts = useGifts()
  // Answered gifts stay a moment with their result line, then leave.
  const [answered, setAnswered] = useState<Record<string, string>>({})
  const shown = gifts.filter(gift => answered[keyOf(gift)] !== '')
  if (shown.length === 0) return null
  return (
    <section className="exo-gifts" aria-labelledby="exo-gifts-title">
      <h2 className="exo-section-title" id="exo-gifts-title">Gifts</h2>
      <ul className="exo-gifts-row">
        {shown.map(gift => (
          <GiftCard
            key={keyOf(gift)}
            gift={gift}
            done={answered[keyOf(gift)] ?? null}
            onDone={line => {
              setAnswered(all => ({ ...all, [keyOf(gift)]: line }))
              window.setTimeout(() => setAnswered(all => ({ ...all, [keyOf(gift)]: '' })), 2400)
            }}
          />
        ))}
      </ul>
    </section>
  )
})

function GiftCard({ gift, done, onDone }: { gift: StoreGift; done: string | null; onDone: (line: string) => void }) {
  const [busy, setBusy] = useState<Busy | null>(null)
  const [error, setError] = useState<string | null>(null)
  async function answer(accept: boolean) {
    setBusy(accept ? 'accept' : 'decline')
    setError(null)
    try {
      const result = await host.giftsAnswer(gift.store, gift.id, accept)
      if (result.ok) onDone(result.message ?? (accept ? 'Added to your library.' : 'Declined.'))
      else setError(result.message ?? 'That did not finish. Try again.')
    } catch (cause) {
      report(cause, 'GiftsShelf.answer')
      setError('That did not finish. Try again.')
    } finally {
      setBusy(null)
    }
  }
  const line = done ?? error ?? (busy === 'confirm' || busy === 'decline' ? `Declining refunds ${gift.sender ?? 'the sender'}.` : null)
  return (
    <li className="exo-gift">
      <div className="exo-gift-art">
        {gift.image ? <img src={gift.image} alt="" draggable={false} loading="lazy" /> : <Gift size={28} aria-hidden="true" />}
      </div>
      <div className="exo-gift-text">
        <strong>{gift.title}</strong>
        {gift.sender ? <span>From {gift.sender}</span> : null}
        {gift.message ? <p className="exo-gift-message">{gift.message}</p> : null}
        {line ? <p className={`exo-gift-line${error && !done ? ' is-bad' : ''}`} role="status">{line}</p> : null}
      </div>
      {done ? null : (
        <div className="exo-gift-actions">
          {busy === 'confirm' || busy === 'decline' ? (
            <>
              <button type="button" className="exo-set-btn" disabled={busy === 'decline'} onClick={() => setBusy(null)}>Keep</button>
              <button type="button" className="exo-set-btn is-danger" disabled={busy === 'decline'} onClick={() => void answer(false)}>
                {busy === 'decline' ? 'Declining…' : 'Decline'}
              </button>
            </>
          ) : (
            <>
              <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => { setError(null); setBusy('confirm') }}>Decline</button>
              <button type="button" className="exo-set-btn is-primary" disabled={busy !== null} onClick={() => void answer(true)}>
                {busy === 'accept' ? 'Accepting…' : 'Accept'}
              </button>
            </>
          )}
        </div>
      )}
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
