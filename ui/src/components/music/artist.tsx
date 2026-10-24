import { useState } from 'react'
import { host } from '../../lib/host'
import type { PlayerCard, PlayerHeader, PlayerLink, PlayerViewData } from '../../lib/player'
import { ExternalLink } from '../../brand/icons'
import { Art } from './Art'
import { Facts, Failed, Shelf } from './pages'
import { loadView, usePlayer, type Page } from './playerContext'

const count = (n: number) => n.toLocaleString()

/**
 * An artist's page under Top songs, in the order Spotify's own has it: the discography (with all of
 * it a click away where the service has it), on tour, Featuring, Fans also like, Appears on,
 * Discovered on, the artist's playlists, the service's own shelves (YouTube Music, Apple Music)
 * and About.
 */
export function ArtistPage({ page, data }: { page: Page; data: PlayerViewData }) {
  const { has } = usePlayer()
  const all: PlayerLink | undefined = page.uri && has('discography') ? { uri: page.uri, kind: 'discography' } : undefined
  const name = data.header?.title || page.title || ''
  return (
    <>
      <Shelf title="Albums" cards={data.albums ?? []} more={all} />
      <Shelf title="Singles and EPs" cards={data.singles ?? []} more={data.albums?.length ? undefined : all} />
      <Shelf title="Compilations" cards={data.compilations ?? []} />
      {data.concerts?.length ? <Tour concerts={data.concerts} /> : null}
      <Shelf title={`Featuring ${name}`} cards={data.featuring ?? []} />
      <Shelf title="Fans also like" cards={data.related ?? []} />
      <Shelf title="Appears on" cards={data.appearsOn ?? []} />
      <Shelf title="Discovered on" cards={data.discoveredOn ?? []} />
      <Shelf title={`${name}'s playlists`} cards={data.playlists ?? []} />
      {(data.sections ?? []).map((section, index) => <Shelf key={`${index}:${section.title}`} title={section.title} cards={section.items} more={section.more} />)}
      <ArtistAbout data={data} name={name} />
    </>
  )
}

/** Tour dates: the day, where and the venue, from the artist's own page. */
function Tour({ concerts }: { concerts: NonNullable<PlayerViewData['concerts']> }) {
  return (
    <section className="exo-music-section">
      <h2>On tour</h2>
      <ul className="exo-music-list exo-music-tour">
        {concerts.slice(0, 8).map(concert => {
          const when = new Date(concert.date)
          const valid = !Number.isNaN(when.getTime())
          return (
            <li key={`${concert.date}:${concert.place}:${concert.venue}`}>
              <span className="exo-music-tour-day">
                <span>{valid ? when.toLocaleDateString(undefined, { month: 'short' }) : ''}</span>
                <strong>{valid ? when.getDate() : concert.date.slice(0, 10)}</strong>
              </span>
              <span className="exo-music-row-text">
                <span className="exo-music-row-title">{concert.place || concert.title}</span>
                <span className="exo-music-row-sub">{[valid ? when.toLocaleString(undefined, { weekday: 'short', year: when.getFullYear() === new Date().getFullYear() ? undefined : 'numeric', hour: 'numeric', minute: '2-digit' }) : '', concert.venue].filter(Boolean).join(' · ')}</span>
              </span>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

/** About: the artist's picture and biography, followers, monthly listeners, world rank, top cities and links. */
function About({ name, about }: { name: string; about: NonNullable<PlayerViewData['about']> }) {
  const [whole, setWhole] = useState(false)
  if (!about.bio && !about.listeners && !about.followers) return null
  return (
    <section className="exo-music-section exo-music-about">
      <h2>About</h2>
      <div className="exo-music-about-body">
        {about.image ? <Art className="exo-music-about-art" src={about.image} /> : null}
        <div className="exo-music-about-text">
          <p className="exo-music-about-stats">
            {about.rank ? <span><strong>#{count(about.rank)}</strong> in the world</span> : null}
            {about.listeners ? <span><strong>{count(about.listeners)}</strong> monthly listeners</span> : null}
            {about.followers ? <span><strong>{count(about.followers)}</strong> followers</span> : null}
          </p>
          {about.bio ? (
            <>
              <p className={`exo-music-about-bio${whole ? ' is-whole' : ''}`}>{about.bio}</p>
              {about.bio.length > 320 ? <button type="button" className="exo-music-link" onClick={() => setWhole(!whole)}>{whole ? 'Less' : 'More'}</button> : null}
            </>
          ) : null}
          {about.cities.length ? (
            <ul className="exo-music-about-cities" aria-label={`Where people listen to ${name}`}>
              {about.cities.map(city => (
                <li key={`${city.city}:${city.country}`}><span>{city.city}{city.country ? `, ${city.country}` : ''}</span><span>{count(city.listeners)} listeners</span></li>
              ))}
            </ul>
          ) : null}
          {about.links.length ? (
            <p className="exo-music-about-links">
              {about.links.map(link => (
                <button key={link.url} type="button" className="exo-set-btn" onClick={() => void host.openUrl(link.url)}>{link.name}<ExternalLink size={14} /></button>
              ))}
            </p>
          ) : null}
        </div>
      </div>
    </section>
  )
}

/** About: Spotify's own, else the biography the service's page gives (Apple Music). */
function ArtistAbout({ data, name }: { data: PlayerViewData; name: string }) {
  if (data.about) return <About name={name} about={data.about} />
  return data.header ? <Biography header={data.header} /> : null
}

/** An artist's biography and where they come from, as the service's page gives them (Apple Music). */
function Biography({ header }: { header: PlayerHeader }) {
  if (!header.about && !header.facts?.length) return null
  return (
    <section className="exo-music-section exo-music-about">
      <h2>About {header.title}</h2>
      {header.about ? <p className="exo-music-about-bio is-whole">{header.about}</p> : null}
      <Facts facts={header.facts} />
    </section>
  )
}

/** Every release an artist has, newest first, 50 at a time. */
export function DiscographyPage({ page, data }: { page: Page; data: PlayerViewData }) {
  const { adapter } = usePlayer()
  const [more, setMore] = useState<PlayerCard[]>([])
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState(false)
  const items = [...(data.items ?? []), ...more]
  async function next() {
    setBusy(true)
    setFailed(false)
    const result = await loadView(adapter, 'discography', { uri: page.uri, offset: items.length })
    if (result.data?.items) setMore(previous => [...previous, ...result.data!.items!])
    else setFailed(true)
    setBusy(false)
  }
  return (
    <>
      <header className="exo-music-header-text exo-music-disc-head"><h2>Discography</h2>{page.title ? <p className="exo-music-header-sub">{page.title}</p> : null}</header>
      <Shelf title="" cards={items} grid />
      {items.length < (data.total ?? 0) ? (
        <button type="button" className="exo-set-btn exo-music-more" disabled={busy} onClick={() => void next()}>{busy ? 'Loading' : 'Show more'}</button>
      ) : null}
      <Failed when={failed} />
      {!items.length ? <p className="exo-music-empty">No releases yet</p> : null}
    </>
  )
}
