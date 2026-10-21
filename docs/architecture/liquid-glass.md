# Liquid Glass

How Exo's glass works and why: what Apple's Liquid Glass is, what Exo takes from it, the arithmetic that keeps every word legible, and what Chromium (WebView2) does with backdrop filters. The look is `DESIGN.md › Glass`; the code is `ui/index.html` (the shared filters), `ui/src/styles/glass.css` (the material), `ui/src/lib/glass.ts` (the lens) and `tools/e2e/exo-e2e.mjs › glass.legible` (the proof on the installed app).

## Apple's rules, and Exo's answer to each

From Apple's own sources (developer.apple.com/documentation/technologyoverviews/liquid-glass and its pages, HIG Materials, Color, Toolbars, Scroll views and Accessibility, WWDC25 sessions 219, 356, 323, 284 and 310, WWDC26's 2026 refinements). Apple publishes no blur radii, curves, spring constants or tint amounts; the numbers below are Exo's.

| Apple | Exo |
| --- | --- |
| Glass is the functional layer that floats over content; never in the content layer; sparingly. | Exo goes further, by the owner's choice: only backgrounds are AMOLED black. On a black page a black surface reads as the page itself, so every panel, sheet, card, menu, popover, toast and control is glass (`glass.css`): over black it stays black, never a flat gray, and over art it takes the art's color. |
| Never glass on glass: glass can't sample glass. | The same holds in Chromium: an element with a backdrop filter is a backdrop root, so glass inside it sees nothing. So a pane (a panel, a sheet, the game sheet) keeps its glass on a layer under its content (`::before`, or `.exo-sheet-glass`), and what sits on it is glass that sees the page; a control inside glass that blurs on its own element (a menu, a dialog, a Bar capsule) is a lift of that glass instead. Exo's menus and Discord's submenus are popovers in the top layer, which sample the page. |
| Regular glass "adjusts luminosity" so text stays legible over anything; small elements adapt constantly, flipping between light and dark with what is under them; large ones stay put and become more opaque. | The band (below): small glass 5–33% under white words. The Bar's capsules and Music's player flip to light glass (70–96%, dark words) over bright content, read from the art under them (`useGlassTone` in `glass.ts`). Large glass is always dark, 3–24%, blurs more and casts the deeper shadow. |
| Clear glass only over media; over bright content, consider a 35% dimming layer. | Not used: every glass is the regular kind, the one that keeps words legible by itself. |
| Lensing is how glass defines itself; refraction bends the edge. | Small glass has a lens: a squircle bezel, Snell refraction (n 1.5), the rim nearly sharp and bent over a soft middle. |
| Specular highlights; 2026 adds a darkened edge and brighter speculars. | One even hairline all around, by the owner's choice: on AMOLED black a lit top and a shaded foot read as an edge missing, not as depth. |
| Glass illuminates from within under a touch, the glow spreading. | A press lights the glass where it lands (`--glass-glow`, placed by one pointerdown listener) and fades as it lets go. |
| Tint only for meaning (the primary action). | No tint on glass. The primary stays the solid white control. |
| Vibrant text on glass. | Secondary words on small glass are `#dbdbdb`, on large `#bdbdbd`, on light glass `#383838`; hover and current fills are lifts of the glass (white alpha, black alpha on light glass), not grays that would turn into dark holes over bright art. |
| Content scrolls under bars; the scroll edge effect dims or blurs it (over dark content it dims), stronger since 2026 where text sits near the bars. | The Library runs under the Bar and stays sharp: no scroll edge, by the owner's choice. The band keeps every word on the Bar legible without one. |
| Concentric corners; capsules for standout controls. | Unchanged: capsules in the Bar, `--exo-row-radius` inside panels. |
| Reduce Transparency: frostier, hiding more of what is behind. Increase Contrast: black or white with a contrasting border. Reduce Motion: no elastic effects. 26.1 adds Clear / Tinted; the 27 releases (fall 2026) a slider from ultra clear to fully tinted. | Settings › Glass › Solid draws no backdrop. Windows' Transparency effects switch (`prefers-reduced-transparency`) is not followed: it is one switch for the whole desktop, mostly turned off for speed, and Exo's own Solid serves anyone who needs glass gone; `prefers-contrast: more` is black with a white border and full ink; forced colors are Canvas and CanvasText; Reduce motion stops the press light's fade, and glass appears and leaves at once instead of fading (Apple: avoid animating into and out of a blur). Settings › General › Glass: Clear, Tinted, Solid; nothing else switches it. |
| Materialize: glass appears by ramping its lensing, not its opacity. | Not done. Exo's glass filters are SVG references, which can't interpolate; menus keep their own short open. |

## Legibility by arithmetic

Text on glass is only as legible as the brightest pixel that can end up under it. The legibility research (NN/g's "Liquid Glass Is Cracked", its glassmorphism and text-over-image studies, Apple's own beta-to-26.1 retreat, WCAG 2.x) agrees that text over unpredictable art must be designed for the worst background (NN/g also asks for more blur over busy art, which Exo's large glass has). Blur removes texture but not brightness, so Exo caps the brightness: contrast is guaranteed, not hoped for.

Glass presses the blurred, saturated backdrop into a band: each channel `v` becomes `intercept + slope × v`. With white ink, the measured worst cases (a pure white, yellow, cyan, sky and black-and-white stripe backdrop, the brightest pixel under where words sit, measured as under Seeing it) were:

| Glass | Band | Words | Worst pixel |
| --- | --- | --- | --- |
| Small, Clear, dark | 0–28% | white / `#dbdbdb` | at least 6.8:1 / 4.9:1 (measured at 5–33%) |
| Small, Clear, light (Bar, Music's player) | 70–96% | `#0a0a0a` / `#383838` | 9.4:1 / 5.6:1 (computed at the band's edge) |
| Large, Clear | 0–21% | white / `#bdbdbd` | at least 9.6:1 / 5.1:1 (measured at 3–24%) |
| Small, Tinted, dark | 10–22% | white / `#dbdbdb` | 11.7:1 / 8.4:1 (computed at the band's edge) |
| Small, Tinted, light | 84–94% | `#0a0a0a` / `#383838` | 13.6:1 / 8.1:1 (computed) |
| Large, Tinted | 7–14% | white / `#bdbdbd` | 15.6:1 / 8.3:1 (computed) |
| Solid | none (`#121212`) | white | 18.7:1 |

The `glass.legible` threshold is 4.5:1. Each band's ceiling is the highest that keeps the secondary words at 4.5:1 where they matter least well, on a hovered row (whose secondary words then go full ink). Over pure black Clear glass keeps no floor: it is black, never a flat gray (a 6% floor, then a 3% one, read as gray panels on the black page), its hairline draws it, and over art it carries its color. Tinted keeps its floor: frosted is what it is for.

For comparison, 3.10's translucent glass (a 50% dark tint over `brightness(0.85)`) computes to 4.1:1 over white, and measured 1.35:1 at its edges, where the blur let raw white through (below).

## Regular glass on black

Apple's regular glass is the default material: it adapts, and that is what keeps it legible. On a black page the risk runs the other way from iOS: glass over black reads as nothing, and glass over a bright cover reads as a bright hole under white words. Exo's answer:

- **Dark small glass** keeps no floor (over black it is black, drawn by its hairline) and a ceiling of 28%, under the 33% that keeps the brighter secondary `#dbdbdb` at 4.5:1 at the worst pixel.
- **Light small glass.** The Bar's capsules and Music's player are the glass that floats over everything, so they flip as Apple's small glass does: `useGlassTone` (`ui/src/lib/glass.ts`) reads the art under the capsule (only images Exo is allowed to read: covers through the host's `covers` mapping, thumbnails first), reduces it to a 24×24 luminance grid, and samples the brightness at points under the capsule, through any dimming layer in between. Above 0.5 it turns light (band 70–96%, words `#0a0a0a`, secondary `#383838`, rim `--exo-glass-rim-light`); below 0.32 it turns dark again, and it changes at most once every 250ms, so a scroll past one bright cover never flickers. Every capsule in a group (the whole Bar) takes one tone. Solid glass never turns light.
- **Large glass** (panels, sheets, menus, popovers, Ctrl+K) never flips: it is the thicker pane, dark (0–21%), and its words are always white, even when it opens from a light capsule.

## Liquid Glass elsewhere on Windows

Windhawk's Windows 11 Taskbar Styler (and every "Liquid Glass" theme for it) restyles the taskbar's XAML through XAML diagnostics and paints with its own `WindhawkBlur` brush: backdrop, Gaussian blur, saturation, an optional noise tile and a flat tint, with a capsule shape and 1 to 2px gradient borders for the specular rim. Nothing is refracted: Windows' composition layer has no displacement (`DisplacementMapEffect` is not supported in composition, and composition effect brushes take no custom shaders). The projects that do refract on Windows capture the screen and draw a lens in a separate window, which Exo will not do. So the lens has to live in the web layer, where Exo has it, and the host's part is signals (Windows' more contrast and forced colors). The themes' tints (7–22% black or white) would leave white words at about 1.2 to 1.7:1 over white art; Exo's band is what keeps them legible.

## Chromium, as found

Each of these was seen on screen in Chromium's own renderer before the code was written around it.

1. **`backdrop-filter: url(#svg)` works** and chains with other filters in either order; WebView2 is Chromium. Only Chromium implements it.
2. **An SVG backdrop filter is laid out against the element's ink overflow.** An outer `box-shadow` (on the element or on its pseudo-element), an outer outline or an overflowing child shifts the filter's box, so a lens map lands offset. Lens glass therefore has no outer shadow and its focus ring sits inside (`outline-offset: -2px`). The lens filter's region is generous and its result is laid over the unbent backdrop, so a shifted map never exposes raw art.
3. **A blur near the glass's edge pulls in transparency**, and whatever is transparent shows the raw backdrop: over white art, a bright fringe right where words can sit. A bleed layer clipped back to the shape doesn't help (the crop follows the clip). The cure is in the filter: after the blur, alpha is set to 1, so each fringe pixel keeps the average color of what was actually sampled (a normalized blur), and the band applies to it like everything else.
4. **A missing `<filter>` voids the whole chain**, blur and all, leaving the glass see-through. The shared filters are in `ui/index.html` from the first frame; a lens is named in `--exo-lens` only after its filter is in the page, and that name is removed before the filter is.
5. **A child's backdrop doesn't include its parent's filtered backdrop.** Splitting tone and blur between an element and its pseudo-element sees nothing; both live in one chain.
6. **Backdrop roots:** `filter`, `opacity` below 1, `mask`, `clip-path`, `backdrop-filter`, `mix-blend-mode`, `view-transition-name` or `will-change` on an ancestor limits what glass can see to that ancestor's content. The Library pane's edge fade is a mask, so Continue's More sees the art inside the pane, which is all it needs; the Bar's capsules sit outside every room and see the whole page.
7. **Unlayered rules beat Tailwind's utilities**, whatever their specificity. Glass's `position: relative` lives in `@layer base`, or Ctrl+K (positioned by utilities) would lose its place.
8. **A blur layer over positioned text over transformed art** can leave that text out of the blur (Continue's titles stayed sharp under a blur band). Exo has no scroll edge now; content under the Bar stays sharp.
9. **A finished animation that keeps filling is a backdrop root.** `animation-fill-mode: both` (or `forwards`) on an opacity entrance keeps a compositor effect after it ends, so glass inside the room or page it faded in saw only that element's own content, never the room behind. Exo's entrance fades hold only before they start (`backwards`), which looks the same, and a glass sheet settles by its size alone while its glass and words fade. A fade that is running is a backdrop root too, so a game page (`.exo-enter`) and a room never fade themselves: a black `::after` over them fades away instead, and the glass on them reads the art from the first frame.
10. **A view transition blinds glass.** Mid-move Chromium draws the page from snapshots, and glass in a snapshot sees nothing behind it: it drew clear and popped back as the move ended. While `data-exo-vt` is on the root, every glass surface and pane draws a still tint in its own band (glass.css) and blurs again after.
11. **The page's root background is not in the backdrop.** Over it the backdrop is transparent: CSS filter functions leave it transparent (no floor at all), and an SVG filter that sets alpha whole turns any faint hairline over that transparency into a white band (it unpremultiplies a near-zero alpha). Rooms paint their own black, and panes and controls use CSS filter functions, with the band written as `brightness(b) contrast(c)` (`c = 1 − 2 × intercept`, `b = slope / c`).
12. **A pane's glass is a layer under its content**, a `::before` at `z-index: -1` inside `isolation: isolate`, so it is not an ancestor of what sits on the pane. Its edge is the pane's own 1px border.
13. **SVG backdrop filters cost more than CSS functions.** About ten small glass controls with the SVG pair (`exo-glass-soft` then `exo-glass-tone`) over a scrolling list dropped 2 to 3% of frames at 165 Hz; the same band in CSS functions dropped none. The Bar's capsules and Music's player keep the SVG lens, the one thing CSS can't do.

Cost: each glass element is a render pass that re-filters when what is behind it changes. Glass is on every surface now, so the repeated controls of a scrolling list (a song's or a library row's More, a friend's Message and More, a reaction) are a lift of the page instead: on black they look the same, and forty backdrops in one list cost frames. Scrolling a long song list with the rest measures as solid glass does (no frame over 12ms in 400 at 165 Hz). A still glass costs nothing; it stays the same while a game runs or Exo is in the background, so it never switches under you.

## Seeing it

`glass.legible` in the E2E run checks the shared filters are present and that every Bar capsule's filter chain resolves (or is `none` when glass is solid), slides a pure white sheet under the Bar and checks the Bar turns light, then measures the worst pixel under the words of dark, light and large glass against their own ink (4.5:1 or more). In a browser build of the UI (`npm run dev`, the mock host), the same method works with Playwright: give the mock games art, open each room, and read the brightest pixel inside each glass with its words hidden. Glass is solid only with Settings › Glass › Solid or Windows' more contrast and forced colors, which is easy to mistake for weak glass.
