// Pictures compared the way a person would notice: the screen against the page's own picture, cell by cell on a
// coarse grid (blur and scaling differences vanish; a missing quarter of the page doesn't).
import { readFile } from 'node:fs/promises'
import { pngPixels } from '../cdp.mjs'

export async function readPng(path) {
  return pngPixels(await readFile(path))
}

/** Mean luminance (0..255) of each cell of a cols x rows grid laid over the picture. */
export function grid(img, cols = 96, rows = 54) {
  const out = new Float64Array(cols * rows), count = new Uint32Array(cols * rows)
  const step = Math.max(1, Math.floor(Math.min(img.width / cols, img.height / rows) / 4))
  for (let y = 0; y < img.height; y += step) {
    const row = Math.min(rows - 1, Math.floor(y * rows / img.height))
    for (let x = 0; x < img.width; x += step) {
      const col = Math.min(cols - 1, Math.floor(x * cols / img.width))
      const i = (y * img.width + x) * 4
      out[row * cols + col] += 0.2126 * img.data[i] + 0.7152 * img.data[i + 1] + 0.0722 * img.data[i + 2]
      count[row * cols + col]++
    }
  }
  for (let i = 0; i < out.length; i++) out[i] = count[i] ? out[i] / count[i] : 0
  return { cols, rows, cells: out }
}

/**
 * How far two pictures of the same view differ: the mean cell difference over the whole and over each edge band
 * (a fifth of the width or height), and the share of cells off by more than 40 levels.
 */
export function compare(a, b, cols = 96, rows = 54) {
  const ga = grid(a, cols, rows), gb = grid(b, cols, rows)
  const band = (pick) => {
    let sum = 0, n = 0
    for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) if (pick(c, r)) { sum += Math.abs(ga.cells[r * cols + c] - gb.cells[r * cols + c]); n++ }
    return n ? +(sum / n).toFixed(1) : 0
  }
  let off = 0
  for (let i = 0; i < ga.cells.length; i++) if (Math.abs(ga.cells[i] - gb.cells[i]) > 40) off++
  return {
    mean: band(() => true),
    right: band(c => c >= cols * 0.8), bottom: band((c, r) => r >= rows * 0.8),
    left: band(c => c < cols * 0.2), top: band((c, r) => r < rows * 0.2),
    offShare: +(off / ga.cells.length).toFixed(3),
  }
}

/** Share of the picture (0..1) within `tolerance` of one color: how much of a view is empty page. */
export function shareNear(img, [r, g, b], tolerance = 6) {
  let near = 0, n = 0
  for (let i = 0; i < img.data.length; i += 4 * 7) {
    n++
    if (Math.abs(img.data[i] - r) <= tolerance && Math.abs(img.data[i + 1] - g) <= tolerance && Math.abs(img.data[i + 2] - b) <= tolerance) near++
  }
  return n ? near / n : 0
}

/** The part of a picture inside a rectangle given as fractions of its size (0..1). */
export function cropFraction(img, fx, fy, fw, fh) {
  const x0 = Math.round(img.width * fx), y0 = Math.round(img.height * fy)
  const w = Math.max(1, Math.round(img.width * fw)), h = Math.max(1, Math.round(img.height * fh))
  const data = Buffer.alloc(w * h * 4)
  for (let y = 0; y < h; y++) img.data.copy(data, y * w * 4, ((y0 + y) * img.width + x0) * 4, ((y0 + y) * img.width + x0 + w) * 4)
  return { width: w, height: h, data }
}
