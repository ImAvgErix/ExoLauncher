import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { avatarUrl, displayName, snowflakeTime, type DiscordChannel, type DiscordForumTag, type DiscordMessage, type DiscordThread } from '../../lib/discord'
import { Icons } from './icons'
import { emojiUrl, formatTimestamp, MarkdownLine, type MarkdownContext } from './markdown'
import { sized } from './Attachments'
import { Topic } from './ChatHeader'
import { isAfter } from './messageCache'

type Page = { threads: DiscordThread[]; first: Map<string, DiscordMessage>; tags: DiscordForumTag[]; error: string | null; done: boolean; loading: boolean }

/**
 * A forum or media channel, as Discord lists it: New Post, a title filter and the tags, then each post with its tags,
 * the first message's author, text and picture, its replies and last activity. Open posts come first, then archived
 * ones load as the list scrolls.
 */
export function ForumView({ channel, guildId, channelName, active, onOpen }: {
  channel: DiscordChannel
  guildId: string
  channelName: (id: string) => string | null
  active: boolean
  onOpen: (thread: DiscordChannel) => void
}) {
  const { page, more } = usePosts(channel.id)
  // Posts begin with markdown: their emoji, mentions and links, as Discord draws a preview.
  const ctx = useMemo<MarkdownContext>(() => ({ users: new Map([...page.first.values()].flatMap(m => m.mentions).map(u => [u.id, u])), channelName, meId: null, guildId, colors: new Map() }), [page.first, channelName, guildId])
  // Viewing a forum reads it, as Discord acks a forum on screen (its newest post is the forum's newest message).
  const newest = page.threads.reduce<string | null>((top, t) => top && !isAfter(t.id, top) ? top : t.id, null)
  useEffect(() => { if (active && newest) void host.discordAck(channel.id, newest).catch(() => { }) }, [active, channel.id, newest])
  const [query, setQuery] = useState('')
  const [tag, setTag] = useState<string | null>(null)
  const [writing, setWriting] = useState(false)
  const q = query.trim().toLowerCase()
  const shown = page.threads.filter(t => (!q || t.name.toLowerCase().includes(q)) && (!tag || t.applied_tags?.includes(tag)))
  const open = (thread: { id: string; name: string; locked?: boolean }) =>
    onOpen({ id: thread.id, name: thread.name, type: 11, kind: 'text', canRead: true, canSend: thread.locked !== true })
  return (
    <section className="exo-dc-chat" aria-label={channel.name}>
      <header className="exo-dc-chat-head">
        <Icons.Forum size={22} />
        <strong>{channel.name}</strong>
        {channel.topic ? <Topic channel={channel} guildId={guildId} topic={channel.topic} ctx={ctx} /> : null}
      </header>
      <div className="exo-dc-forum" onScroll={event => { const el = event.currentTarget; if (el.scrollHeight - el.scrollTop - el.clientHeight < 400) more() }}>
        <div className="exo-dc-forum-bar">
          <label className="exo-dc-friend-search exo-dc-grow">
            <Icons.Search size={16} />
            <input placeholder="Search posts" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search posts" />
          </label>
          {channel.canSend !== false ? <button type="button" className="exo-set-btn is-primary" onClick={() => setWriting(true)}>New post</button> : null}
        </div>
        {page.tags.length ? (
          <div className="exo-dc-forum-tags" role="group" aria-label="Tags">
            {page.tags.map(t => (
              <button key={t.id} type="button" className="exo-dc-tag-chip" aria-pressed={tag === t.id} onClick={() => setTag(current => current === t.id ? null : t.id)}>
                <TagLabel tag={t} />
              </button>
            ))}
          </div>
        ) : null}
        {writing ? <NewPost channelId={channel.id} tags={page.tags} onCancel={() => setWriting(false)} onPosted={thread => { setWriting(false); open(thread) }} /> : null}
        {shown.map(thread => <PostCard key={thread.id} thread={thread} first={page.first.get(thread.id)} tags={page.tags} ctx={ctx} onOpen={() => open({ id: thread.id, name: thread.name, locked: thread.thread_metadata?.locked })} />)}
        {page.loading ? <p className="exo-dc-note" role="status">Loading posts</p> : null}
        {page.error ? <p className="exo-dc-note" role="alert">{page.error}</p> : null}
        {!page.loading && !page.error && page.done && shown.length === 0 ? <p className="exo-dc-note">{q || tag ? 'No posts match.' : 'No posts yet.'}</p> : null}
      </div>
    </section>
  )
}

// Open posts first (one page), then archived ones 25 at a time; a page that fails stops there with its reason.
function usePosts(channelId: string) {
  const [page, setPage] = useState<Page>({ threads: [], first: new Map(), tags: [], error: null, done: false, loading: true })
  const next = useRef<{ archived: boolean; offset: number } | null>({ archived: false, offset: 0 })
  const busy = useRef(false)
  const load = useCallback(async () => {
    const at = next.current
    if (!at || busy.current) return
    busy.current = true
    setPage(p => ({ ...p, loading: true }))
    try {
      const result = await host.discordThreads(channelId, at)
      if (!result.ok) { next.current = null; setPage(p => ({ ...p, loading: false, done: true, error: result.message ?? 'Discord did not send the posts.' })); return }
      const threads = result.threads ?? []
      next.current = !at.archived ? { archived: true, offset: 0 } : result.hasMore ? { archived: true, offset: at.offset + threads.length } : null
      setPage(p => {
        const first = new Map(p.first)
        for (const m of result.firstMessages ?? []) first.set(m.channel_id, m)
        return { threads: [...p.threads, ...threads.filter(t => !p.threads.some(x => x.id === t.id))], first, tags: result.tags ?? p.tags, error: null, done: next.current === null, loading: false }
      })
    } catch (err) {
      report(err, 'ForumView.load')
      next.current = null
      setPage(p => ({ ...p, loading: false, done: true, error: 'Discord did not send the posts.' }))
    } finally {
      busy.current = false
    }
  }, [channelId])
  // The open posts, then the first archived page at once: a forum whose posts are all archived isn't empty.
  useEffect(() => { void load().then(load) }, [load])
  return { page, more: () => void load() }
}

function TagLabel({ tag }: { tag: DiscordForumTag }) {
  return (
    <>
      {tag.emojiId ? <img src={emojiUrl(tag.emojiId, false, 32)} alt="" draggable={false} /> : tag.emojiName ? <span aria-hidden="true">{tag.emojiName}</span> : null}
      {tag.name}
    </>
  )
}

function PostCard({ thread, first, tags, ctx, onOpen }: { thread: DiscordThread; first?: DiscordMessage; tags: DiscordForumTag[]; ctx: MarkdownContext; onOpen: () => void }) {
  const image = first?.attachments.find(a => a.content_type?.startsWith('image/'))
  // Its picture from Discord's media proxy at the size that covers the 88 px square, not the original (often 4K, megabytes).
  const w = image?.width ?? 0, h = image?.height ?? 0
  const cover = w && h ? 88 / Math.min(w, h) : 0
  const thumb = image ? sized(image.proxy_url ?? image.url, w, h, cover ? { width: w * cover, height: h * cover } : undefined) : null
  const applied = tags.filter(t => thread.applied_tags?.includes(t.id))
  return (
    <button type="button" className="exo-dc-forum-post" onClick={onOpen}>
      <span className="exo-dc-forum-post-body">
        {applied.length ? <span className="exo-dc-forum-tags">{applied.map(t => <span key={t.id} className="exo-dc-tag-chip is-static"><TagLabel tag={t} /></span>)}</span> : null}
        <strong>{thread.name}</strong>
        <PostPreview thread={thread} first={first} ctx={ctx} />
        <span className="exo-dc-forum-meta">
          <span><Icons.Chat size={14} /> {thread.message_count ?? 0}</span>
          <span>{formatTimestamp(new Date(snowflakeTime(thread.last_message_id ?? thread.id)), 'R')}</span>
          {thread.thread_metadata?.locked ? <span>Locked</span> : null}
        </span>
      </span>
      {thumb ? <img className="exo-dc-forum-thumb" src={thumb} alt="" loading="lazy" draggable={false} /> : null}
    </button>
  )
}

// Who started the post and how it begins.
function PostPreview({ thread, first, ctx }: { thread: DiscordThread; first?: DiscordMessage; ctx: MarkdownContext }) {
  const owner = thread.owner
  const author = first ? displayName(first) : owner?.nick ?? owner?.user?.global_name ?? owner?.user?.username
  if (!author && !first?.content) return null
  return (
    <span className="exo-dc-forum-preview">
      {first ? <img className="exo-dc-forum-avatar" src={avatarUrl(first.author, 20)} alt="" draggable={false} /> : null}
      {author ? <b>{author}</b> : null}
      {first?.content ? <MarkdownLine text={first.content} ctx={ctx} /> : null}
    </span>
  )
}

// Discord's new post: a title, the first message and the forum's tags; posting opens it.
function NewPost({ channelId, tags, onCancel, onPosted }: {
  channelId: string
  tags: DiscordForumTag[]
  onCancel: () => void
  onPosted: (thread: { id: string; name: string }) => void
}) {
  const [name, setName] = useState('')
  const [content, setContent] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const post = async () => {
    if (busy || !name.trim() || !content.trim()) return
    setBusy(true); setError(null)
    const result = await host.discordThread(channelId, { name: name.trim(), content: content.trim(), tags: picked }).catch(() => ({ ok: false, message: null, thread: null }))
    setBusy(false)
    if (result.ok && result.thread) onPosted(result.thread)
    else setError(result.message ?? 'Discord did not take the post.')
  }
  return (
    <div className="exo-dc-forum-new" role="group" aria-label="New post">
      <input className="exo-dc-forum-title" placeholder="Post title" value={name} maxLength={100} autoFocus aria-label="Post title"
        onChange={event => setName(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') onCancel() }} />
      <textarea className="exo-dc-input" placeholder="Write the first message" value={content} maxLength={4000} rows={3} aria-label="First message"
        onChange={event => setContent(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') onCancel() }} />
      {tags.length ? (
        <div className="exo-dc-forum-tags" role="group" aria-label="Tags for the post">
          {tags.map(t => (
            <button key={t.id} type="button" className="exo-dc-tag-chip" aria-pressed={picked.includes(t.id)}
              onClick={() => setPicked(list => list.includes(t.id) ? list.filter(x => x !== t.id) : list.length < 5 ? [...list, t.id] : list)}>
              <TagLabel tag={t} />
            </button>
          ))}
        </div>
      ) : null}
      <div className="exo-dc-forum-new-foot">
        {error ? <p className="exo-dc-forum-error" role="alert">{error}</p> : <span />}
        <button type="button" className="exo-dc-tab" onClick={onCancel}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={busy || !name.trim() || !content.trim()} onClick={() => void post()}>Post</button>
      </div>
    </div>
  )
}
