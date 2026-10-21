import { useMemo } from 'react'
import qrcode from 'qrcode-generator'

/** A QR code for a phone to scan (Discord's sign-in, a Spotify Jam's invite): black on white with its quiet zone. */
export function Qr({ text, label, className }: { text: string; label: string; className?: string }) {
  const cells = useMemo(() => {
    const code = qrcode(0, 'M')
    code.addData(text)
    code.make()
    const count = code.getModuleCount()
    const path: string[] = []
    for (let row = 0; row < count; row++)
      for (let col = 0; col < count; col++)
        if (code.isDark(row, col)) path.push(`M${col + 4} ${row + 4}h1v1h-1z`)
    return { size: count + 8, path: path.join('') }
  }, [text])
  return (
    <svg className={className} viewBox={`0 0 ${cells.size} ${cells.size}`} role="img" aria-label={label} shapeRendering="crispEdges">
      <rect width={cells.size} height={cells.size} fill="#fff" />
      <path d={cells.path} fill="#000" />
    </svg>
  )
}
