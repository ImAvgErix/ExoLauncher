/** Split node_modules without putting packages that merely have "react" in their path in the React chunk. */
export function vendorChunk(id: string): string | undefined {
  const path = id.replaceAll('\\', '/')
  if (!path.includes('/node_modules/')) return undefined
  // The trailer players load on demand (TrailerPlayer's dynamic import). Kept
  // out of vendor, which every page preloads, they stay out of startup.
  if (path.includes('/node_modules/dashjs/') || path.includes('/node_modules/hls.js/')) return undefined
  // Discord's animated stickers load their player (lottie) the first time one is near the screen.
  if (path.includes('/node_modules/lottie-web/')) return undefined
  // scheduler is react-dom's only dependency; splitting it into vendor makes
  // vendor -> react-dom -> vendor a cycle (Rollup warns and can misorder init).
  if (path.includes('/node_modules/react-dom/') || path.includes('/node_modules/scheduler/')) return 'react-dom'
  if (path.includes('/node_modules/react/')) return 'react'
  return 'vendor'
}
