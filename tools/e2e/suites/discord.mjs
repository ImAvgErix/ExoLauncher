// Exo's Discord client, used. Anything that sends or changes something happens only in the test place, Uhh
// #mod-chat, and is undone (a test message is reacted to and deleted). Nothing joins a call.
import { sleep } from '../cdp.mjs'

const TEST_SERVER = 'Uhh', TEST_CHANNEL = 'mod-chat'
const chat = () => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') ?? null

async function openTestChannel(t) {
  await t.shell.key('k', { ctrl: true })
  await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 3000 })
  await t.shell.type(TEST_CHANNEL)
  const option = await t.shell.waitFor((n, s) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].some(o => { const x = o.querySelectorAll('span span'); return x[0]?.textContent.trim() === n && x[1]?.textContent.trim() === s }), { timeoutMs: 8000, args: [TEST_CHANNEL, TEST_SERVER] })
  if (!option) { await t.shell.key('Escape'); return false }
  await t.shell.click((n, s) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].find(o => { const x = o.querySelectorAll('span span'); return x[0]?.textContent.trim() === n && x[1]?.textContent.trim() === s }), TEST_CHANNEL, TEST_SERVER)
  return !!await t.shell.waitFor((n) => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') === n && document.querySelectorAll('.exo-dc-msg').length > 0, { timeoutMs: 10000, args: [TEST_CHANNEL] })
}

export async function run(t) {
  const { run, k } = t
  await k.nav('Discord')
  const ready = await t.shell.waitFor(() => document.querySelector('main.exo-dc') ? 'client' : document.querySelector('main.exo-dc-signin') ? 'signin' : null, { timeoutMs: 30000 })

  await run.check('discord', 'signed-in', 'Discord opens signed in, with servers, DMs and you', async (c) => {
    if (ready !== 'client') c.skip(ready === 'signin' ? 'not signed in to Discord' : 'the room did not load')
    const parts = await t.shell.eval(`({ servers: document.querySelectorAll('.exo-dc-rail .exo-dc-guild:not(.is-add)').length, dms: document.querySelectorAll('.exo-dc-item.is-dm').length, me: document.querySelector('.exo-dc-me-who')?.textContent.trim().length > 0 })`)
    c.evidence.parts = { servers: parts.servers, dms: parts.dms }
    return [!parts.servers && 'no servers', !parts.me && 'no user panel'].filter(Boolean)
  })
  if (ready !== 'client') return

  await run.check('discord', 'lists-clear-panel', 'the server rail and DM list end clear of the user panel', async (c) => {
    const ends = await t.shell.eval(`(async () => {
      const out = {}
      for (const [name, sel] of [['rail', '.exo-dc-rail .exo-dc-guild'], ['dms', '.exo-dc-item.is-dm']]) {
        const els = [...document.querySelectorAll(sel)]; const el = els[els.length - 1]
        if (!el) { out[name] = 'none'; continue }
        let sc = el.parentElement; while (sc && !(sc.scrollHeight > sc.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(sc).overflowY))) sc = sc.parentElement
        if (sc) { sc.scrollTop = sc.scrollHeight; await new Promise(r => setTimeout(r, 300)) }
        const r = el.getBoundingClientRect(), top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
        out[name] = top === el || el.contains(top) ? 'clear' : 'under ' + (top?.className?.toString().slice(0, 40) ?? '?')
        if (sc) sc.scrollTop = 0
      }
      return out
    })()`)
    c.evidence.ends = ends
    return Object.entries(ends).filter(([, v]) => v !== 'clear' && v !== 'none').map(([n, v]) => `${n}'s last item is ${v}`)
  })

  await run.check('discord', 'channel', 'Ctrl+K opens the test channel with its messages', async (c) => {
    const t0 = Date.now()
    if (!await openTestChannel(t)) return [`could not open ${TEST_SERVER} #${TEST_CHANNEL}`]
    c.metric('open', Date.now() - t0, ' ms', 2500)
    await sleep(600)
    await c.shot('channel', t.shell)
    return k.look(c, 'channel')
  })

  /** Deletes your test message through its right-click menu; true once it is gone. Tried up to three times. */
  async function deleteMessage(text) {
    for (let attempt = 0; attempt < 3; attempt++) {
      await k.closeAll()
      const hit = await t.shell.call((x) => { const m = [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x)); if (!m) return 'gone'; m.scrollIntoView({ block: 'center' }); const r = m.getBoundingClientRect(); m.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: r.left + 200, clientY: r.top + 20, button: 2 })); return 'menu' }, text)
      if (hit === 'gone') return true
      await sleep(600)
      await t.shell.click(() => [...document.querySelectorAll('[role^="menuitem"], button')].find(b => b.textContent.trim() === 'Delete Message'))
      await sleep(600)
      await t.shell.click(() => { const d = document.querySelector('[role="dialog"], [role="alertdialog"]'); return d && [...d.querySelectorAll('button')].find(b => b.textContent.trim() === 'Delete') })
      if (await t.shell.waitFor((x) => ![...document.querySelectorAll('.exo-dc-msg')].some(e => e.textContent.includes(x)), { timeoutMs: 6000, args: [text] })) return true
    }
    return false
  }

  await run.check('discord', 'message', 'a message is sent, reacted to, and deleted (test channel only)', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    const text = `Exo e2e ${new Date().toISOString().slice(11, 19)}`
    const failures = []
    let sent = false
    try {
      await t.shell.click(() => document.querySelector('main.exo-dc [contenteditable="true"], main.exo-dc textarea'))
      await t.shell.type(text)
      await t.shell.key('Enter')
      sent = !!await t.shell.waitFor((x) => [...document.querySelectorAll('.exo-dc-msg')].slice(-3).some(m => m.textContent.includes(x)), { timeoutMs: 10000, args: [text] })
      if (!sent) return ['the message did not appear']
      // Hover it until Discord's bar shows, Add Reaction, the first emoji, then the reaction taken back.
      let bar = false
      for (let i = 0; i < 4 && !bar; i++) {
        const at = await t.shell.call((x, n) => { const m = [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x)); if (!m) return null; m.scrollIntoView({ block: 'center' }); const r = m.getBoundingClientRect(); return { x: r.left + r.width / 2 + n * 7, y: r.top + r.height / 2 } }, text, i)
        if (!at) break
        await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y })
        bar = !!await t.shell.waitFor((x) => { const b = [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('button[aria-label="Add Reaction"]'); return b && b.getBoundingClientRect().width > 0 }, { timeoutMs: 1500, args: [text] })
      }
      if (!bar) failures.push('the hover bar did not show')
      else if (await t.shell.click((x) => [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('button[aria-label="Add Reaction"]') ?? null, text)) {
        await t.shell.waitFor(() => document.querySelector('.exo-dc-picker .exo-dc-picker-emoji'), { timeoutMs: 4000 })
        await t.shell.click(() => document.querySelector('.exo-dc-picker .exo-dc-picker-emoji:not(.is-locked)'))
        const reacted = await t.shell.waitFor((x) => !![...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('.exo-dc-reaction'), { timeoutMs: 6000, args: [text] })
        if (!reacted) failures.push('the reaction did not show')
        else {
          await t.shell.click((x) => [...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('.exo-dc-reaction') ?? null, text)
          if (!await t.shell.waitFor((x) => ![...document.querySelectorAll('.exo-dc-msg')].reverse().find(e => e.textContent.includes(x))?.querySelector('.exo-dc-reaction'), { timeoutMs: 6000, args: [text] })) failures.push('the reaction could not be taken back')
        }
      }
    } finally {
      // Whatever happened above, the test message goes.
      if (sent && !await deleteMessage(text)) failures.push('THE TEST MESSAGE COULD NOT BE DELETED: delete it by hand')
    }
    return failures
  })

  await run.check('discord', 'invites', 'an invite link joins in the room; a dead one shows why', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    const home = await t.host('discord.home').catch(() => null)
    const guild = home?.guilds?.find(g => g.name === TEST_SERVER)
    const list = guild ? await t.host('discord.channels', { guildId: guild.id }).catch(() => null) : null
    const channelId = list?.channels?.find(ch => ch.name === TEST_CHANNEL)?.id ?? null
    const invite = channelId ? await t.host('discord.channel', { channelId, action: 'invite' }).catch(() => null) : null
    const failures = []
    if (invite?.url) {
      await t.shell.click(() => document.querySelector('.exo-dc-rail .exo-dc-home, .exo-dc-rail button'))
      await sleep(800)
      await t.shell.eval(`window.dispatchEvent(new CustomEvent('exo-dc-invite', { detail: ${JSON.stringify(invite.url.split('/').pop())} }))`)
      if (!await t.shell.waitFor((n) => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') === n, { timeoutMs: 8000, args: [TEST_CHANNEL] })) failures.push('the invite did not open its channel')
    } else c.warn('no channel id to make an invite (the live invite step was left out)')
    await t.shell.eval(`window.dispatchEvent(new CustomEvent('exo-dc-invite', { detail: 'exoE2eNoSuchInvite' }))`)
    const why = await t.shell.waitFor(() => document.querySelector('.exo-dc-modal')?.innerText.match(/Discord: .+/)?.[0] ?? null, { timeoutMs: 8000 })
    c.evidence.dead = why
    if (!why) failures.push('a dead invite showed no reason')
    await k.closeAll()
    return failures
  })

  await run.check('discord', 'server-pages', 'the test server\'s Guide, Events, Browse Channels and Members open', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test server')
    const failures = []
    for (const page of ['Server Guide', 'Events', 'Browse Channels', 'Members']) {
      if (!await t.shell.click((p) => [...document.querySelectorAll('.exo-dc-side button, .exo-dc-side a')].find(b => b.textContent.trim() === p), page)) { failures.push(`no ${page}`); continue }
      await sleep(1200)
      const shown = await t.shell.eval(`!!document.querySelector('main.exo-dc .exo-dc-page, main.exo-dc [class*="guide"], main.exo-dc [class*="events"], main.exo-dc [class*="browse"], main.exo-dc [class*="members"]')`)
      if (!shown) failures.push(`${page} showed nothing`)
    }
    await openTestChannel(t)
    return failures
  })

  await run.check('discord', 'settings', 'Discord\'s settings open, change section and close', async (c) => {
    await k.closeAll()
    const clicked = await t.shell.click(() => document.querySelector('main.exo-dc button[aria-label="Discord settings"]'))
    if (!await t.shell.waitFor(() => !!document.querySelector('.exo-dc-settings'), { timeoutMs: 6000 })) {
      await c.shot('not-open', t.shell)
      c.evidence.why = await t.shell.eval(`({ dialogs: [...document.querySelectorAll('[role=dialog]')].map(d => d.getAttribute('aria-label')), active: document.activeElement?.className?.toString().slice(0, 40) })`)
      return [`the settings did not open (clicked: ${clicked}; ${JSON.stringify(c.evidence.why)})`]
    }
    const failures = []
    for (const section of ['Voice & Video', 'Appearance', 'Notifications']) {
      if (!await t.shell.click((s) => [...document.querySelectorAll('.exo-dc-settings button, .exo-dc-settings [role="tab"]')].find(b => b.textContent.trim() === s), section)) { failures.push(`no ${section}`); continue }
      await sleep(500)
    }
    await c.shot('settings', t.shell)
    failures.push(...await k.look(c, 'settings'))
    await t.shell.key('Escape')
    if (!await t.shell.waitFor(() => !document.querySelector('.exo-dc-settings'), { timeoutMs: 3000 })) failures.push('Escape did not close the settings')
    return failures
  })

  await run.check('discord', 'profile', 'a member\'s profile opens from the member list and closes', async (c) => {
    if (await t.shell.call(chat) !== TEST_CHANNEL && !await openTestChannel(t)) c.skip('not in the test channel')
    const opened = await t.shell.click(() => document.querySelector('.exo-dc-members [data-dc-user], .exo-dc-members button'))
    if (!opened) c.skip('no member list')
    const card = await t.shell.waitFor(() => !!document.querySelector('.exo-dc-profile, .exo-dc-popover'), { timeoutMs: 3000 })
    await c.shot('profile', t.shell)
    await t.shell.key('Escape')
    const closed = await t.shell.waitFor(() => !document.querySelector('.exo-dc-popover'), { timeoutMs: 2000 })
    return [!card && 'no profile card', !closed && 'Escape did not close it'].filter(Boolean)
  })
}
