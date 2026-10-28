// The Library, used: every sort, a game's sheet (click, keyboard, close), its menu, Ctrl+K to a game, a folder made,
// opened, recolored and deleted, games taken out of and put into folders (an emptied folder going away, Add all not
// installed), the + menu, Continue's cards, and scrolling the whole grid.
import { sleep } from '../cdp.mjs'

const SORTS = ['Name', 'Installed first', 'Last launched', 'Time played', 'Size', 'Store']
const sortTrigger = () => document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')
const menuItem = (label) => [...document.querySelectorAll('[role^="menuitem"]')].find(i => i.getBoundingClientRect().width && i.textContent.trim() === label)
const tiles = () => [...document.querySelectorAll('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')].map(b => b.dataset.gameId)
const sheetTitle = () => document.querySelector('.exo-game-overlay-stage .exo-game-title')?.textContent.trim() ?? null
// The folder cards by name and count (each card's label reads "Name, folder, N games").
const FOLDERS = `[...document.querySelectorAll('.exo-game-grid .exo-folder-tile button')].map(b => (b.getAttribute('aria-label') ?? '').match(/^(.*), folder, (\\d+) games?$/)).filter(Boolean).map(m => ({ name: m[1], count: +m[2] }))`
const folderButton = (name) => [...document.querySelectorAll('.exo-game-grid .exo-folder-tile button')].find(b => (b.getAttribute('aria-label') ?? '').startsWith(`${name}, folder,`))
const looseGame = (id) => !!document.querySelector(`.exo-game-grid button[data-game-id="${CSS.escape(id)}"]:not(.exo-folder-tile *)`)
const gridGame = (id) => document.querySelector(`.exo-game-grid button[data-game-id="${CSS.escape(id)}"]`)
const panelGame = (id) => document.querySelector(`.exo-folder-panel button[data-game-id="${CSS.escape(id)}"]`)
// After filing: nothing left hidden as "away" (a folder's or a game page's source) and every Continue banner showing.
const STRANDED = `[...document.querySelectorAll('[data-exo-away]')].map(e => 'away: ' + String(e.className).slice(0, 40)).concat([...document.querySelectorAll('.exo-continue-art')].filter(i => getComputedStyle(i).visibility !== 'visible').map(() => 'a Continue banner'))`

async function openFolder(t, name) {
  await t.shell.click(folderButton, name)
  return t.shell.waitFor(() => document.querySelector('.exo-folder-panel')?.querySelectorAll('button[data-game-id]').length, { timeoutMs: 4000 })
}

async function closeFolder(t) {
  if (!await t.shell.eval(`!!document.querySelector('.exo-folder-panel')`)) return
  await t.shell.key('Escape')
  await t.shell.waitFor(() => !document.querySelector('.exo-folder-panel'), { timeoutMs: 3000 })
  await sleep(700)
}

/** The game's right-click menu, then its item starting with `label`; false when either is missing (the menu is closed). */
// A game's menu item by its exact words: a folder is a check item named as the folder ("Folder" is not "Folder 2").
async function fileFrom(t, button, id, label) {
  await t.shell.rightClick(button, id)
  await sleep(450)
  if (await t.shell.click((l) => [...document.querySelectorAll('[role^="menuitem"]')].find(i => i.getBoundingClientRect().width && i.textContent.trim() === l), label)) return true
  await t.shell.key('Escape')
  return false
}

/** A game's sheet through Ctrl+K by its name; false when it didn't open. */
async function openByName(t, title) {
  await t.shell.key('k', { ctrl: true })
  if (!await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 3000 })) return false
  await t.shell.type(title.slice(0, 18))
  if (!await t.shell.waitFor((n) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].some(o => o.textContent.includes(n)), { timeoutMs: 4000, args: [title.slice(0, 12)] })) return false
  await t.shell.key('Enter')
  return !!await t.shell.waitFor(sheetTitle, { timeoutMs: 5000 })
}

async function pickSort(t, label) {
  await t.shell.click(sortTrigger)
  await sleep(350)
  const ok = await t.shell.click(menuItem, label)
  await sleep(500)
  await t.k.settle()
  return ok && (await t.shell.eval(`document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')?.getAttribute('aria-label')`)) === `Sort library, ${label}`
}

export async function run(t) {
  const { run, k } = t
  await k.nav('Library')

  await run.check('library', 'grid', 'All games lists the Library with its count', async (c) => {
    const ids = await t.shell.call(tiles)
    const folders = await t.shell.eval(FOLDERS)
    const count = await t.shell.eval(`Number(document.querySelector('.exo-shelf-count')?.textContent ?? NaN)`)
    c.metric('games', ids.length)
    c.metric('folders', folders.length)
    c.evidence.count = count
    const failures = []
    if (!ids.length && !folders.length) failures.push('no games or folders in All games')
    if (new Set(ids).size !== ids.length) failures.push('a game shows twice')
    if (!Number.isFinite(count)) failures.push('no count beside All games')
    return failures
  })

  // The game taken out of a folder here: folder-solo files it alone, folder-add puts it back (the run's guard restores
  // settings.json at the end as well). With every game in folders, it is also the grid's game for the checks between.
  const filed = { id: null, folder: null }
  await run.check('library', 'folder-remove', 'a game removed from inside its folder goes back to the grid, the counts follow and nothing stays hidden', async (c) => {
    const folders = await t.shell.eval(FOLDERS)
    const from = folders.find(f => f.count > 1) ?? folders[0]
    if (!from) c.skip('no folders')
    const failures = []
    const inside = await openFolder(t, from.name)
    if (inside !== from.count) failures.push(`the open folder holds ${inside ?? 0} games, its card says ${from.count}`)
    const game = await t.shell.eval(`(() => { const b = document.querySelector('.exo-folder-panel button[data-game-id]'); return b && { id: b.dataset.gameId, name: b.getAttribute('aria-label') } })()`)
    if (!game) { await closeFolder(t); return [...failures, 'no game inside the folder'] }
    Object.assign(filed, { id: game.id, folder: from.name })
    c.evidence.game = game.name
    c.evidence.folder = from.name
    // Its menu's Folders section: the folder it is in, ticked; pressing it takes the game out.
    if (!await fileFrom(t, panelGame, game.id, from.name)) { await closeFolder(t); return [...failures, `no ${from.name} in its menu's Folders`] }
    await sleep(900)
    const left = await t.shell.eval(`document.querySelector('.exo-folder-panel')?.querySelectorAll('button[data-game-id]').length ?? 0`)
    if (from.count > 1 && left !== from.count - 1) failures.push(`the open folder holds ${left} after the remove, not ${from.count - 1}`)
    if (await t.shell.eval(`[...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width)`)) failures.push('the menu stayed open')
    await closeFolder(t)
    if (!await t.shell.call(looseGame, game.id)) failures.push(`${game.name} is not back in the grid`)
    const after = (await t.shell.eval(FOLDERS)).find(f => f.name === from.name)
    if (from.count > 1 && after?.count !== from.count - 1) failures.push(`the card says ${after?.count} games, not ${from.count - 1}`)
    if (from.count === 1 && after) failures.push('the emptied folder stayed')
    failures.push(...(await t.shell.eval(STRANDED)).map(x => `left hidden: ${x}`))
    return failures
  })

  await run.check('library', 'sorts', 'every sort orders the grid and the trigger names it', async (c) => {
    const current = (await t.shell.eval(`document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')?.getAttribute('aria-label') ?? ''`)).replace('Sort library, ', '')
    const failures = []
    const orders = {}
    for (const label of SORTS) {
      if (!await pickSort(t, label)) { failures.push(`${label} did not apply`); continue }
      orders[label] = (await t.shell.call(tiles)).slice(0, 12).join(',')
    }
    // With nearly every game in folders there are too few cards in the grid for an order to show.
    const loose = (await t.shell.call(tiles)).length
    c.metric('cards sorted', loose)
    if (loose >= 4 && new Set(Object.values(orders)).size < 3) failures.push('the sorts barely change the order')
    const names = await t.shell.eval(`[...document.querySelectorAll('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')].slice(0, 30).map(b => (b.getAttribute('aria-label') || b.textContent).trim())`)
    if (await pickSort(t, 'Name')) {
      const byName = await t.shell.eval(`[...document.querySelectorAll('.exo-game-grid .exo-tile-title, .exo-game-grid [class*="tile-name"]')].slice(0, 20).map(e => e.textContent.trim())`)
      const sorted = [...byName].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }))
      if (byName.length > 3 && byName.join('|') !== sorted.join('|')) c.warn(`Name isn't alphabetical: ${byName.slice(0, 5).join(', ')}`)
    }
    c.evidence.names = names.slice(0, 5)
    if (current && !await pickSort(t, current)) failures.push(`could not put ${current} back`)
    return failures
  })

  await run.check('library', 'sheet', 'a game opens its sheet by click and by keyboard, and closes', async (c) => {
    const failures = []
    const first = await t.shell.eval(`(() => { const b = document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)'); return b && { id: b.dataset.gameId, name: (b.getAttribute('aria-label') || '').replace(/^Open /, '') } })()`)
    if (!first) c.skip('no game')
    const t0 = Date.now()
    await t.shell.click((id) => document.querySelector(`.exo-game-grid button[data-game-id="${CSS.escape(id)}"]`), first.id)
    const title = await t.shell.waitFor(sheetTitle, { timeoutMs: 5000 })
    c.metric('open', Date.now() - t0, ' ms', 500)
    if (!title) return ['the sheet did not open']
    await sleep(700)
    await c.shot('sheet', t.shell)
    failures.push(...await k.look(c, 'sheet'))
    const play = await t.shell.eval(`!![...document.querySelectorAll('.exo-game-overlay-stage button')].find(b => /^(Play|Install|Update|Starting)/.test(b.textContent.trim()))`)
    if (!play) failures.push('no Play or Install on the sheet')
    await t.shell.key('Escape')
    if (!await t.shell.waitFor(() => !document.querySelector('.exo-game-overlay-stage'), { timeoutMs: 3000 })) failures.push('Escape did not close the sheet')
    await sleep(400)
    // Keyboard: focus the card, Enter opens, the close button closes.
    await t.shell.eval(`document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')?.focus()`)
    await t.shell.key('Enter')
    if (!await t.shell.waitFor(sheetTitle, { timeoutMs: 5000 })) failures.push('Enter on a card did not open its sheet')
    await sleep(500)
    if (!await t.shell.click(() => document.querySelector('.exo-game-overlay-stage .exo-game-close'))) failures.push('no close button')
    if (!await t.shell.waitFor(() => !document.querySelector('.exo-game-overlay-stage'), { timeoutMs: 3000 })) failures.push('the close button did not close the sheet')
    return failures
  })

  await run.check('library', 'game-menu', 'a game\'s right-click menu opens with its actions and closes', async (c) => {
    await t.shell.rightClick(() => document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)'))
    const items = await t.shell.waitFor(() => { const m = [...document.querySelectorAll('[role="menu"]')].find(x => x.getBoundingClientRect().width); return m ? [...m.querySelectorAll('[role^="menuitem"]')].map(i => i.textContent.trim()) : null }, { timeoutMs: 3000 })
    c.evidence.items = items
    if (!items) return ['no menu']
    await c.shot('menu', t.shell)
    await t.shell.key('Escape')
    const closed = await t.shell.waitFor(() => ![...document.querySelectorAll('[role="menu"]')].some(x => x.getBoundingClientRect().width), { timeoutMs: 2000 })
    const failures = []
    if (items.length < 3) failures.push(`only ${items.length} items`)
    if (!items.some(i => /folder/i.test(i))) failures.push('no folder action')
    if (!closed) failures.push('Escape did not close it')
    return failures
  })

  await run.check('library', 'search', 'Ctrl+K finds a game by name and Enter opens it', async (c) => {
    const name = await t.shell.eval(`(document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')?.getAttribute('aria-label') || '').replace(/^Open /, '')`)
    if (!name) c.skip('no game')
    await t.shell.key('k', { ctrl: true })
    if (!await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 3000 })) return ['Ctrl+K did not open the search with its field focused']
    await t.shell.type(name.slice(0, 18))
    const hit = await t.shell.waitFor((n) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].find(o => o.textContent.includes(n.slice(0, 12)))?.textContent.trim().slice(0, 60), { timeoutMs: 4000, args: [name] })
    c.evidence.query = name.slice(0, 18)
    c.evidence.hit = hit
    if (!hit) { await t.shell.key('Escape'); return [`no result for "${name.slice(0, 18)}"`] }
    await t.shell.key('Enter')
    const opened = await t.shell.waitFor(sheetTitle, { timeoutMs: 5000 })
    await k.closeAll()
    return opened ? [] : ['Enter did not open the game']
  })

  // Never pressed: a press writes the game's own settings files.
  await run.check('library', 'optimize', "a game Exo has best settings for shows Optimize with the word its files call for, Applied in green; others show none", async (c) => {
    const games = (await t.host('library.get'))?.games ?? []
    let found = null
    let other = null
    for (const game of games) {
      const state = await t.host('game.settings', { id: game.id })
      if (state?.ok && state.supported) found ??= { game, state }
      else if (state?.ok && game.installed) other ??= game
      if (found && other) break
    }
    if (!found) c.skip('no game here that Exo has best settings for')
    const { game, state } = found
    const on = state.groups?.filter(g => g.on).length ?? 0
    const want = state.running ? `Close ${state.title} first` : !state.found ? `Play ${state.title} once first`
      : on === state.groups.length && on > 0 ? 'Applied' : 'Not optimized'
    c.evidence.game = game.title
    c.evidence.groups = `${on} of ${state.groups?.length ?? 0} on`
    const open = (title) => openByName(t, title)
    const line = () => { const s = document.querySelector('.exo-game-overlay-stage section.exo-game-line[aria-label="Optimize"]'); const b = s?.querySelector('button'); const w = s?.querySelector('.exo-game-line-word'); return s && { word: w?.textContent.trim() ?? '', good: w?.classList.contains('is-good') ?? false, color: w ? getComputedStyle(w).color : '', disabled: b?.disabled ?? false, visible: s.checkVisibility() } }
    if (!await open(game.title)) { await k.closeAll(); return [`Ctrl+K did not open ${game.title}`] }
    const shown = await t.shell.waitFor(() => { const s = document.querySelector('.exo-game-overlay-stage section.exo-game-line[aria-label="Optimize"] .exo-game-line-word'); return s?.textContent.trim() ? true : null }, { timeoutMs: 5000 }) && await t.shell.eval(`(${line})()`)
    c.evidence.line = shown
    const failures = []
    if (!shown) failures.push(`${game.title}'s sheet has no Optimize line`)
    else {
      await sleep(500)
      await c.shot('optimize', t.shell)
      failures.push(...await k.look(c, 'optimize'))
      if (!shown.visible) failures.push('the Optimize line is not visible')
      if (shown.word !== want) failures.push(`Optimize says "${shown.word}", ${game.title}'s files call for "${want}"`)
      if (want === 'Applied' && (!shown.good || !shown.disabled)) failures.push('Applied is not green, or can still be pressed')
      if (want === 'Not optimized' && shown.disabled) failures.push('Not optimized but Optimize cannot be pressed')
    }
    await k.closeAll()
    if (other) {
      c.evidence.other = other.title
      if (!await open(other.title)) failures.push(`Ctrl+K did not open ${other.title}`)
      else {
        await sleep(800)
        if (await t.shell.eval(`!!document.querySelector('.exo-game-overlay-stage section.exo-game-line[aria-label="Optimize"]')`)) failures.push(`${other.title} shows Optimize without best settings`)
      }
      await k.closeAll()
    }
    return failures
  })

  // Never pressed: Rebuild deletes the game's shader cache.
  await run.check('library', 'shaders-rebuild', "Call of Duty's Properties › Installed files shows its shader cache with Rebuild shaders; a game without one shows no such row", async (c) => {
    const games = (await t.host('library.get'))?.games?.filter(g => g.installed) ?? []
    let found = null
    let other = null
    for (const game of games) {
      const props = await t.host('game.properties', { id: game.id })
      if (props?.ok && props.shaderCacheBytes != null) found ??= { game, props }
      else if (props?.ok && /steam|epic/.test(props.store)) other ??= game
      if (found && other) break
    }
    if (!found) c.skip('no game here whose shaders Exo can rebuild')
    const { game, props } = found
    c.evidence.game = game.title
    c.evidence.bytes = props.shaderCacheBytes
    const row = () => { const r = [...document.querySelectorAll('.exo-props .exo-props-row')].find(x => x.querySelector('.exo-set-item-label')?.textContent.trim() === 'Shaders'); if (!r) return null; const b = [...r.querySelectorAll('button')].find(x => x.textContent.trim() === 'Rebuild shaders'); return { text: r.innerText.replace(/\s+/g, ' ').trim(), button: !!b, disabled: b?.disabled ?? null, visible: r.checkVisibility() } }
    const files = async (title) => {
      if (!await openByName(t, title)) return `Ctrl+K did not open ${title}`
      if (!await t.shell.click(() => document.querySelector('.exo-game-overlay-stage button[aria-label="Properties"]'))) return `${title} has no Properties button`
      // The sections come with the game's properties; until they are read only General shows.
      if (!await t.shell.waitFor(() => [...document.querySelectorAll('.exo-props-nav button')].some(b => b.textContent.trim() === 'Installed files' && b.checkVisibility()) || null, { timeoutMs: 15000 })) return 'Properties did not open at its sections'
      if (!await t.shell.click(() => [...document.querySelectorAll('.exo-props-nav button')].find(b => b.textContent.trim() === 'Installed files'))) return 'Properties has no Installed files'
      await t.shell.waitFor(() => document.querySelector('.exo-props .exo-props-row') || null, { timeoutMs: 8000 })
      await sleep(500)
      return null
    }
    const failures = []
    const missing = await files(game.title)
    if (missing) { await k.closeAll(); return [missing] }
    const shown = await t.shell.eval(`(${row})()`)
    c.evidence.row = shown
    await c.shot('shaders-rebuild', t.shell)
    failures.push(...await k.look(c, 'shaders-rebuild'))
    if (!shown) failures.push('Installed files has no Shaders row')
    else {
      if (!shown.button) failures.push('the Shaders row has no Rebuild shaders')
      if (props.shaderCacheBytes > 0 && (shown.disabled || /Builds at next start/.test(shown.text))) failures.push(`${props.shaderCacheBytes} bytes of shaders, but the row reads "${shown.text}" or can't be pressed`)
      if (props.shaderCacheBytes === 0 && (!shown.disabled || !/Builds at next start/.test(shown.text))) failures.push(`no shaders, but the row reads "${shown.text}" or can be pressed`)
    }
    await k.closeAll()
    if (other) {
      c.evidence.other = other.title
      const missingOther = await files(other.title)
      if (missingOther) failures.push(missingOther)
      else if (await t.shell.eval(`(${row})()`)) failures.push(`${other.title} shows a Shaders row`)
      await k.closeAll()
    }
    return failures
  })

  await run.check('library', 'folder', 'a folder is made, shows first, opens, changes color and is deleted', async (c) => {
    const failures = []
    await t.shell.click(() => document.querySelector('.exo-library-add'))
    await sleep(400)
    if (!await t.shell.click(menuItem, 'New folder')) return ['no New folder in the + menu']
    await sleep(700)
    await t.shell.type('Exo e2e')
    await t.shell.click(() => document.querySelector('.exo-folder-swatch[data-color="green"]'))
    // Games in no folder first, so the user's own folders are left whole.
    for (const n of [0, 1]) await t.shell.click((i) => { const free = [...document.querySelectorAll('.exo-folder-pick-item[aria-selected="false"]')]; return [...free.filter(x => !/\bIn \S/.test(x.textContent)), ...free.filter(x => /\bIn \S/.test(x.textContent))][i] }, n)
    await c.shot('sheet', t.shell)
    await t.shell.click(() => [...document.querySelectorAll('.exo-folder-sheet-foot button')].find(b => b.textContent === 'Create'))
    const tile = await t.shell.waitFor(() => { const f = document.querySelector('.exo-game-grid .exo-folder-tile'); return f && f.innerText.includes('Exo e2e') ? { first: f === document.querySelector('.exo-game-grid')?.firstElementChild, color: f.dataset.color } : null }, { timeoutMs: 5000 })
    if (!tile) return ['the folder did not appear']
    if (!tile.first) failures.push('the folder is not first')
    if (tile.color !== 'green') failures.push(`its color is ${tile.color}`)
    await sleep(800)
    await t.shell.click(() => [...document.querySelectorAll('.exo-folder-tile')].find(f => f.innerText.includes('Exo e2e'))?.querySelector('button'))
    const open = await t.shell.waitFor(() => document.querySelector('.exo-folder-panel')?.querySelectorAll('button[data-game-id]').length, { timeoutMs: 4000 })
    if (open !== 2) failures.push(`the open folder holds ${open ?? 0} games, not 2`)
    await c.shot('open', t.shell)
    await t.shell.click(() => document.querySelector('.exo-folder-edit'))
    await sleep(600)
    await t.shell.click(() => document.querySelector('.exo-folder-swatch[data-color="pink"]'))
    await t.shell.click(() => [...document.querySelectorAll('.exo-folder-sheet-foot button')].find(b => b.textContent === 'Save'))
    await sleep(800)
    await t.shell.key('Escape')
    await sleep(900)
    const color = await t.shell.eval(`[...document.querySelectorAll('.exo-folder-tile')].find(f => f.innerText.includes('Exo e2e'))?.dataset.color ?? null`)
    if (color !== 'pink') failures.push(`after Edit its color is ${color}`)
    for (let i = 0; i < 3 && await t.shell.eval(`!![...document.querySelectorAll('.exo-folder-tile')].find(f => f.innerText.includes('Exo e2e'))`); i++) {
      await t.shell.rightClick(() => [...document.querySelectorAll('.exo-folder-tile')].find(f => f.innerText.includes('Exo e2e')).querySelector('button'))
      await sleep(400)
      await t.shell.click(menuItem, 'Delete folder')
      await sleep(600)
      // Deleting asks first, in Exo's dialog (its action last, after Cancel).
      await t.shell.click(() => document.querySelector('.exo-dialog .exo-dialog-actions button:last-child'))
      await sleep(900)
    }
    if (await t.shell.eval(`!![...document.querySelectorAll('.exo-folder-tile')].find(f => f.innerText.includes('Exo e2e'))`)) failures.push('the folder could not be deleted')
    return failures
  })

  await run.check('library', 'folder-solo', "a folder made from a game's menu holds it, and goes away when that game is removed", async (c) => {
    const id = filed.id ?? await t.shell.eval(`document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')?.dataset.gameId ?? null`)
    if (!id) c.skip('no game in the grid')
    const failures = []
    if (!await fileFrom(t, gridGame, id, 'New folder')) return ["no New folder in the game's menu"]
    await sleep(700)
    const ticked = await t.shell.eval(`document.querySelector('.exo-folder-sheet-count[data-picked]')?.textContent ?? null`)
    if (ticked !== '1 game') failures.push(`the new folder starts with ${ticked}, not the game`)
    await t.shell.type('Exo e2e solo')
    await t.shell.click(() => [...document.querySelectorAll('.exo-folder-sheet-foot button')].find(b => b.textContent === 'Create'))
    const made = await t.shell.waitFor((n) => [...document.querySelectorAll('.exo-game-grid .exo-folder-tile button')].some(b => (b.getAttribute('aria-label') ?? '') === `${n}, folder, 1 game`), { timeoutMs: 4000, args: ['Exo e2e solo'] })
    if (!made) return [...failures, 'the folder did not appear holding 1 game']
    if (await t.shell.call(looseGame, id)) failures.push('the game is still in the grid as well')
    await sleep(600)
    await openFolder(t, 'Exo e2e solo')
    if (!await fileFrom(t, panelGame, id, 'Exo e2e solo')) failures.push("no Exo e2e solo in its menu's Folders")
    await sleep(900)
    await closeFolder(t)
    if ((await t.shell.eval(FOLDERS)).some(f => f.name === 'Exo e2e solo')) failures.push('the emptied folder stayed')
    if (!await t.shell.call(looseGame, id)) failures.push('the game is not back in the grid')
    failures.push(...(await t.shell.eval(STRANDED)).map(x => `left hidden: ${x}`))
    return failures
  })

  await run.check('library', 'folder-not-installed', "New folder's Add all not installed ticks every uninstalled game and takes them back", async (c) => {
    await t.shell.click(() => document.querySelector('.exo-library-add'))
    await sleep(400)
    if (!await t.shell.click(menuItem, 'New folder')) { await t.shell.key('Escape'); return ['no New folder in the + menu'] }
    await sleep(700)
    const TOGGLE = `[...document.querySelectorAll('.exo-folder-sheet-tools button')].find(b => /not installed/.test(b.textContent))`
    const label = await t.shell.eval(`${TOGGLE}?.textContent ?? null`)
    if (!label) { await t.shell.key('Escape'); c.skip('every game is installed') }
    const n = Number(label.match(/\((\d+)\)/)?.[1])
    c.metric('not installed', n)
    const failures = []
    await t.shell.eval(`${TOGGLE}.click()`)
    await sleep(300)
    const [count, again] = await t.shell.eval(`[document.querySelector('.exo-folder-sheet-count[data-picked]')?.textContent, ${TOGGLE}?.textContent]`)
    if (count !== `${n} game${n === 1 ? '' : 's'}`) failures.push(`it ticked ${count}, the button said ${n}`)
    if (again !== 'Remove not installed') failures.push(`the button reads "${again}" once they are all ticked`)
    await c.shot('not-installed', t.shell)
    await t.shell.eval(`${TOGGLE}.click()`)
    await sleep(300)
    const back = await t.shell.eval(`document.querySelector('.exo-folder-sheet-count[data-picked]')?.textContent`)
    if (back !== '0 games') failures.push(`taking them back left ${back}`)
    const before = (await t.shell.eval(FOLDERS)).length
    await t.shell.click(() => [...document.querySelectorAll('.exo-folder-sheet-foot button')].find(b => b.textContent === 'Cancel'))
    await sleep(700)
    if (await t.shell.eval(`!!document.querySelector('.exo-folder-sheet')`)) failures.push('Cancel did not close the sheet')
    if ((await t.shell.eval(FOLDERS)).length !== before) failures.push('Cancel made a folder')
    return failures
  })

  await run.check('library', 'folder-add', "a game added to a folder from its menu leaves the grid and shows inside it", async (c) => {
    const folders = await t.shell.eval(FOLDERS)
    const into = folders.find(f => f.name === filed.folder) ?? folders[0]
    const id = filed.id ?? await t.shell.eval(`document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')?.dataset.gameId ?? null`)
    if (!into || !id) c.skip('no folder or no game in the grid')
    const failures = []
    if (!await fileFrom(t, gridGame, id, into.name)) return [`no ${into.name} in the game's menu's Folders`]
    await sleep(900)
    if (await t.shell.call(looseGame, id)) failures.push('the game is still in the grid')
    const after = (await t.shell.eval(FOLDERS)).find(f => f.name === into.name)
    if (after?.count !== into.count + 1) failures.push(`the card says ${after?.count} games, not ${into.count + 1}`)
    await openFolder(t, into.name)
    if (!await t.shell.call((g) => !!document.querySelector(`.exo-folder-panel button[data-game-id="${CSS.escape(g)}"]`), id)) failures.push('the game is not inside the folder')
    await closeFolder(t)
    failures.push(...(await t.shell.eval(STRANDED)).map(x => `left hidden: ${x}`))
    return failures
  })

  await run.check('library', 'add-menu', 'the + menu offers New folder, Add a game and Activate key, each once', async (c) => {
    await t.shell.click(() => document.querySelector('.exo-library-add'))
    const items = await t.shell.waitFor(() => { const m = [...document.querySelectorAll('[role="menu"]')].find(x => x.getBoundingClientRect().width); return m ? [...m.querySelectorAll('[role^="menuitem"]')].map(i => i.textContent.trim()) : null }, { timeoutMs: 3000 })
    await t.shell.key('Escape')
    c.evidence.items = items
    const want = ['New folder', 'Add a game', 'Activate key']
    if (!items) return ['the menu did not open']
    return [...want.filter(w => !items.includes(w)).map(w => `no ${w}`), ...items.filter((w, i) => items.indexOf(w) !== i).map(w => `${w} twice`)]
  })

  await run.check('library', 'continue', 'Continue shows its games with Play, More and their titles on glass', async (c) => {
    const cards = await t.shell.eval(`[...document.querySelectorAll('.exo-continue-card')].map(card => ({ title: card.querySelector('.exo-continue-text strong')?.textContent.trim(), glass: card.querySelector('.exo-continue-text')?.classList.contains('exo-glass'), play: !!card.querySelector('.exo-continue-play'), more: !!card.querySelector('.exo-continue-more') }))`)
    c.evidence.cards = cards
    if (!cards.length) c.skip('nothing to continue')
    const failures = cards.filter(x => !x.title || !x.glass || !x.more).map(x => `${x.title ?? '?'}: ${!x.glass ? 'title not on glass ' : ''}${!x.more ? 'no More' : ''}`)
    await t.shell.click(() => document.querySelector('.exo-continue-more'))
    const menu = await t.shell.waitFor(() => [...document.querySelectorAll('[role="menu"]')].some(x => x.getBoundingClientRect().width), { timeoutMs: 2500 })
    await t.shell.key('Escape')
    if (!menu) failures.push('More did not open a menu')
    return failures
  })

  await run.check('library', 'scroll', 'the whole grid scrolls through with every cover loading', async (c) => {
    const result = await t.shell.eval(`(async () => {
      const pane = document.querySelector('main.exo-library-pane') ?? document.scrollingElement
      const broken = new Set()
      const start = performance.now()
      for (let y = 0; y <= pane.scrollHeight; y += Math.round(pane.clientHeight * 0.8)) {
        pane.scrollTop = y
        await new Promise(r => setTimeout(r, 160))
        for (const img of document.querySelectorAll('.exo-game-grid img')) { const r = img.getBoundingClientRect(); if (r.bottom > 0 && r.top < innerHeight && img.complete && img.naturalWidth === 0 && img.currentSrc) broken.add(img.alt || img.currentSrc) }
      }
      const ms = performance.now() - start
      pane.scrollTop = 0
      return { ms: Math.round(ms), broken: [...broken].slice(0, 10), height: pane.scrollHeight }
    })()`)
    c.evidence.scroll = result
    if (result.broken.length) c.warn(`${result.broken.length} covers failed: ${result.broken.slice(0, 3).join(', ')}`)
    return []
  })

  await run.check('library', 'covers-sharp', 'every cover on screen has the pixels its card needs', async (c) => {
    await t.shell.eval(`(document.querySelector('main.exo-library-pane') ?? document.scrollingElement).scrollTop = 0`)
    const covers = await settledCovers(t)
    if (!covers.length) return ['no covers on screen']
    // Device pixels per CSS pixel on the screen itself: the monitor's DPI times Exo's page zoom.
    const w = await t.win.window()
    const perCss = w.client.w / await t.shell.eval('innerWidth')
    const thumbs = covers.filter(x => x.thumb)
    const sources = thumbs.length ? await t.shell.eval(fullWidths(thumbs.map(x => x.file))) : {}
    const soft = []
    for (const x of covers) {
      const card = Math.ceil(x.cssWidth * perCss)
      // A thumbnail is made at the card's own size and drawn about 1:1 (lib/coverThumb.ts › THUMB_OVERSAMPLE).
      const need = Math.min(card, x.thumb ? sources[x.file] || x.natural : x.natural)
      if (x.natural + 2 < need) soft.push(`${x.title ?? x.id}: ${x.natural} px for a ${card} px card (needs ${need})`)
    }
    c.evidence.perCss = +perCss.toFixed(3)
    c.evidence.sample = covers.slice(0, 6).map(x => ({ title: x.title, natural: x.natural, css: x.cssWidth, thumb: x.thumb, source: sources[x.file] ?? null }))
    c.metric('covers', covers.length)
    return soft.slice(0, 10)
  })

  await run.check('library', 'covers-after-rooms', 'back from the other rooms and Settings, every cover is there within 1.5 s', async (c) => {
    const before = await settledCovers(t)
    if (!before.length) return ['no covers on screen']
    for (const room of ['Discord', 'Music', 'Devices']) { await k.nav(room); await sleep(1500) }
    await t.shell.click(() => document.querySelector('.exo-settings-btn'))
    await sleep(1200)
    await k.closeAll()
    // From the click to every cover painted (k.nav would add its wait for the room's motion to settle).
    const t0 = Date.now()
    await t.shell.click(() => [...document.querySelectorAll('.exo-nav-btn')].find(b => (b.getAttribute('aria-label') || b.textContent).trim().startsWith('Library')))
    const painted = new Set(before.filter(x => x.painted).map(x => x.id))
    let now = []
    while (Date.now() - t0 < 3000) {
      now = await t.shell.eval(COVERS)
      if (now.length && now.every(x => x.painted || !painted.has(x.id))) break
      await sleep(50)
    }
    c.metric('back', Date.now() - t0, ' ms', 1500)
    await k.settle()
    const blank = now.filter(x => !x.painted && painted.has(x.id)).map(x => x.title ?? x.id)
    return blank.length ? [`${blank.length} covers blank after the rooms: ${blank.slice(0, 4).join(', ')}`] : []
  })
}

const THUMB = 'https://thumbs.exo-launcher.local/', FULL = 'https://covers.exo-launcher.local/'
/** Every card on screen: the cover it draws, how big it is drawn and how many pixels it has. */
const COVERS = `[...document.querySelectorAll('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')].map(b => {
  const pane = document.querySelector('main.exo-library-pane')?.getBoundingClientRect() ?? { top: 0, bottom: innerHeight }
  const r = b.getBoundingClientRect()
  if (!r.width || r.bottom < pane.top + 4 || r.top > pane.bottom - 4) return null
  const img = b.querySelector('img.exo-cover-front:not([data-under])')
  const src = img ? img.currentSrc || img.src : ''
  const ir = (img ?? b).getBoundingClientRect()
  return {
    id: b.dataset.gameId, title: b.querySelector('.exo-card-title')?.textContent.trim() || img?.alt || null,
    painted: !!img && img.complete && img.naturalWidth > 0 && getComputedStyle(img).opacity === '1',
    natural: img?.naturalWidth ?? 0, cssWidth: ir.width,
    thumb: src.startsWith('${THUMB}'), file: src.startsWith('${THUMB}') ? decodeURIComponent(new URL(src).pathname.slice(1)) : null,
  }
}).filter(Boolean)`

/** The full cached file's width behind each thumbnail. */
const fullWidths = (files) => `(async () => {
  const out = {}
  await Promise.all(${JSON.stringify(files)}.map(f => new Promise(done => {
    const i = new Image(), t = setTimeout(done, 5000)
    i.onload = () => { clearTimeout(t); out[f] = i.naturalWidth; done() }
    i.onerror = () => { clearTimeout(t); done() }
    i.src = '${FULL}' + encodeURIComponent(f)
  })))
  return out
})()`

async function settledCovers(t, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs
  let covers = []
  while (Date.now() < deadline) {
    covers = await t.shell.eval(COVERS)
    if (covers.length && covers.every(x => x.painted)) break
    await sleep(250)
  }
  return covers
}
