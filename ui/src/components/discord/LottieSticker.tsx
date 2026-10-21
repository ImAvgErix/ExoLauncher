import { useEffect, useRef, useState } from 'react'
import type { AnimationItem } from 'lottie-web/build/player/lottie_svg'
import { host } from '../../lib/host'
import { performanceMode } from '../../lib/discord'
import { useAccountSettings } from '../../lib/discordPrefs'
import { usePageActive } from '../../lib/pageActivity'
import { shouldReduceMotion } from '../../lib/motionPreference'

// The player loads the first time a sticker comes near the screen, never at startup.
const player = () => import('lottie-web/build/player/lottie_svg').then(module => module.default)

// Each sticker's animation, asked for once per run (the host keeps a copy too).
const kept = new Map<string, Promise<unknown>>()
function animationOf(id: string) {
  let answer = kept.get(id)
  if (!answer) {
    answer = host.discordStickerData(id).then(result => result.ok && result.json ? JSON.parse(result.json) as unknown : null).catch(() => null)
    kept.set(id, answer)
  }
  return answer
}

/**
 * An animated (Lottie) sticker, drawn as Discord draws it: loaded once it is near the screen, playing
 * while it is in view and Exo is active. Play animated stickers (Discord settings > Accessibility) on
 * interaction, Performance mode or reduced motion play it only under the pointer; never keeps it still.
 * Discord's own sticker packs are mostly these.
 */
export function LottieSticker({ id, size, label }: { id: string; size: number; label?: string }) {
  const box = useRef<HTMLSpanElement>(null)
  const animation = useRef<AnimationItem | null>(null)
  const [near, setNear] = useState(false)
  const [ready, setReady] = useState(false)
  const [hover, setHover] = useState(false)

  useEffect(() => {
    const element = box.current
    if (!element) return
    const watch = new IntersectionObserver(([entry]) => setNear(entry.isIntersecting), { rootMargin: '200px 0px' })
    watch.observe(element)
    return () => watch.disconnect()
  }, [])

  useEffect(() => {
    if (!near || animation.current) return
    let live = true
    void Promise.all([animationOf(id), player()]).then(([data, lottie]) => {
      if (!live || !data || !box.current || animation.current) return
      // lottie changes the data it is given; every sticker on screen gets its own copy.
      animation.current = lottie.loadAnimation({ container: box.current, renderer: 'svg', loop: true, autoplay: false, animationData: structuredClone(data) })
      setReady(true)
    })
    return () => { live = false }
  }, [near, id])

  useEffect(() => () => { animation.current?.destroy(); animation.current = null }, [])

  const mode = useAccountSettings().animateStickers
  const active = usePageActive()
  const playing = ready && near && active && mode !== 2 && (hover || (mode === 0 && !performanceMode() && !shouldReduceMotion()))
  useEffect(() => {
    const current = animation.current
    if (!current) return
    if (playing) current.play()
    else current.pause()
  }, [playing])

  return (
    <span ref={box} className="exo-dc-lottie" role="img" aria-label={label} style={{ width: size, height: size }}
      onPointerEnter={() => setHover(true)} onPointerLeave={() => setHover(false)} />
  )
}
