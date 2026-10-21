import { useCallback, useEffect, useRef, useState } from 'react'

const AUTO_CLEAR_MS = 4500

export type StatusTone = 'info' | 'bad'

export type ActionStatus = {
  message: string | null
  /** The card the message belongs to; null means it belongs to the shell. */
  gameId: string | null
  /** Failures read in red; everything else is a quiet confirmation. */
  tone: StatusTone
  /** Asks the user to act; it stays until they dismiss it or something replaces it. */
  sticky: boolean
  /**
   * Set a message. Sticky messages ask the user to act and never time out.
   * Tone defaults to `bad` for sticky messages (every sticky caller reports a
   * failure) and `info` otherwise.
   */
  set: (message: string | null, gameId?: string | null, sticky?: boolean, tone?: StatusTone) => void
  /** A passing shell notice (a sale, a mouse alert). It never replaces the status line. */
  notice: string | null
  notify: (message: string) => void
}

/** Runs `clear` once Exo has been on screen for AUTO_CLEAR_MS, so a hidden window keeps the message. */
function clearWhenSeen(clear: () => void): () => void {
  let timer = 0
  const start = () => {
    if (document.hidden || timer) return
    timer = window.setTimeout(clear, AUTO_CLEAR_MS)
  }
  start()
  document.addEventListener('visibilitychange', start)
  return () => {
    window.clearTimeout(timer)
    document.removeEventListener('visibilitychange', start)
  }
}

/**
 * One status line for the whole shell. Terminal launch/install messages clear
 * themselves after a short delay; anything that tells the user to go finish a
 * step elsewhere stays until they act. A message about the game that is
 * downloading right now waits for the transfer to end; every other message
 * clears on schedule even while a download runs.
 */
export function useActionStatus(transferGameId: string | null, selectedIdRef: { current: string | null }): ActionStatus {
  const [message, setMessage] = useState<string | null>(null)
  const [gameId, setGameId] = useState<string | null>(null)
  const [sticky, setSticky] = useState(false)
  const [tone, setTone] = useState<StatusTone>('info')
  const selectedRef = useRef(selectedIdRef)
  selectedRef.current = selectedIdRef

  const set = useCallback((next: string | null, forGame?: string | null, isSticky = false, nextTone?: StatusTone) => {
    setMessage(next)
    setGameId(next ? (forGame === undefined ? selectedRef.current.current : forGame) : null)
    setSticky(Boolean(next) && isSticky)
    setTone(next ? nextTone ?? (isSticky ? 'bad' : 'info') : 'info')
  }, [])

  const heldByTransfer = !!transferGameId && !!gameId && transferGameId.toLowerCase() === gameId.toLowerCase()
  useEffect(() => {
    if (!message || heldByTransfer || sticky) return
    return clearWhenSeen(() => set(null, null))
  }, [message, heldByTransfer, sticky, set])

  const [notice, setNotice] = useState<string | null>(null)
  useEffect(() => {
    if (!notice) return
    return clearWhenSeen(() => setNotice(null))
  }, [notice])

  return { message, gameId, tone, sticky, set, notice, notify: setNotice }
}

const FAILURE_EVENT = 'exo-shell-failure'

/** A failure from a control outside the shell's callbacks (the Bar's Capture button), as a red toast. */
export function reportFailure(message: string) {
  window.dispatchEvent(new CustomEvent<string>(FAILURE_EVENT, { detail: message }))
}

export function onReportedFailure(listener: (message: string) => void): () => void {
  const heard = (event: Event) => listener((event as CustomEvent<string>).detail)
  window.addEventListener(FAILURE_EVENT, heard)
  return () => window.removeEventListener(FAILURE_EVENT, heard)
}
