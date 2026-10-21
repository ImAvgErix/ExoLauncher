import { useCallback, useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { useLayer } from './admin'
import { Icons } from './icons'
import { open } from './markdown'

type At = { src: string; link: string; still: boolean; x: number; y: number }

// Discord's own pictures (its CDN and media proxies), the only ones its app copies or saves.
const discordAsset = (url: URL) => url.protocol === 'https:' && (url.hostname === 'cdn.discordapp.com' || url.hostname.endsWith('.discordapp.net'))
// The types Discord's app copies (a GIF is not) and saves; a picture with no extension is saved as PNG.
const COPIES = new Set(['jpg', 'jpeg', 'jfif', 'png', 'webp', 'avif'])
const SAVES = new Set([...COPIES, 'gif', 'tiff', 'bmp'])
const IMAGE_LINK = /\.(png|jpe?g|webp|avif|bmp|svg)$/i
const MEDIA_LINK = /\.(png|jpe?g|webp|avif|bmp|svg|gif|mp4|webm|mov)$/i

/**
 * The picture itself, as Discord's Copy Image and Save Image fetch it: the CDN's original for one the media proxy
 * resized, without the proxy's size and format; PNG when the address names no type.
 */
function original(src: string) {
  const url = new URL(src)
  if (url.hostname === 'media.discordapp.net') url.hostname = 'cdn.discordapp.com'
  for (const key of ['width', 'height', 'quality', 'size', 'format']) url.searchParams.delete(key)
  if (!/\.[a-z0-9]+$/i.test(url.pathname)) url.searchParams.set('format', 'png')
  return url
}

const extension = (url: URL) => (url.searchParams.get('format') ?? /\.([a-z0-9]+)$/i.exec(url.pathname)?.[1] ?? '').toLowerCase()

// What Discord calls the link: an image on its CDN, any other picture or video, or a link.
function linkKind(link: string) {
  try {
    const url = new URL(link)
    return url.hostname === 'cdn.discordapp.com' && IMAGE_LINK.test(url.pathname) ? 'Image Link' : MEDIA_LINK.test(url.pathname) ? 'Media Link' : 'Link'
  } catch { return 'Link' }
}

// A picture in a message, the image viewer or a profile; not a person (their menu), a reaction, a picker's tile or a menu row.
function pictureAt(target: EventTarget | null) {
  const el = (target as Element | null)?.closest?.('img, video')
  if (!el || !el.closest('.exo-dc-msg-body, .exo-dc-lightbox, .exo-dc-pf') || el.closest('[data-dc-user], .exo-dc-reactions, [role="menu"]')) return null
  const src = (el as HTMLImageElement | HTMLVideoElement).currentSrc || (el as HTMLImageElement).src
  if (!/^https?:/.test(src)) return null
  return { src, link: el.getAttribute('data-dc-link') || el.closest('a')?.href || original(src).toString(), still: el.tagName === 'IMG' }
}

let say: (text: string) => void = () => { }

/** Discord's Save Image. */
function saveImage(src: string) {
  void host.discordImage('save', original(src).toString())
    .then(result => say(result.ok ? 'Image Saved' : result.message ?? 'Unable to save image'))
    .catch(() => say('Unable to save image'))
}

/**
 * Discord's image menu, on a right-click on any picture (an attachment, an embed's or a link's picture, a sticker,
 * an emoji, a profile's picture or banner, the image viewer): Copy Image and Save Image for Discord's own
 * pictures, then Copy Link and Open Link, as Discord's app names them. A right-click elsewhere on a message
 * still opens its message menu.
 */
export function ImageMenuHost() {
  const [at, setAt] = useState<At | null>(null)
  const [note, setNote] = useState<string | null>(null)
  useEffect(() => {
    // Capture: ahead of the message's own right-click (its menu) and the person menu.
    const menu = (event: MouseEvent) => {
      const picture = pictureAt(event.target)
      if (!picture || window.getSelection()?.toString()) return
      event.preventDefault()
      event.stopPropagation()
      setAt({ ...picture, x: event.clientX, y: event.clientY })
    }
    document.addEventListener('contextmenu', menu, true)
    return () => document.removeEventListener('contextmenu', menu, true)
  }, [])
  // Discord's toast for what happened: "Image Copied!", "Image Saved", "Link Copied!", or why not.
  useEffect(() => {
    say = setNote
    return () => { say = () => { } }
  }, [])
  useEffect(() => {
    if (!note) return
    const timer = window.setTimeout(() => setNote(null), 2400)
    return () => window.clearTimeout(timer)
  }, [note])
  const close = useCallback(() => setAt(null), [])
  return (
    <>
      {at ? <ImageMenu key={`${at.src}:${at.x}:${at.y}`} at={at} onClose={close} /> : null}
      {note ? <p className="exo-dc-toast" role="status">{note}</p> : null}
    </>
  )
}

function ImageMenu({ at, onClose }: { at: At; onClose: () => void }) {
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  useEffect(() => {
    const away = (event: PointerEvent) => { if (box.current && !box.current.contains(event.target as Node)) onClose() }
    document.addEventListener('pointerdown', away, true)
    return () => document.removeEventListener('pointerdown', away, true)
  }, [onClose])
  const picture = original(at.src)
  const ownImage = at.still && discordAsset(picture)
  const type = extension(picture)
  const copies = ownImage && COPIES.has(type)
  const saves = ownImage && (!type || SAVES.has(type))
  const kind = linkKind(at.link)
  const run = (action: () => void) => () => { onClose(); action() }
  return (
    <div className="exo-dc-menu exo-dc-ctx" ref={box} role="menu" aria-label="Image Actions" style={{ left: at.x, top: at.y }}>
      {copies ? (
        <button type="button" role="menuitem" onClick={run(() => void host.discordImage('copy', picture.toString())
          .then(result => say(result.ok ? 'Image Copied!' : result.message ?? 'Unable to copy image'))
          .catch(() => say('Unable to copy image')))}><Icons.Copy size={16} />Copy Image</button>
      ) : null}
      {saves ? <button type="button" role="menuitem" onClick={run(() => saveImage(at.src))}><Icons.SaveImage size={16} />Save Image</button> : null}
      {copies || saves ? <span className="exo-dc-menu-line" role="separator" /> : null}
      <button type="button" role="menuitem" onClick={run(() => void navigator.clipboard.writeText(at.link).then(() => say('Link Copied!')).catch(() => { }))}>
        <Icons.Link size={16} />Copy {kind}
      </button>
      <button type="button" role="menuitem" onClick={run(() => open(at.link))}><Icons.Open size={16} />Open {kind}</button>
    </div>
  )
}
