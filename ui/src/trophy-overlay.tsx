import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/geist'
import { TrophyBanner } from './components/TrophyBanner'
import './trophy-overlay.css'

type TrophyHostMessage = {
  type: 'show' | 'hide' | 'clear'
  id?: string
  tier?: string
  name?: string
  detail?: string
  game?: string
  iconUrl?: string | null
  reducedMotion?: boolean
}

type OverlayState = {
  id: string
  tier: string
  name: string
  detail: string
  game: string
  iconUrl?: string | null
  reducedMotion: boolean
  leaving: boolean
}

type WebViewHost = {
  addEventListener: (type: 'message', handler: (event: { data: TrophyHostMessage | string }) => void) => void
  removeEventListener: (type: 'message', handler: (event: { data: TrophyHostMessage | string }) => void) => void
  postMessage: (msg: unknown) => void
}

function host(): WebViewHost | null {
  const webview = (window as Window & { chrome?: { webview?: WebViewHost } }).chrome?.webview
  return webview ?? null
}

function parseMessage(data: TrophyHostMessage | string): TrophyHostMessage | null {
  if (typeof data === 'string') {
    try {
      return JSON.parse(data) as TrophyHostMessage
    } catch {
      return null
    }
  }
  return data
}

function TrophyOverlay() {
  const [item, setItem] = useState<OverlayState | null>(null)

  useEffect(() => {
    const webview = host()
    const onMessage = (event: { data: TrophyHostMessage | string }) => {
      const msg = parseMessage(event.data)
      if (!msg) return
      if (msg.type === 'show') {
        setItem({
          id: msg.id ?? String(Date.now()),
          tier: msg.tier ?? 'bronze',
          name: msg.name ?? '',
          detail: msg.detail ?? '',
          game: msg.game ?? '',
          iconUrl: msg.iconUrl,
          reducedMotion: Boolean(msg.reducedMotion),
          leaving: false,
        })
        return
      }
      if (msg.type === 'hide') {
        setItem((current) => (current ? { ...current, leaving: true } : current))
        return
      }
      if (msg.type === 'clear') setItem(null)
    }
    webview?.addEventListener('message', onMessage)
    webview?.postMessage({ type: 'ready' })
    return () => webview?.removeEventListener('message', onMessage)
  }, [])

  useEffect(() => {
    const banner = document.querySelector('.exo-trophy-banner')
    if (!(banner instanceof HTMLElement)) return
    const style = getComputedStyle(banner)
    banner.dataset.font = style.fontFamily
    banner.dataset.radius = style.borderRadius
  }, [item])

  return (
    <div className="exo-trophy-overlay" data-exo-trophy-surface="overlay">
      {item ? (
        <TrophyBanner
          key={item.id}
          announce
          animate={!item.leaving}
          leaving={item.leaving}
          reduced={item.reducedMotion}
          tier={item.tier}
          name={item.name}
          detail={item.detail}
          game={item.game}
          iconUrl={item.iconUrl}
        />
      ) : null}
    </div>
  )
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <TrophyOverlay />
  </StrictMode>,
)
