# Liquid Glass

How Exo's glass works and why: what Apple's Liquid Glass is, what Exo takes from it, the arithmetic that keeps words legible, and what Chromium (WebView2) does with backdrop filters. The look is `DESIGN.md › Glass`; the code is `ui/src/tokens.css` (the material in each Appearance: `--glassy-*`, `--exo-control-glass`, `--exo-glass-*`), `ui/src/styles/glass.css` (the rules), `ui/index.html` (the shared SVG filter: the soft blur), `ui/src/lib/glass.ts` (the lens and the light or dark tone) and `tools/e2e` (the proof on the installed app: `appearance.glass-*` and the contrast measured in `rooms` and `appearance`).

The material is Glassy UI's (github.com/alexburlis/glassyui, MIT, `styles/liquid.css`): a frosted backdrop (blur, dim, contrast 0.9, saturation) under a thin neutral slab, a sheen at the top, a rim lit from the top left with a softer counter light bottom right, shade just inside it, a faint blue and pink split and a specular ring (`--glassy-rim`, `--glassy-ring`, `--glassy-sheen`), and a soft two-layer shadow. Light, Dark and Void share it; only the slab, the dim, the ink and the shadows change.

## Apple's rules, and Exo's answer to each

From Apple's own sources (developer.apple.com/documentation/technologyoverviews/liquid-glass and its pages, HIG Materials, Color, Toolbars, Scroll views and Accessibility, WWDC25 sessions 219, 356, 323, 284 and 310, WWDC26's 2026 refinements). Apple publishes no blur radii, curves, spring constants or tint amounts; the numbers below are Exo's.

| Apple | Exo |
| --- | --- |
| Glass is the functional layer that floats over content; never in the content layer; sparingly. | Exo goes further, by the owner's choice: the page is the Appearance's background (Void `#000`, Dark `#121212`, Light `#f5f5f5`), and every pane, sheet, menu, popover, toast and control on it is glass (`glass.css`); over art it takes the art's color. |
| Never glass on glass: glass can't sample glass. | The same holds in Chromium: an element with a backdrop filter is a backdrop root, so glass inside it sees nothing. So a pane (a room's side panel, System's panels, Discord's dock and member list, the game sheet) and a sheet (Settings, Discord's settings, Properties) keep their glass on a layer under their content (`::before`, or `.exo-sheet-glass`), and what sits on them is glass that sees the page; a control inside glass that blurs on its own element (a menu, a dialog, a Bar capsule) is a lift of that glass instead. Exo's menus and Discord's submenus are popovers in the top layer, which sample the page. |
| Regular glass "adjusts luminosity" so text stays legible over anything; small elements adapt constantly, flipping between light and dark with what is under them; large ones stay put and become more opaque. | Small glass dims what is behind it into a band under its words (below), so it needs no flip: Exo's glass keeps one tone over any art (the Bar's capsules and Music's player once flipped to light glass over bright content; it read as two controls and was removed). Large glass: a more opaque pane, more blur, the deeper shadow. |
| Clear glass only over media; over bright content, consider a 35% dimming layer. | Not used: every glass is the regular kind, the one that keeps words legible by itself. |
| Lensing is how glass defines itself; refraction bends the edge. | Small glass over content (the Bar's capsules, Music's player, Continue's More and title plate, a sheet's close button, a folder's front) has a lens (`useLens`): a squircle bezel, Snell refraction (n 1.5), the rim nearly sharp and bent over a soft middle. |
| Specular highlights; 2026 adds a darkened edge and brighter speculars. | Glassy's rim: lit from the top left, a softer light opposite, shade inside, a specular ring; on Light a dark hairline on top so the edge holds on the light page. |
| Glass illuminates from within under a touch, the glow spreading. | A press lights the glass where it lands (`--glass-glow`, placed by one pointerdown listener) and fades as it lets go. |
| Tint only for meaning (the primary action). | No color on glass: Glassy's slab is a neutral gray (white on Light). The primary stays the solid ink control. |
| Vibrant text on glass. | Secondary words on glass are brighter than the page's Muted: small glass `#dbdbdb` (Dark `#c8c8c8`), large `#bdbdbd` (Dark `#b0b0b0`), Light `#555555`; hover and current fills are lifts of the glass (ink alpha), not grays that would turn into holes over bright art. |
| Content scrolls under bars; the scroll edge effect dims or blurs it (over dark content it dims), stronger since 2026 where text sits near the bars. | The Library runs under the Bar and stays sharp: no scroll edge, by the owner's choice. The band keeps every word on the Bar legible without one. |
| Concentric corners; capsules for standout controls. | Unchanged: capsules in the Bar, `--exo-row-radius` inside panels. |
| Reduce Transparency: frostier, hiding more of what is behind. Increase Contrast: black or white with a contrasting border. Reduce Motion: no elastic effects. 26.1 adds Clear / Tinted; the 27 releases (fall 2026) a slider from ultra clear to fully tinted. | Settings › General › Material: Glass or Solid (no backdrop: controls take the solid fill, large glass `--exo-solid-panel`). Windows' Transparency effects switch (`prefers-reduced-transparency`) is not followed: it is one switch for the whole desktop, mostly turned off for speed, and Solid serves anyone who needs glass gone. `prefers-contrast: more` is the page's color with an ink border and every word full ink; forced colors are Canvas and CanvasText; Reduce motion stops the press light's fade. Nothing else switches it. |
| Materialize: glass appears by ramping its lensing, not its opacity. | Not done: the lens is an SVG reference, which can't interpolate; menus keep their own short open. |

## Legibility by arithmetic

Text on glass is only as legible as the brightest pixel that can end up under it. The legibility research (NN/g's "Liquid Glass Is Cracked", its glassmorphism and text-over-image studies, Apple's own beta-to-26.1 retreat, WCAG 2.x) agrees that text over unpredictable art must be designed for the worst background (NN/g also asks for more blur over busy art, which large glass has). Blur removes texture but not brightness, so the glass caps the brightness.

Each glass maps a backdrop channel `v` to `slope × v + intercept`: Clear by CSS filter functions (`brightness(b) contrast(c)`: `slope = b × c`, `intercept = (1 − c) / 2`) with the slab composited over it. Computed from the tokens (sRGB arithmetic, the band from a black to a white backdrop, the worst pixel over white, yellow, cyan, sky, red, green, blue and black; the sheen, at most 5% white over the top 45%, 8% on Light, left out):

| Glass | Band | Words: worst pixel |
| --- | --- | --- |
| Small, Clear, Void | 7–36% | white 6.6:1, `#dbdbdb` 4.8:1 |
| Small, Clear, Dark | 9–36% | `#e0e0e0` 5.1:1, `#c8c8c8` 4.0:1 |
| Small, Clear, Light | 80–100% | `#1e1e1e` 10.4:1, `#555555` 4.6:1 |
| Small, Clear, light tone (Void, Dark) | 77–100% | `#0a0a0a` 11.4:1, `#383838` 6.7:1 |
| Large, Clear, Void / Dark | 13–35% | white 6.8:1, `#bdbdbd` 3.6:1 / `#e0e0e0` 5.2:1, `#b0b0b0` 3.2:1 |
| Large, Clear, Light | 90–100% | `#1e1e1e` 13.3:1, `#555555` 5.9:1 |
| Badge over art (Void, Dark) | 12–40% | white 5.6:1 |
| Solid | none | the page's ink on the solid fill |

The E2E threshold is 4.5:1 (under 3:1 fails), measured from the screen against what is really behind each word. By the arithmetic, secondary words on Clear large glass, and on Dark's small glass, fall under it only over near-white art; a hovered row's secondary words go full ink. Alpha is set whole after an SVG blur, so the edge never shows raw art (Chromium 3).

## One tone per Appearance

- **Small glass** on Void and Dark is Glassy's slab (`--glassy-slab`) over `--glassy-tone`; on Light, Light's frost. It
  never flips with what is under it: the band keeps its words legible over any art. (Exo once flipped the Bar's capsules
  and Music's player to light glass over bright art, read from a 24×24 sample of the covers; it read as two controls
  and changed the Bar as you scrolled, so it was removed.)
- **Large glass** (panes, sheets, menus, popovers, Ctrl+K, toasts) is the same in every place: its words are always the
  panel ink.

## Liquid Glass elsewhere on Windows

Windhawk's Windows 11 Taskbar Styler (and every "Liquid Glass" theme for it) restyles the taskbar's XAML through XAML diagnostics and paints with its own `WindhawkBlur` brush: backdrop, Gaussian blur, saturation, an optional noise tile and a flat tint, with a capsule shape and 1 to 2px gradient borders for the specular rim. Nothing is refracted: Windows' composition layer has no displacement (`DisplacementMapEffect` is not supported in composition, and composition effect brushes take no custom shaders). The projects that do refract on Windows capture the screen and draw a lens in a separate window, which Exo will not do. So the lens has to live in the web layer, where Exo has it, and the host's part is signals (Windows' more contrast and forced colors). The themes' tints (7–22% black or white) would leave white words at about 1.2 to 1.7:1 over white art; Exo's band is what keeps them legible.

## Chromium, as found

Each of these was seen on screen in Chromium's own renderer before the code was written around it.

1. **`backdrop-filter: url(#svg)` works** and chains with other filters in either order; WebView2 is Chromium. Only Chromium implements it.
2. **An SVG backdrop filter is laid out against the element's ink overflow.** An outer `box-shadow` (on the element or on its pseudo-element), an outer outline or an overflowing child shifts the filter's box, so a lens map lands offset. Lens glass therefore has no outer shadow and its focus ring sits inside (`outline-offset: -2px`). The lens filter's region is generous and its result is laid over the unbent backdrop, so a shifted map never exposes raw art.
3. **A blur near the glass's edge pulls in transparency**, and whatever is transparent shows the raw backdrop: over white art, a bright fringe right where words can sit. A bleed layer clipped back to the shape doesn't help (the crop follows the clip). The cure is in the filter: after the blur, alpha is set to 1, so each fringe pixel keeps the average color of what was actually sampled (a normalized blur), and the band applies to it like everything else.
4. **A missing `<filter>` voids the whole chain**, blur and all, leaving the glass see-through. The shared filters are in `ui/index.html` from the first frame; a lens is named in `--exo-lens` only after its filter is in the page, and that name is removed before the filter is.
5. **A child's backdrop doesn't include its parent's filtered backdrop.** Splitting tone and blur between an element and its pseudo-element sees nothing; both live in one chain.
6. **Backdrop roots:** `filter`, `opacity` below 1, `mask`, `clip-path`, `backdrop-filter`, `mix-blend-mode`, `view-transition-name` or `will-change` on an ancestor limits what glass can see to that ancestor's content. The Bar's capsules sit outside every room and see the whole page.
7. **Unlayered rules beat Tailwind's utilities**, whatever their specificity. Glass's `position: relative` lives in `@layer base`, or Ctrl+K (positioned by utilities) would lose its place.
8. **A blur layer over positioned text over transformed art** can leave that text out of the blur (Continue's titles stayed sharp under a blur band). Exo has no scroll edge now; content under the Bar stays sharp.
9. **A finished animation that keeps filling is a backdrop root.** `animation-fill-mode: both` (or `forwards`) on an opacity entrance keeps a compositor effect after it ends, so glass inside the room or page it faded in saw only that element's own content, never the room behind. Exo's entrance fades hold only before they start (`backwards`), which looks the same, and a glass sheet settles by its size alone while its glass and words fade. A fade that is running is a backdrop root too, so a game page (`.exo-enter`) and a room never fade themselves: a cover over them fades away instead, and the glass on them reads the art from the first frame.
10. **A view transition blinds glass.** Mid-move Chromium draws the page from snapshots, and glass in a snapshot sees nothing behind it: it drew clear and popped back as the move ended. While `data-exo-vt` is on the root, every glass surface and pane draws a still tint in its own band (`--glassy-still*`) and blurs again after; controls take it on themselves, not through the root, so a transition doesn't restyle the whole page.
11. **The page's root background is not in the backdrop.** Over it the backdrop is transparent: CSS filter functions leave it transparent, and an SVG filter that sets alpha whole turns any faint hairline over that transparency into a white band (it unpremultiplies a near-zero alpha). Rooms paint their own background, and panes and controls use CSS filter functions.
12. **A pane's glass is a layer under its content**, a `::before` at `z-index: -1` inside `isolation: isolate`, so it is not an ancestor of what sits on the pane. Its edge is that layer's rim. An SVG blur on such a layer drew white bands and blocks along the pane's edges, so panes use CSS functions.
13. **SVG backdrop filters cost more than CSS functions.** About ten small glass controls with an SVG pair over a scrolling list dropped 2 to 3% of frames at 165 Hz; the same band in CSS functions dropped none. Only the lens stays SVG.

Cost: each glass element is a render pass that re-filters when what is behind it changes. The repeated controls of a scrolling list (a song's or a library row's More, a friend's Message and More, a reaction) take no backdrop: a backdrop for every row cost about 2% of frames while a list scrolled at 165 Hz. A still glass costs nothing; it stays the same while a game runs or Exo is in the background, so it never switches under you.

## Seeing it

`appearance.glass-<theme>` in the E2E run checks, in each Appearance, that every glass on screen has a backdrop and every filter it names is in the page (unless glass is solid), and slides a sheet of the opposite brightness under the Bar (black where the Bar reads bright, as Light's frost does, else white) to check the Bar changes over it; `rooms` and `appearance` measure each word against the pixels behind it, glass and art included (4.5:1 or more). In a browser build of the UI (`npm run dev`, the mock host), the same method works with Playwright: give the mock games art, open each room, and read the brightest pixel inside each glass with its words hidden. Glass is solid only with Settings › General › Glass › Solid or Windows' more contrast and forced colors, which is easy to mistake for weak glass.
