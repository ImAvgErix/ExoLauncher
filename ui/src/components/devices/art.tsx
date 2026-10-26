import type { CSSProperties, ReactNode } from 'react'

/**
 * Exo's own drawings of what you plug in (DESIGN.md › Devices), for a device with no product render: flat, in the
 * Appearance's ink, a body lifted from the page with one lit rim, its parts a step lighter. Drawn here rather than
 * borrowed, so every Appearance and every model of a kind looks like one family.
 */
export type ArtKind = 'mouse' | 'keyboard' | 'controller' | 'iem' | 'headphones' | 'headset' | 'microphone' | 'speakers'

export function DeviceArt({ kind, model, layout, color, className }: {
  kind: ArtKind
  model?: string | null
  /** A keyboard's layout as it reports it: a split spacebar draws split. */
  layout?: string | null
  /** A controller's body color as it reports it ("Midnight Black"). */
  color?: string | null
  className?: string
}) {
  const art = kind === 'keyboard' ? <Keyboard size={model} split={layout === 'ansi-split' || layout === 'iso-split'} />
    : kind === 'controller' ? <Controller edge={model === 'dualsense-edge'} />
    : kind === 'iem' ? <Iem />
    : kind === 'headphones' ? <Headphones />
    : kind === 'headset' ? <Headphones mic />
    : kind === 'microphone' ? <Microphone />
    : kind === 'speakers' ? <Speakers />
    : <Mouse />
  const body = color ? BODY[color] : undefined
  return (
    <span className={['exo-device-art', body?.dark && 'is-dark', className].filter(Boolean).join(' ')} aria-hidden="true"
      style={body ? { '--exo-art-body': body.fill } as CSSProperties : undefined}>{art}</span>
  )
}

// A controller's shell in its own color (its color is what it is, as a cover's art is); white and black follow the
// Appearance's ink instead, so a white pad reads light and a black one reads as black glass in every Appearance.
const BODY: Record<string, { fill?: string; dark?: boolean }> = {
  'White': { fill: 'rgb(var(--exo-ink-rgb) / 0.78)' },
  'Midnight Black': { dark: true },
  'Cosmic Red': { fill: '#8e1f2d' }, 'Nova Pink': { fill: '#d6a2b6' }, 'Galactic Purple': { fill: '#4c2d70' },
  'Starlight Blue': { fill: '#8db3da' }, 'Grey Camouflage': { fill: '#6c7077' }, 'Volcanic Red': { fill: '#7b2020' },
  'Sterling Silver': { fill: '#a9aeb5' }, 'Cobalt Blue': { fill: '#25417c' }, 'Chroma Teal': { fill: '#2c7e7e' },
  'Chroma Indigo': { fill: '#3c3b8d' }, 'Chroma Pearl': { fill: '#d8d3cd' },
}

function Svg({ box, children }: { box: string; children: ReactNode }) {
  return <svg viewBox={box} fill="none" xmlns="http://www.w3.org/2000/svg">{children}</svg>
}

// A 60% board is 15 units wide in five rows; larger boards add a column of units each.
const ROWS_60 = [
  [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2],
  [1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5],
  [1.75, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.25],
  [2.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.75],
  [1.25, 1.25, 1.25, 6.25, 1.25, 1.25, 1.25, 1.25],
]
// A split spacebar (Wooting's split bar): the space in three, the same width in all.
const SPLIT_BOTTOM = [1.25, 1.25, 1.25, 2.25, 1.25, 2.75, 1.25, 1.25, 1.25, 1.25]

function Keyboard({ size, split }: { size?: string | null; split: boolean }) {
  const extra = size === '65' ? 1 : size === '75' ? 1 : size === 'tkl' ? 3.25 : size === 'full' ? 7.5 : 0
  const units = 15 + extra
  const unit = 18
  const gap = 2.4
  const width = units * unit + 24
  const keys: ReactNode[] = []
  ;(split ? [...ROWS_60.slice(0, 4), SPLIT_BOTTOM] : ROWS_60).forEach((row, r) => {
    let x = 12
    row.forEach((w, k) => {
      const wide = w > 1.5
      keys.push(<rect key={`${r}-${k}`} className={wide ? 'is-mod' : 'is-key'} x={x + gap / 2} y={12 + r * unit + gap / 2}
        width={w * unit - gap} height={unit - gap} rx="3.2" />)
      x += w * unit
    })
    // Columns past a 60%: a block of plain keys beside the main rows.
    for (let c = 0; c < Math.floor(extra); c += 1)
      keys.push(<rect key={`${r}-x${c}`} className="is-key" x={12 + (15 + 0.25 + c) * unit + gap / 2} y={12 + r * unit + gap / 2}
        width={unit - gap} height={unit - gap} rx="3.2" />)
  })
  return (
    <Svg box={`0 0 ${width} ${5 * unit + 24}`}>
      <rect className="is-body" x="1" y="1" width={width - 2} height={5 * unit + 22} rx="12" />
      {keys}
    </Svg>
  )
}

function Controller({ edge }: { edge: boolean }) {
  return (
    <Svg box="0 0 300 196">
      <path className="is-body" d="M70 28C110 17 190 17 230 28C262 36 286 70 292 120C297 158 286 186 262 186C240 186 226 170 214 150C205 135 195 129 180 129H120C105 129 95 135 86 150C74 170 60 186 38 186C14 186 3 158 8 120C14 70 38 36 70 28Z" />
      <rect className="is-part" x="104" y="25" width="92" height="52" rx="9" />
      <path className="is-light" d="M107 27.5h86" />
      <circle className="is-part" cx="114" cy="104" r="17" />
      <circle className="is-key" cx="114" cy="104" r="10" />
      <circle className="is-part" cx="186" cy="104" r="17" />
      <circle className="is-key" cx="186" cy="104" r="10" />
      <rect className="is-key" x="62" y="66" width="22" height="9" rx="2.5" />
      <rect className="is-key" x="68.5" y="59.5" width="9" height="22" rx="2.5" />
      <circle className="is-key" cx="230" cy="57" r="6.5" />
      <circle className="is-key" cx="245" cy="71" r="6.5" />
      <circle className="is-key" cx="230" cy="85" r="6.5" />
      <circle className="is-key" cx="215" cy="71" r="6.5" />
      <circle className="is-part" cx="150" cy="107" r="5" />
      <rect className="is-part" x="144" y="116" width="12" height="4" rx="2" />
      <rect className="is-part" x="88" y="36" width="6" height="12" rx="3" />
      <rect className="is-part" x="206" y="36" width="6" height="12" rx="3" />
      {/* The Edge's function buttons under its sticks. */}
      {edge ? <><rect className="is-key" x="104" y="125" width="14" height="5" rx="2.5" /><rect className="is-key" x="182" y="125" width="14" height="5" rx="2.5" /></> : null}
    </Svg>
  )
}

function Iem() {
  return (
    <Svg box="0 0 240 210">
      <path className="is-wire" d="M78 92C74 130 96 150 120 160M162 92C166 130 144 150 120 160M120 160V198" />
      <circle className="is-part" cx="120" cy="160" r="4.5" />
      <rect className="is-part" x="115" y="192" width="10" height="16" rx="3" />
      <path className="is-body" d="M44 30C66 10 104 18 108 46C111 68 98 92 76 96C52 100 30 84 28 62C27 50 33 39 44 30Z" />
      <path className="is-part" d="M50 44C62 34 84 38 88 52C91 64 82 76 70 77C58 78 48 70 46 60C45 54 46 48 50 44Z" />
      <path className="is-key" d="M98 30L112 18C116 15 121 19 118 23L106 37Z" />
      <path className="is-body" d="M196 30C174 10 136 18 132 46C129 68 142 92 164 96C188 100 210 84 212 62C213 50 207 39 196 30Z" />
      <path className="is-part" d="M190 44C178 34 156 38 152 52C149 64 158 76 170 77C182 78 192 70 194 60C195 54 194 48 190 44Z" />
      <path className="is-key" d="M142 30L128 18C124 15 119 19 122 23L134 37Z" />
    </Svg>
  )
}

function Headphones({ mic = false }: { mic?: boolean }) {
  return (
    <Svg box="0 0 220 210">
      <path className="is-wire is-band" d="M42 120V100C42 58 72 26 110 26C148 26 178 58 178 100V120" />
      <rect className="is-body" x="22" y="104" width="44" height="78" rx="18" />
      <rect className="is-part" x="32" y="116" width="24" height="54" rx="11" />
      <rect className="is-body" x="154" y="104" width="44" height="78" rx="18" />
      <rect className="is-part" x="164" y="116" width="24" height="54" rx="11" />
      {mic ? <><path className="is-wire" d="M36 176C40 196 70 202 92 196" /><circle className="is-key" cx="96" cy="195" r="6" /></> : null}
    </Svg>
  )
}

function Microphone() {
  return (
    <Svg box="0 0 160 220">
      <rect className="is-body" x="48" y="14" width="64" height="110" rx="32" />
      <path className="is-line" d="M60 50H100M60 64H100M60 78H100" />
      <path className="is-wire" d="M34 96C34 134 54 152 80 152C106 152 126 134 126 96M80 152V196" />
      <rect className="is-body" x="44" y="194" width="72" height="12" rx="6" />
    </Svg>
  )
}

function Speakers() {
  return (
    <Svg box="0 0 260 200">
      {[16, 152].map(x => (
        <g key={x}>
          <rect className="is-body" x={x} y="20" width="92" height="164" rx="14" />
          <circle className="is-part" cx={x + 46} cy="62" r="18" />
          <circle className="is-part" cx={x + 46} cy="128" r="30" />
          <circle className="is-key" cx={x + 46} cy="128" r="12" />
        </g>
      ))}
    </Svg>
  )
}

function Mouse() {
  return (
    <Svg box="0 0 140 220">
      <path className="is-body" d="M70 8C108 8 128 40 128 92V150C128 190 104 212 70 212C36 212 12 190 12 150V92C12 40 32 8 70 8Z" />
      <path className="is-line" d="M70 10V84M14 84H126" />
      <rect className="is-key" x="64" y="30" width="12" height="28" rx="6" />
    </Svg>
  )
}
