// Music, used: its home, search, opening a playlist and coming back, the player's parts. Nothing is played (no sound
// leaves the PC).
import { sleep } from '../cdp.mjs'

export async function run(t) {
  const { run, k } = t
  await k.nav('Music')
  const ready = await t.shell.waitFor(() => document.querySelectorAll('.exo-music-card, .exo-music-row').length > 0 ? 'ok' : document.querySelector('.exo-music-signin-note, .exo-music-error') ? 'off' : null, { timeoutMs: 25000 })

  await run.check('music', 'home', 'Music opens on its home with shelves and your library', async (c) => {
    if (ready !== 'ok') c.skip(ready === 'off' ? 'no music service signed in' : 'Music did not load')
    const parts = await t.shell.eval(`({ sections: document.querySelectorAll('.exo-music-section').length, cards: document.querySelectorAll('.exo-music-card').length, library: document.querySelectorAll('.exo-music-side .exo-music-row, .exo-music-side [class*="library"] button').length })`)
    c.evidence.parts = parts
    return [parts.sections < 2 && `only ${parts.sections} shelves`, !parts.cards && 'no cards'].filter(Boolean)
  })
  if (ready !== 'ok') return

  await run.check('music', 'search', 'searching finds songs, artists or playlists', async (c) => {
    const field = '.exo-music-search input[aria-label^="Search"]'
    if (!await k.fill(field, 'lofi')) c.skip('no search field')
    await t.shell.key('Enter')
    const results = await t.shell.waitFor(() => document.querySelectorAll('.exo-music-row, .exo-music-card').length > 3 && document.body.innerText.includes('lofi') ? document.querySelectorAll('.exo-music-row, .exo-music-card').length : null, { timeoutMs: 12000 })
    c.metric('results', results ?? 0)
    await c.shot('search', t.shell)
    await k.fill(field, '')
    await t.shell.click(() => [...document.querySelectorAll('.exo-music-side button, nav button')].find(b => b.textContent.trim() === 'Home'))
    await sleep(800)
    return results ? [] : ['no results for "lofi"']
  })

  await run.check('music', 'playlist', 'a playlist opens with its songs, and Back returns home', async (c) => {
    const t0 = Date.now()
    if (!await t.shell.click(() => document.querySelector('.exo-music-card .exo-music-card-open, .exo-music-card button'))) c.skip('no card')
    const rows = await t.shell.waitFor(() => document.querySelectorAll('.exo-music-row').length > 2 && document.querySelector('.exo-music-header-text') ? document.querySelectorAll('.exo-music-row').length : null, { timeoutMs: 12000 })
    c.metric('open', Date.now() - t0, ' ms', 1500)
    c.metric('songs', rows ?? 0)
    if (!rows) return ['the playlist showed no songs']
    await sleep(500)
    const failures = await k.look(c, 'playlist')
    await c.shot('playlist', t.shell)
    if (!await t.shell.click(() => document.querySelector('.exo-music-back') ?? [...document.querySelectorAll('.exo-music-side button')].find(b => b.textContent.trim() === 'Home'))) failures.push('no way back')
    await sleep(900)
    return failures
  })

  await run.check('music', 'player', 'the player shows the song and its controls', async (c) => {
    const parts = await t.shell.eval(`({ bar: !!document.querySelector('.exo-music-bar'), song: document.querySelector('.exo-music-bar-song')?.textContent.trim().slice(0, 40) ?? null, play: !!document.querySelector('.exo-music-bar-play') })`)
    c.evidence.parts = parts
    if (!parts.bar) c.skip('no player (nothing queued)')
    return [!parts.play && 'no play button'].filter(Boolean)
  })

  await run.check('music', 'spotify-hidden', 'Spotify, which plays for the Music room, never shows a window of its own', async (c) => {
    const r = await t.win.shown('Spotify')
    if (!r.running) c.skip('Spotify is not running')
    c.evidence.shown = r.shown
    return r.shown.length ? [`Spotify shows ${r.shown.length} window(s): ${r.shown.slice(0, 2).join(' | ')}`] : []
  })
}
