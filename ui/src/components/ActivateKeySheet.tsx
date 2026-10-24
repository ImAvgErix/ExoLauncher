import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button } from './ui/Button'
import { host } from '../lib/host'
import { report } from '../lib/report'
import { useGlide } from '../lib/glide'
import { useDismiss } from '../lib/dismiss'

const STORES = ['steam', 'epic', 'gog'] as const
const STORE_LABELS: Record<(typeof STORES)[number], string> = { steam: 'Steam', epic: 'Epic', gog: 'GOG' }
/** What pressing Open does: each store keeps the final confirmation for itself. */
const STORE_STEPS: Record<(typeof STORES)[number], string> = {
  steam: 'Opens Steam with the key filled in. You accept Steam\'s agreement and press Continue.',
  epic: 'Opens Epic\'s redeem page in your browser and copies the key. You paste it and press Redeem.',
  gog: 'Opens GOG in your browser with the key filled in. You press Continue, then Redeem.',
}

/**
 * Anchored popover under the library's Add button. It hands the key to the
 * store's own redeem page and closes; LauncherApp listens for `keys.found`
 * so the confirmation still lands after this popover is gone.
 */
export function ActivateKeySheet({
  open,
  onClose,
  onStatus,
}: {
  open: boolean
  onClose: () => void
  onStatus: (message: string, fail?: boolean) => void
}) {
  const inputRef = useRef<HTMLInputElement>(null)
  const chipGlide = useGlide<HTMLDivElement>()
  const panelRef = useRef<HTMLDivElement>(null)
  const sessionRevision = useRef(0)
  const edited = useRef(false)
  const [key, setKey] = useState('')
  const [store, setStore] = useState<(typeof STORES)[number]>('steam')
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState('')
  const [position, setPosition] = useState({ top: 96, left: 12, width: 360, maxHeight: 500 })

  useLayoutEffect(() => {
    if (!open) return
    const place = () => {
      const inset = 12
      const width = Math.min(360, Math.max(0, window.innerWidth - inset * 2))
      const maxHeight = Math.max(0, window.innerHeight - inset * 2)
      const anchor = document.querySelector('.exo-library-add')?.getBoundingClientRect()
      const height = Math.min(panelRef.current?.getBoundingClientRect().height || 240, maxHeight)
      // Right-align under the Add button, which sits at the right edge of the shelf header.
      const preferredLeft = anchor ? anchor.right - width : window.innerWidth - width - inset
      const left = Math.max(inset, Math.min(preferredLeft, window.innerWidth - width - inset))
      const preferredTop = (anchor?.bottom ?? 84) + 8
      const top = Math.max(inset, Math.min(preferredTop, window.innerHeight - height - inset))
      setPosition({ top, left, width, maxHeight })
    }
    place()
    const observer = new ResizeObserver(place)
    if (panelRef.current) observer.observe(panelRef.current)
    window.addEventListener('resize', place)
    return () => { observer.disconnect(); window.removeEventListener('resize', place) }
  }, [open])

  useEffect(() => {
    const revision = ++sessionRevision.current
    if (!open) return
    setBusy(false)
    setNote('')
    edited.current = false
    setKey('')
    setStore('steam')
    void host.keysPreview().then((preview) => {
      if (revision !== sessionRevision.current || edited.current) return
      if (preview.looksLikeKey && preview.key) {
        setKey(preview.key)
        if (preview.store === 'steam' || preview.store === 'epic' || preview.store === 'gog') {
          setStore(preview.store)
        }
      }
    }).catch(() => {
      // Clipboard detection is optional; manual key entry remains available.
    })
    const focusFrame = window.requestAnimationFrame(() => inputRef.current?.focus())
    return () => {
      sessionRevision.current = revision + 1
      window.cancelAnimationFrame(focusFrame)
      document.querySelector<HTMLElement>('.exo-library-add')?.focus({ preventScroll: true })
    }
  }, [open])

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Tab' && panelRef.current) {
        // Portaled to the end of body: Tab wraps inside instead of falling out behind the library.
        const stops = Array.from(panelRef.current.querySelectorAll<HTMLElement>('input, button:not(:disabled)'))
        const edge = event.shiftKey ? stops[0] : stops[stops.length - 1]
        if (!edge || document.activeElement === edge || !panelRef.current.contains(document.activeElement)) {
          event.preventDefault()
          ;(event.shiftKey ? stops[stops.length - 1] : stops[0])?.focus()
        }
        return
      }
      if (event.key !== 'Escape') return
      event.preventDefault()
      onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])
  useDismiss(panelRef, onClose, { on: open })

  if (!open) return null

  const trimmed = key.trim()
  const activate = () => {
    const revision = sessionRevision.current
    setBusy(true)
    setNote('')
    // Epic's page cannot take the key in its address, so it goes to the clipboard for the paste.
    const copied = store === 'epic' ? navigator.clipboard.writeText(trimmed).catch(() => undefined) : Promise.resolve()
    void copied.then(() => host.keysActivate(trimmed, store)).then((result) => {
      if (revision !== sessionRevision.current) return
      setBusy(false)
      if (result.ok) {
        onStatus(result.message ?? `Finish in ${STORE_LABELS[store]}.`)
        onClose()
        return
      }
      setNote(result.message ?? 'Could not open the redeem page.')
    }).catch((e: unknown) => {
      if (revision !== sessionRevision.current) return
      report(e, 'ActivateKeySheet.keysActivate')
      setBusy(false)
      setNote('Could not open the redeem page.')
    })
  }

  return createPortal(
    <div ref={panelRef} className="exo-key-popover" style={position} role="dialog" aria-modal="true" aria-labelledby="exo-key-popover-title">
      <h2 id="exo-key-popover-title">Activate key</h2>
      <input
        ref={inputRef}
        className="exo-field"
        value={key}
        onChange={(event) => { edited.current = true; setKey(event.target.value) }}
        onKeyDown={(event) => { if (event.key === 'Enter' && trimmed && !busy) { event.preventDefault(); activate() } }}
        placeholder="Product key"
        autoComplete="off"
        spellCheck={false}
        aria-label="Product key"
      />
      <div ref={chipGlide} className="exo-shelf-chips exo-glide" role="group" aria-label="Store">
        {STORES.map((id) => (
          <button
            key={id}
            type="button"
            className="exo-shelf-chip"
            aria-pressed={id === store}
            disabled={busy}
            onClick={() => { edited.current = true; setStore(id) }}
          >
            {STORE_LABELS[id]}
          </button>
        ))}
      </div>
      <p className="exo-key-popover-note">{STORE_STEPS[store]}</p>
      {note ? <p className="exo-key-popover-note" role="status">{note}</p> : null}
      <div className="exo-key-popover-actions">
        <Button
          type="button"
          size="sm"
          className="exo-set-btn"
          isDisabled={busy || !trimmed}
          onPress={() => {
            void navigator.clipboard.writeText(trimmed)
              .then(() => setNote('Key copied.'))
              .catch(() => setNote('Select the key and press Ctrl+C to copy it.'))
          }}
        >
          Copy key
        </Button>
        <Button type="button" size="sm" className="exo-set-btn" onPress={onClose}>
          Cancel
        </Button>
        <Button
          type="button"
          size="sm"
          className="exo-set-btn is-primary"
          isDisabled={busy || !trimmed}
          onPress={activate}
        >
          {busy ? 'Opening…' : `Open ${STORE_LABELS[store]}`}
        </Button>
      </div>
    </div>,
    document.body,
  )
}
