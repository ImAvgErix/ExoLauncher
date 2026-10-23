// Probes that run inside Exo's page (passed to shell.call, so each is self-contained). They look for what a person
// would see as broken: words that can't be read, controls cut off or covered, the wrong theme's color showing,
// missing pictures, controls without a name, frames that came late.

/** Which room shows, the theme, whether a transition is stuck, what floats open, an error screen. */
export function roomState() {
  const current = document.querySelector('nav [aria-current="page"], [role="tablist"] [aria-current="page"], .exo-nav-btn[aria-current="page"]')
  const text = document.body.innerText
  return {
    room: current ? (current.getAttribute('aria-label') || current.textContent || '').trim().replace(/\d+$/, '').trim() : null,
    theme: document.documentElement.dataset.exoTheme ?? null,
    vt: document.documentElement.hasAttribute('data-exo-vt'),
    dialogs: document.querySelectorAll('[role="dialog"], [role="alertdialog"], dialog[open]').length,
    menus: [...document.querySelectorAll('[role="menu"]')].filter(m => m.getBoundingClientRect().width > 0 && getComputedStyle(m).visibility !== 'hidden').length,
    errorScreen: /something went wrong|unexpected error|this room stopped/i.test(text),
    settings: !!document.querySelector('.exo-settings')?.checkVisibility(),
    sheet: !!document.querySelector('.exo-game-overlay-stage')?.checkVisibility(),
  }
}

/** Resolves once nothing animates (finite animations) and the DOM has been quiet for `quietMs`, or at `timeoutMs`. */
export function settle(quietMs = 250, timeoutMs = 6000) {
  return new Promise(resolve => {
    const started = performance.now()
    let last = performance.now()
    const mo = new MutationObserver(() => { last = performance.now() })
    mo.observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true })
    const tick = () => {
      const now = performance.now()
      const busy = document.documentElement.hasAttribute('data-exo-vt') || document.getAnimations().some(a => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming().endTime ?? Infinity))
      if ((!busy && now - last >= quietMs) || now - started >= timeoutMs) { mo.disconnect(); resolve(Math.round(now - started)) }
      else requestAnimationFrame(tick)
    }
    requestAnimationFrame(tick)
  })
}

/** Layout faults: the page scrolling sideways, controls cut off by the window, controls something covers, cut words. */
export function layoutScan() {
  const vw = innerWidth, vh = innerHeight
  const visible = (el) => {
    const r = el.getBoundingClientRect()
    if (r.width < 2 || r.height < 2) return null
    for (let a = el; a && a !== document.documentElement; a = a.parentElement) {
      const cs = getComputedStyle(a)
      if (cs.visibility === 'hidden' || cs.display === 'none' || +cs.opacity < 0.05 || a.getAttribute('aria-hidden') === 'true' || a.inert) return null
    }
    return r
  }
  const clipped = (el, r) => {
    // Inside something that scrolls or clips it on purpose (a carousel, a list): not the window's fault.
    for (let a = el.parentElement; a && a !== document.body; a = a.parentElement) {
      const cs = getComputedStyle(a)
      if (/(auto|scroll|hidden|clip)/.test(cs.overflow + cs.overflowX + cs.overflowY)) {
        const ar = a.getBoundingClientRect()
        if (r.right > ar.right + 1 || r.left < ar.left - 1 || r.bottom > ar.bottom + 1 || r.top < ar.top - 1) return true
      }
    }
    return false
  }
  const name = (el) => (el.getAttribute('aria-label') || el.textContent || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 50)
  const issues = { sideScroll: document.scrollingElement.scrollWidth > vw + 1, offscreen: [], covered: [], cutText: [] }
  const controls = [...document.querySelectorAll('button, a[href], input:not([type=hidden]), select, textarea, [role="button"], [role="tab"], [role="menuitem"], [role="option"]')]
  for (const el of controls) {
    const r = visible(el)
    if (!r) continue
    const partly = r.right > vw + 1 || r.bottom > vh + 1 || r.left < -1 || r.top < -1
    const inside = r.left < vw && r.top < vh && r.right > 0 && r.bottom > 0
    if (partly && inside && !clipped(el, r)) issues.offscreen.push(`${name(el)} @${Math.round(r.left)},${Math.round(r.top)} ${Math.round(r.width)}x${Math.round(r.height)}`)
    // A backdrop the size of the window (a sheet's scrim) is meant to be under what it holds.
    if (!partly && !clipped(el, r) && getComputedStyle(el).pointerEvents !== 'none' && r.width * r.height < vw * vh * 0.5) {
      const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)
      if (top && top !== el && !el.contains(top) && !top.contains(el)) {
        // A list that scrolls under floating glass (Discord's user panel, the player) brings it out at its end: fine.
        let scrolls = false
        for (let a = el.parentElement; a && a !== document.body; a = a.parentElement) {
          if (a.scrollHeight > a.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(a).overflowY) && a.scrollHeight - a.clientHeight - a.scrollTop > 1) { scrolls = true; break }
        }
        // Under an open sheet, dialog or menu: covered on purpose.
        const layer = top.closest('[role="dialog"], [role="menu"], .exo-game-overlay, .exo-dc-modal-back, .exo-folder-back, .exo-folder-sheet-back, [popover], [class*="backdrop"], [class*="scrim"]')
        if (layer && !layer.contains(el)) scrolls = true
        // A backdrop the size of the window over everything (a dialog's scrim) is meant to cover what is under it.
        const tr = top.getBoundingClientRect()
        if (tr.width * tr.height > vw * vh * 0.5) scrolls = true
        if (!scrolls && getComputedStyle(top).pointerEvents !== 'none') issues.covered.push(`${name(el)} under ${top.tagName.toLowerCase()}.${[...top.classList].slice(0, 2).join('.')}`)
      }
    }
  }
  for (const el of document.querySelectorAll('body *')) {
    if (el.children.length || !el.textContent || el.textContent.trim().length < 3) continue
    const cs = getComputedStyle(el)
    if (!/(hidden|clip)/.test(cs.overflow + cs.overflowX)) continue
    const r = visible(el)
    if (!r || r.right < 0 || r.left > vw || r.bottom < 0 || r.top > vh) continue
    const wide = el.scrollWidth > el.clientWidth + 1 && cs.textOverflow !== 'ellipsis'
    const tall = el.scrollHeight > el.clientHeight + 2 && cs.webkitLineClamp === 'none' && cs.whiteSpace !== 'nowrap'
    if (wide || tall) issues.cutText.push(`"${el.textContent.trim().slice(0, 40)}" ${wide ? 'cut at the side' : 'cut at the foot'}`)
  }
  issues.offscreen = [...new Set(issues.offscreen)].slice(0, 15)
  issues.covered = [...new Set(issues.covered)].slice(0, 15)
  issues.cutText = [...new Set(issues.cutText)].slice(0, 15)
  return issues
}

/**
 * Words against what is behind them (WCAG contrast). Only where the background can be known from styles: words over
 * a picture, glass or a gradient are left out (their glass keeps them readable by its own rules). Returns the worst.
 */
export function contrastScan(limit = 500) {
  const parse = (c) => { const m = c.match(/rgba?\(([^)]+)\)/); if (!m) return null; const p = m[1].split(/[ ,/]+/).filter(Boolean).map(Number); return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1] }
  const lum = ([r, g, b]) => { const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b) }
  const over = (top, under) => { const a = top[3]; return [top[0] * a + under[0] * (1 - a), top[1] * a + under[1] * (1 - a), top[2] * a + under[2] * (1 - a), 1] }
  const page = parse(getComputedStyle(document.body).backgroundColor) ?? [0, 0, 0, 1]
  const results = []
  const seen = new Set()
  for (const el of document.querySelectorAll('body *')) {
    if (results.length >= limit) break
    const own = [...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim().length > 1)
    if (!own || seen.has(el)) continue
    seen.add(el)
    const r = el.getBoundingClientRect()
    if (r.width < 4 || r.height < 4 || r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) continue
    if (el.closest('button:disabled, [aria-disabled="true"], [inert], [aria-hidden="true"]')) continue
    let alpha = 1, hidden = false
    for (let a = el; a; a = a.parentElement) { const cs = getComputedStyle(a); alpha *= +cs.opacity; if (cs.visibility === 'hidden' || cs.display === 'none') hidden = true }
    if (hidden || alpha < 0.9) continue
    // The background: the element's own and its ancestors', composited until one is opaque. Glass, a gradient or
    // a picture in that stack (a picture counts when it sits above the opaque layer) can't be known: left out.
    const glassy = (u) => { const cs = getComputedStyle(u); if (cs.backgroundImage !== 'none' || cs.backdropFilter !== 'none') return true
      return ['::before', '::after'].some(p => { const ps = getComputedStyle(u, p); return ps.content !== 'none' && (ps.backdropFilter !== 'none' || ps.backgroundImage !== 'none') }) }
    const layers = []
    let opaque = null, complex = false
    for (let u = el; u; u = u.parentElement) {
      if (glassy(u)) { complex = true; break }
      const c = parse(getComputedStyle(u).backgroundColor)
      if (c && c[3] > 0) { layers.push(c); if (c[3] >= 0.99) { opaque = u; break } }
    }
    if (complex) continue
    const cx = Math.min(innerWidth - 1, r.left + Math.min(r.width / 2, 20)), cy = r.top + r.height / 2
    const media = document.elementsFromPoint(cx, cy).filter(u => ['IMG', 'VIDEO', 'CANVAS', 'IFRAME', 'PICTURE'].includes(u.tagName) && !el.contains(u))
    if (media.some(m => !opaque || opaque.contains(m))) continue
    let bg = opaque ? [0, 0, 0, 1] : page
    for (const c of layers.reverse()) bg = over(c, bg)
    const fg0 = parse(getComputedStyle(el).color)
    if (!fg0) continue
    const fg = over(fg0, bg)
    const L1 = lum(fg), L2 = lum(bg)
    const ratio = (Math.max(L1, L2) + 0.05) / (Math.min(L1, L2) + 0.05)
    const size = parseFloat(getComputedStyle(el).fontSize), weight = +getComputedStyle(el).fontWeight
    const large = size >= 24 || (size >= 18.6 && weight >= 700)
    results.push({ text: el.textContent.trim().replace(/\s+/g, ' ').slice(0, 40), ratio: +ratio.toFixed(2), need: large ? 3 : 4.5, fg: fg0.slice(0, 3).map(Math.round).join(','), bg: bg.slice(0, 3).map(Math.round).join(','), cls: [...el.classList].slice(0, 2).join('.') })
  }
  const bad = results.filter(x => x.ratio < x.need).sort((a, b) => a.ratio - b.ratio)
  return { checked: results.length, bad: bad.slice(0, 20), worst: bad[0] ?? null }
}

/** Every readable label in view: its box, color, size and weight, for the pixel contrast check (lib/contrast.mjs). */
export function textBoxes(limit = 600) {
  const parse = (c) => { const m = c.match(/rgba?\(([^)]+)\)/); if (!m) return null; const p = m[1].split(/[ ,/]+/).filter(Boolean).map(Number); return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1] }
  const out = []
  // With something open over the page (a dialog, a menu, a sheet), only its own words are measured.
  const layers = [...document.querySelectorAll('[role="dialog"], [role="alertdialog"], [role="menu"], .exo-game-overlay-stage')].filter(l => l.checkVisibility() && l.getBoundingClientRect().width > 0)
  for (const el of document.querySelectorAll('body *')) {
    if (out.length >= limit) break
    if (layers.length && !layers.some(l => l.contains(el))) continue
    const node = [...el.childNodes].find(n => n.nodeType === 3 && n.textContent.trim().length > 1)
    if (!node) continue
    const range = document.createRange(); range.selectNodeContents(node)
    const r = range.getBoundingClientRect()
    if (r.width < 6 || r.height < 6 || r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) continue
    if (el.closest('button:disabled, [aria-disabled="true"], [inert], [aria-hidden="true"], input, textarea, .sr-only')) continue
    let alpha = 1, hidden = false
    for (let a = el; a; a = a.parentElement) { const cs = getComputedStyle(a); alpha *= +cs.opacity; if (cs.visibility === 'hidden' || /rect\(0/.test(cs.clip) || cs.clipPath === 'inset(50%)') hidden = true }
    if (hidden || alpha < 0.9) continue
    const cs = getComputedStyle(el)
    const color = parse(cs.webkitTextFillColor && cs.webkitTextFillColor !== cs.color ? cs.webkitTextFillColor : cs.color)
    if (!color || color[3] < 0.05) continue
    // Scrolled out of (or cut by) a container that clips: not on view.
    let cut = false
    for (let a = el.parentElement; a && a !== document.body; a = a.parentElement) {
      const st = getComputedStyle(a)
      if (/(auto|scroll|hidden|clip)/.test(st.overflow + st.overflowX + st.overflowY)) {
        const ar = a.getBoundingClientRect()
        if (r.top < ar.top - 1 || r.bottom > ar.bottom + 1 || r.left < ar.left - 1 || r.right > ar.right + 1) { cut = true; break }
      }
    }
    if (cut) continue
    // Partly under something on top (the player, a menu): left out, its words aren't all on view.
    const covered = [0.1, 0.5, 0.9].some(f => {
      const top = document.elementFromPoint(Math.min(innerWidth - 1, r.left + r.width * f), Math.min(innerHeight - 1, r.top + r.height / 2))
      return top && top !== el && !el.contains(top) && !top.contains(el)
    })
    if (covered) continue
    // Words painted by their background (gradient names) or in a color someone chose (Discord's name styles and role
    // colors are the user's own): measured, but reported apart.
    let painted = false
    for (let a = el; a && a !== document.body; a = a.parentElement) { const st = getComputedStyle(a); if (st.backgroundClip === 'text' || st.webkitBackgroundClip === 'text') { painted = true; break } }
    if (painted) continue
    const chosen = !!el.closest('[style*="color"], [style*="--dn-"], [style*="--exo-dc-role"]')
    const size = parseFloat(cs.fontSize), weight = +cs.fontWeight
    out.push({ x: r.left, y: r.top, w: r.width, h: r.height, color, size, weight, chosen, text: node.textContent.trim().replace(/\s+/g, ' ').slice(0, 40), cls: [...el.classList].slice(0, 2).join('.') })
  }
  return out
}

/** Hides every word (and only words) so a screenshot shows what is behind them; call again with false to undo. */
export function hideText(on) {
  let style = document.getElementById('exo-e2e-hide-text')
  if (on && !style) {
    style = document.createElement('style'); style.id = 'exo-e2e-hide-text'
    style.textContent = '*, *::before, *::after { color: transparent !important; -webkit-text-fill-color: transparent !important; text-shadow: none !important; caret-color: transparent !important; transition: none !important; } svg, svg * { visibility: hidden !important; }'
    document.head.append(style)
  } else if (!on && style) style.remove()
  return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))
}

/** Big blocks in the other theme's color: black slabs on Light, white slabs on Dark and Void (pictures aside). */
export function themeLeakScan() {
  const theme = document.documentElement.dataset.exoTheme ?? 'void'
  const parse = (c) => { const m = c.match(/rgba?\(([^)]+)\)/); if (!m) return null; const p = m[1].split(/[ ,/]+/).filter(Boolean).map(Number); return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1] }
  const area = innerWidth * innerHeight
  const out = []
  for (const el of document.querySelectorAll('body *')) {
    const r = el.getBoundingClientRect()
    const w = Math.min(r.right, innerWidth) - Math.max(r.left, 0), h = Math.min(r.bottom, innerHeight) - Math.max(r.top, 0)
    if (w <= 0 || h <= 0 || w * h < area * 0.015) continue
    const cs = getComputedStyle(el)
    if (cs.visibility === 'hidden' || +cs.opacity < 0.5) continue
    const c = parse(cs.backgroundColor)
    if (!c || c[3] < 0.9) continue
    if (el.querySelector('img, video, canvas, iframe')) {
      const media = [...el.querySelectorAll('img, video, canvas, iframe')].some(m => { const mr = m.getBoundingClientRect(); return mr.width * mr.height >= w * h * 0.4 })
      if (media) continue
    }
    const l = (0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]) / 255
    const wrong = theme === 'light' ? l < 0.22 : l > 0.78
    if (wrong) out.push(`${el.tagName.toLowerCase()}.${[...el.classList].slice(0, 2).join('.')} rgb(${c.slice(0, 3).join(',')}) ${Math.round(w)}x${Math.round(h)}`)
  }
  return [...new Set(out)].slice(0, 12)
}

/** Pictures in view that failed to load. */
export function brokenImages() {
  return [...document.images].filter(img => {
    const r = img.getBoundingClientRect()
    return r.width > 4 && r.height > 4 && r.bottom > 0 && r.top < innerHeight && img.complete && img.naturalWidth === 0 && img.currentSrc
  }).map(img => (img.alt || img.currentSrc).slice(0, 80)).slice(0, 12)
}

/** Controls a screen reader can't name, and pictures without alt text. */
export function a11yScan() {
  const named = (el) => {
    if ((el.getAttribute('aria-label') || '').trim() || (el.getAttribute('title') || '').trim()) return true
    const by = el.getAttribute('aria-labelledby')
    if (by && by.split(/\s+/).some(id => document.getElementById(id)?.textContent.trim())) return true
    if ((el.innerText || '').trim()) return true
    if (el.querySelector('img[alt]:not([alt=""])')) return true
    if (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`)) return true
    if (el.closest('label')?.textContent.trim()) return true
    if ((el.getAttribute('placeholder') || '').trim()) return true
    return false
  }
  const out = { unnamed: [], noAlt: 0 }
  for (const el of document.querySelectorAll('button, a[href], [role="button"], [role="tab"], [role="menuitem"], [role="switch"], [role="checkbox"], input:not([type=hidden]), select, textarea')) {
    const r = el.getBoundingClientRect()
    if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.top > innerHeight || el.closest('[aria-hidden="true"], [inert]')) continue
    if (!named(el)) out.unnamed.push(`${el.tagName.toLowerCase()}.${[...el.classList].slice(0, 2).join('.')}`)
  }
  out.noAlt = [...document.images].filter(img => !img.hasAttribute('alt') && !img.closest('[aria-hidden="true"]')).length
  out.unnamed = [...new Set(out.unnamed)].slice(0, 15)
  return out
}

/** Starts recording each frame's time (requestAnimationFrame), for frameStop. */
export function frameStart() {
  window.__exoFrames = []
  window.__exoFramesOn = true
  const loop = (t) => { if (!window.__exoFramesOn) return; window.__exoFrames.push(t); requestAnimationFrame(loop) }
  requestAnimationFrame(loop)
  return true
}

/** Stops recording; returns the gaps between frames (ms). */
export function frameStop() {
  window.__exoFramesOn = false
  const t = window.__exoFrames ?? []
  const gaps = []
  for (let i = 1; i < t.length; i++) gaps.push(+(t[i] - t[i - 1]).toFixed(2))
  return gaps
}

/** Animations running now: their name, duration and whether they loop. */
export function animations() {
  return document.getAnimations().map(a => {
    const t = a.effect?.getComputedTiming() ?? {}
    return { name: a.animationName || a.transitionProperty || a.id || a.constructor.name, duration: Math.round(Number(t.duration) || 0), loops: t.iterations === Infinity, state: a.playState, target: a.effect?.target?.className?.toString().slice(0, 40) ?? '' }
  })
}
