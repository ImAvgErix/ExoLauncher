// Contrast as it shows: one screenshot with every word hidden is what is behind the words (glass, art and all), and
// each label's own color is measured against those pixels. The worst tenth of a label's background counts, so a
// label over busy art is judged where it is hardest to read.
import { pngPixels } from '../cdp.mjs'
import { hideText, textBoxes } from './page.mjs'

const lin = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }
const lum = (r, g, b) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)

export async function pixelContrast(shell, limit = 600) {
  // The words' places, the picture, the places again: if anything moved meanwhile (a smooth scroll), again.
  let boxes, shot
  const view = await shell.eval('({ w: innerWidth, h: innerHeight })')
  for (let attempt = 0; attempt < 4; attempt++) {
    boxes = await shell.call(textBoxes, limit)
    await shell.call(hideText, true)
    try { shot = await shell.s.send('Page.captureScreenshot', { format: 'png' }) } finally { await shell.call(hideText, false) }
    const again = await shell.call(textBoxes, limit)
    const moved = boxes.length !== again.length || boxes.some((b, i) => Math.abs(b.x - again[i].x) > 2 || Math.abs(b.y - again[i].y) > 2)
    if (!moved) break
    await new Promise(r => setTimeout(r, 400))
  }
  const img = pngPixels(Buffer.from(shot.data, 'base64'))
  const sx = img.width / view.w, sy = img.height / view.h
  const results = []
  for (const b of boxes) {
    const ratios = []
    const x0 = Math.max(0, Math.floor(b.x * sx)), x1 = Math.min(img.width - 1, Math.ceil((b.x + b.w) * sx))
    const y0 = Math.max(0, Math.floor(b.y * sy)), y1 = Math.min(img.height - 1, Math.ceil((b.y + b.h) * sy))
    const stepX = Math.max(1, Math.floor((x1 - x0) / 24)), stepY = Math.max(1, Math.floor((y1 - y0) / 6))
    const [cr, cg, cb, ca] = b.color
    for (let y = y0; y <= y1; y += stepY) for (let x = x0; x <= x1; x += stepX) {
      const i = (y * img.width + x) * 4
      const br = img.data[i], bg = img.data[i + 1], bb = img.data[i + 2]
      const fr = cr * ca + br * (1 - ca), fg = cg * ca + bg * (1 - ca), fb = cb * ca + bb * (1 - ca)
      const L1 = lum(fr, fg, fb), L2 = lum(br, bg, bb)
      ratios.push((Math.max(L1, L2) + 0.05) / (Math.min(L1, L2) + 0.05))
    }
    if (!ratios.length) continue
    ratios.sort((a, b) => a - b)
    const p10 = ratios[Math.floor(ratios.length * 0.1)]
    const large = b.size >= 24 || (b.size >= 18.6 && b.weight >= 700)
    results.push({ text: b.text, cls: b.cls, ratio: +p10.toFixed(2), need: large ? 3 : 4.5, size: b.size, chosen: b.chosen })
  }
  const low = results.filter(r => r.ratio < r.need).sort((a, b) => a.ratio - b.ratio)
  const bad = low.filter(r => !r.chosen)
  return { checked: results.length, bad, unreadable: bad.filter(r => r.ratio < 3), chosenLow: low.filter(r => r.chosen) }
}
