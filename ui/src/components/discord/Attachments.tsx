import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import { performanceMode, type DiscordAttachment } from '../../lib/discord'
import { mediaFilter, useAccountSettings, type MediaFilter } from '../../lib/discordPrefs'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { usePageActive } from '../../lib/pageActivity'
import { VoiceMessage } from './VoiceMessage'
import { showImage, type ViewerItem } from './extras'
import { Icons } from './icons'
import { open, spoilersShown } from './markdown'

const IMAGE = /\.(png|jpe?g|gif|webp|avif)$/i
const VIDEO = /\.(mp4|webm|mov)$/i
// The kinds of text Discord previews in the chat, by the file's last extension (its own list, the common part).
const TEXT = new Set(('txt text md markdown csv json xml html xhtml svg css scss less js jsx mjs cjs ts tsx py rb rs go java kt kts c h cc cpp cxx hpp '
  + 'cs fs vb sh bash zsh ps1 bat cmd yml yaml toml ini cfg conf properties lua php pl pm sql swift dart diff patch srt vtt svelte gradle '
  + 'groovy dockerfile makefile mk lock tf hcl r scala ex exs erl hs ml nim clj coffee tex proto graphql gql').split(' '))

/** A picture shown at most maxW by maxH (Discord's 550 by 350 for one in the chat), keeping its shape. */
export function fit(width?: number | null, height?: number | null, maxW = 550, maxH = 350) {
  if (!width || !height) return { width: undefined, height: undefined }
  const scale = Math.min(1, maxW / width, maxH / height)
  return { width: Math.round(width * scale), height: Math.round(height * scale) }
}

// The largest side Discord's media proxy is asked for (Discord's own cap).
const PROXY_MAX = 4096

/**
 * A picture from Discord's media proxy at the size it is shown, at the screen's pixel density, as Discord's client
 * asks for it (width, height and keep_aspect_ratio, the proxy scaling it down sharply rather than the page): WebP
 * unless it moves, lossless for a PNG. A known box is the picture's own shown size; without one (a mosaic cell) it
 * covers Discord's 550 by 350. The original (often 4K or larger, many megabytes) would otherwise paint top-down for
 * seconds, and one about twice the box was squeezed by the page and looked soft.
 */
export function sized(src: string, width?: number | null, height?: number | null, box?: { width: number; height: number }, animated = false, still = false) {
  let url: URL
  try { url = new URL(src) } catch { return src }
  if (!/(^|\.)discordapp\.net$/.test(url.hostname)) return src
  // still: a GIF or animated picture as its first frame (Discord's media proxy sends a WebP still unless asked to animate).
  if (still) { url.searchParams.set('format', 'webp'); url.searchParams.delete('animated') }
  if (!width || !height) return still ? url.toString() : src
  const shown = box ? Math.min(box.width / width, box.height / height) : Math.max(550 / width, 350 / height)
  const scale = Math.min(Math.min(shown, 1) * (window.devicePixelRatio || 1), PROXY_MAX / width, PROXY_MAX / height)
  if (!still && !animated && !/\.gif$/i.test(url.pathname)) {
    url.searchParams.set('format', 'webp')
    if (/\.(png|avif)$/i.test(url.pathname)) url.searchParams.set('quality', 'lossless')
  }
  url.searchParams.set('width', String(Math.max(1, Math.ceil(width * scale))))
  url.searchParams.set('height', String(Math.max(1, Math.ceil(height * scale))))
  url.searchParams.set('keep_aspect_ratio', 'true')
  return url.toString()
}

/**
 * A picture that may move (a GIF, an animated WebP or AVIF): it plays by itself when Play GIFs when Discord is focused
 * is on, Exo is active and neither Reduced Motion nor Performance mode is on; otherwise it holds its first frame until
 * pointed at, as Discord's GIFs do.
 */
export function MovingImage({ src, width, height, moving, alt, link, box }: {
  src: string; width?: number | null; height?: number | null; moving: boolean; alt: string; link?: string | null; box: { width?: number; height?: number }
}) {
  const { gifAutoPlay } = useAccountSettings()
  const active = usePageActive()
  const [hover, setHover] = useState(false)
  const play = moving && (hover || (gifAutoPlay && active && !performanceMode() && !shouldReduceMotion()))
  return (
    <img src={sized(src, width, height, box.width && box.height ? { width: box.width, height: box.height } : undefined, play, moving && !play)} alt={alt} loading="lazy" draggable={false} data-dc-link={link ?? undefined} {...box}
      onPointerEnter={moving ? () => setHover(true) : undefined} onPointerLeave={moving ? () => setHover(false) : undefined} />
  )
}

export function size(bytes: number) {
  return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`
}

// Discord's sensitive media filters for this attachment. A DM takes the stricter of the friend and other-DM choices,
// since the room doesn't know which it is.
function sensitive(flags: number, guildId: string | null, settings: ReturnType<typeof useAccountSettings>): MediaFilter {
  if (guildId) return mediaFilter(flags, 'guild', settings)
  const friend = mediaFilter(flags, 'friendDm', settings), other = mediaFilter(flags, 'dm', settings)
  return friend === 'block' || other === 'block' ? 'block' : friend === 'blur' || other === 'blur' ? 'blur' : 'show'
}

/** A picture or video hidden behind Discord's SPOILER (or its sensitive content blur) until clicked. */
function Hidden({ label, children }: { label: string; children: React.ReactNode }) {
  const [shown, setShown] = useState(false)
  if (shown) return <>{children}</>
  return (
    <div className="exo-dc-hidden-media">
      <div className="exo-dc-hidden-blur" aria-hidden="true" inert>{children}</div>
      <button type="button" className="exo-dc-hidden-label" aria-label={`${label}, show`} onClick={() => setShown(true)}>{label}</button>
    </div>
  )
}

function mediaOf(file: DiscordAttachment, inline: boolean): 'image' | 'video' | null {
  const type = file.content_type ?? ''
  if (!inline) return null
  return type.startsWith('image/') || IMAGE.test(file.filename) ? 'image' : type.startsWith('video/') || VIDEO.test(file.filename) ? 'video' : null
}

/**
 * A picture or video in the chat: behind SPOILER or the sensitive content blur when it is one, left out when
 * your settings block it; with Show image descriptions, an ALT tag that shows what the sender wrote. In a
 * mosaic it fills its cell.
 */
function Media({ file, media, guildId, moderator, cell, pictures }: { file: DiscordAttachment; media: 'image' | 'video'; guildId: string | null; moderator?: boolean; cell?: boolean; pictures?: DiscordAttachment[] }) {
  const settings = useAccountSettings()
  const [alt, setAlt] = useState(false)
  const box = cell ? { width: undefined, height: undefined } : fit(file.width, file.height)
  const flags = file.flags ?? 0
  const spoiler = (file.filename.startsWith('SPOILER_') || (flags & 8) !== 0) && !spoilersShown(settings.renderSpoilers, moderator)
  const explicit = sensitive(flags, guildId, settings)
  if (explicit === 'block') return <p className="exo-dc-small">Sensitive content hidden by your settings.</p>
  const src = file.proxy_url ?? file.url
  // Attachment flag 32: an animated WebP or AVIF, which keeps its own format to keep moving.
  const shown = media === 'image'
    ? <button type="button" className="exo-dc-media" onClick={() => showImage(src, file.url, false, pictures && { items: pictures.map(viewerItem), index: pictures.indexOf(file) })} aria-label={file.description ? `Image: ${file.description}` : 'Open image'}>
        <MovingImage src={src} width={file.width} height={file.height} moving={(flags & 32) !== 0 || /\.gif$/i.test(file.filename)} alt={file.description ?? ''} link={file.url} box={box} />
      </button>
    : <video className="exo-dc-media" src={src} controls preload="metadata" {...box} />
  const described = settings.viewImageDescriptions && !!file.description
  return (
    <div className={`exo-dc-media-wrap${cell ? ' is-cell' : ''}`}>
      {spoiler || explicit === 'blur' ? <Hidden label={spoiler ? 'SPOILER' : 'Sensitive content'}>{shown}</Hidden> : shown}
      {described ? <button type="button" className="exo-dc-alt-tag" aria-expanded={alt} aria-label="Image description" onClick={() => setAlt(a => !a)}>ALT</button> : null}
      {described && alt ? <span className="exo-dc-alt" role="note">{file.description}</span> : null}
    </div>
  )
}

const viewerItem = (file: DiscordAttachment): ViewerItem => ({ src: file.proxy_url ?? file.url, original: file.url, alt: file.description })

// Discord's mosaic: how many pictures go in each row, by how many there are (three is one tall and two stacked).
const ROWS: Record<number, number[]> = { 2: [2], 4: [2, 2], 5: [2, 3], 6: [3, 3], 7: [1, 3, 3], 8: [2, 3, 3], 9: [3, 3, 3], 10: [1, 3, 3, 3] }

function Mosaic({ files, guildId, moderator }: { files: { file: DiscordAttachment; media: 'image' | 'video' }[]; guildId: string | null; moderator?: boolean }) {
  if (files.length === 1) return <Media file={files[0].file} media={files[0].media} guildId={guildId} moderator={moderator} />
  // The viewer goes through the message's pictures (a video plays where it is).
  const pictures = files.filter(f => f.media === 'image').map(f => f.file)
  if (files.length === 3) {
    return (
      <div className="exo-dc-mosaic is-three">
        {files.map(f => <Media key={f.file.id} file={f.file} media={f.media} guildId={guildId} moderator={moderator} cell pictures={pictures} />)}
      </div>
    )
  }
  let at = 0
  return (
    <div className="exo-dc-mosaic">
      {(ROWS[files.length] ?? [3, 3, 3, 1]).map((count, row) => {
        const cells = files.slice(at, at += count)
        return (
          <div key={row} className={`exo-dc-mosaic-row is-${cells.length}`}>
            {cells.map(f => <Media key={f.file.id} file={f.file} media={f.media} guildId={guildId} moderator={moderator} cell pictures={pictures} />)}
          </div>
        )
      })}
    </div>
  )
}

/** A file's name, size and download, as Discord's file card. */
function FileCard({ file, children }: { file: DiscordAttachment; children?: React.ReactNode }) {
  return (
    <div className={`exo-dc-file${children ? ' has-preview' : ''}`}>
      {children}
      <div className="exo-dc-file-row">
        <Icons.File size={28} />
        <span className="exo-dc-file-meta">
          <button type="button" className="exo-dc-file-name" onClick={() => open(file.url)}>{file.filename}</button>
          <span className="exo-dc-file-size">{size(file.size)}</span>
        </span>
        <button type="button" className="exo-dc-icon-btn" aria-label={`Download ${file.filename}`} onClick={() => open(file.url)}><Icons.Download size={18} /></button>
      </div>
    </div>
  )
}

const COLLAPSED_LINES = 6

/** Discord's text file preview: the first lines in a code box, Expand for the rest it read (50 KB), the file's card under it. */
function TextPreview({ file }: { file: DiscordAttachment }) {
  const [text, setText] = useState<{ body: string; left: number } | null>(null)
  const [failed, setFailed] = useState(false)
  const [expanded, setExpanded] = useState(false)
  useEffect(() => {
    let live = true
    host.discordFileText(file.url).then(result => {
      if (!live) return
      if (result.ok && typeof result.text === 'string') setText({ body: result.text, left: result.left ?? 0 })
      else setFailed(true)
    }).catch(() => { if (live) setFailed(true) })
    return () => { live = false }
  }, [file.url])
  if (failed) return <FileCard file={file} />
  const lines = text ? text.body.replace(/\n$/, '').split('\n') : []
  const long = lines.length > COLLAPSED_LINES || (text?.left ?? 0) > 0
  const body = text ? (expanded ? lines : lines.slice(0, COLLAPSED_LINES)).join('\n') : ''
  return (
    <FileCard file={file}>
      <pre className="exo-dc-file-preview" aria-busy={!text}>{body}{expanded && text && text.left > 0 ? `\n… ${size(text.left)} more` : ''}</pre>
      {long ? (
        <button type="button" className="exo-dc-file-expand" aria-expanded={expanded} onClick={() => setExpanded(e => !e)}>
          {expanded ? <Icons.Collapse size={16} /> : <Icons.Expand size={16} />}
          {expanded ? 'Collapse' : 'Expand'} ({lines.length}{text && text.left > 0 ? '+' : ''} lines)
        </button>
      ) : null}
    </FileCard>
  )
}

function Other({ file }: { file: DiscordAttachment }) {
  // A voice message plays with its waveform; any other sound file in a small player, as in Discord.
  if (file.waveform) return <VoiceMessage attachment={file} />
  if ((file.content_type ?? '').startsWith('audio/')) {
    return (
      <div className="exo-dc-audio">
        <span className="exo-dc-file-name">{file.filename}<span className="exo-dc-file-size">{size(file.size)}</span></span>
        <audio src={file.url} controls preload="metadata" />
      </div>
    )
  }
  const extension = file.filename.split('.').pop()?.toLowerCase() ?? ''
  if (file.content_type?.startsWith('text/') || TEXT.has(extension)) return <TextPreview file={file} />
  return <FileCard file={file} />
}

/**
 * A message's files, Discord's way: pictures and videos together in one mosaic (or one alone at its own size),
 * then each other file: a voice message, a sound player, a text preview, or a file card.
 */
export function Attachments({ files, guildId, moderator }: { files: DiscordAttachment[]; guildId: string | null; moderator?: boolean }) {
  // Discord settings > Appearance > Show images, videos... When uploaded directly to Discord; off, they're files to open.
  const inline = useAccountSettings().inlineAttachmentMedia
  const media = files.flatMap(file => { const kind = mediaOf(file, inline); return kind ? [{ file, media: kind }] : [] })
  const rest = files.filter(file => !mediaOf(file, inline))
  return (
    <>
      {media.length ? <Mosaic files={media} guildId={guildId} moderator={moderator} /> : null}
      {rest.map(file => <Other key={file.id} file={file} />)}
    </>
  )
}
