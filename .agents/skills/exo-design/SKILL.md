---
name: exo-design
description: Exo Launcher's design system and visual-change checklist. Use for any change to how Exo looks, moves or reads, in ui/ or mouse/, even a one-line CSS tweak: CSS, tokens, Liquid Glass, Light/Dark/Void, type, icons, motion, hover and press states, layout, menus, sheets, a room's UI (Library, Store, Discord, Music, System, Devices, Settings), UI copy, or a mockup. Not for host C#, RPC or logic changes that draw nothing new.
---

# Exo design system

The sources are the truth; this file points at them. Read `DESIGN.md` for the look (Tokens, Grammar, Glass, Motion), `docs/architecture/liquid-glass.md` for how glass works in Chromium and why, and `ui/src/tokens.css` for the values. When this file and those disagree, they win.

## The rules that matter most

- **Only the page is flat.** The page is the Appearance's color (Void `#000`, Dark `#121212`, Light `#F5F5F5`). Everything on it that floats or acts (Bar capsules, panels, sheets, menus, popovers, Ctrl+K, toasts, Music's player, every control) is Liquid Glass: small glass for controls, large for overlays, panes as a layer under their content. Glass never sits on glass that blurs on its own element; controls there are lifts of that glass.
- **Tokens only.** Every color, in Light, Dark and Void alike, is a theme token. A gray is the ink at an alpha (`rgb(var(--exo-ink-rgb) / 0.14)`), a page fill `var(--color-bg)`. A control's fill is `--exo-fill`, `--hover-fill`, `--exo-pressed`, `--exo-selected` or `--color-fg` (Grammar › Controls). Glass adds no color; primary is the one control in the ink's color.
- **Type.** Inter 4.1 from `ui/public/fonts`. Sizes 20 / 13 / 12 as `calc(Npx * var(--exo-scale, 1))`; other sizes are named exceptions in `ui/css-design.config.mjs`. Controls 13px Medium, section names the only bold (600), titles 650 in the Display cut. Sentence case, no uppercase labels, no letter-spacing on lowercase (it turns off Inter's contextual forms in Chromium).
- **Shape.** Radii: cards and menus 14px, panels, popovers, dialogs and featured art 18px, sheets 24px, pills 999px; window inset 10px. One hairline (`--exo-float-line`) sets a surface apart; no nested cards. Art has no ring, border or inset shadow on its edge; rings are an outline 2px outside it.
- **Motion.** Two curves: `--ease-out` for what appears or changes state, `--ease-spring` (damped, no visible overshoot) for what travels or changes shape; `linear` for progress only. Five times: `--dur-press` 90ms, `--dur-quick` 120ms, `--dur-base` 160ms, `--dur-move` 220ms, `--dur-flight` 300ms. Press gives way to 0.97. Glass filters never animate, and nothing that holds glass fades whole (a fading parent blinds the glass in it; DESIGN.md › Motion › Appear). No bounce, no growing on hover, no shadow on hover, no hover fade. Under Reduce motion only fades remain. It must feel instant at 120 Hz: cache first, transforms not layout, no backdrop per row in a scrolling list.
- **Legible over real backgrounds.** Words on glass keep 4.5:1 against the brightest pixel that can be under them (3:1 fails); the arithmetic is in liquid-glass.md › Legibility.
- **Content first, quiet chrome.** Art is the biggest thing on screen. No hint copy, ledes, eyebrows or decoration. A page that fits gets no sidebar or disclosure.

Interaction feel, after Emil Kowalski's apple-design skill (github.com/emilkowalski/skills, MIT), only where it agrees with DESIGN.md: respond on pointer-down, not on release; a drag tracks the pointer 1:1 from where it was grabbed (`setPointerCapture`); every animation can be interrupted and restarts from the value on screen, never from its target; a released drag hands its velocity to the spring and snaps to where its momentum projects; push past an edge against growing resistance rather than a hard stop; things leave the way they came and grow from their trigger. Exo departs from it on purpose: no bounce even after a flick, no letter-spacing, glass blur never animates, no scroll-edge effect, Inter not the system font, and Windows' Transparency switch is not followed.

## Exo's own rules

- US spelling everywhere: color, gray, favorite, center, behavior.
- No source notes under values ("From tracker.gg"); show the value alone.
- Discord has Discord's layout and features with Exo's look: tokens, rows, menus, glass and motion from DESIGN.md, never Discord's palette. Every room is one ecosystem.
- Icons are Tabler through `ui/src/brand/icons.tsx` (`npm run icons` writes the data); no emoji, no native `title=` tooltips.
- Ideas and brainstorms: show rendered PNG mockups; DESIGN.md is the current state, not a limit on ideas.

## Enforced checks

From `ui/`:

- `npm run lint:css`: `scripts/css-design.mjs` (`exo/hover-fill`, `exo/control-fill`, `exo/art-edge`, `exo/no-hover-fade`, `exo/type-scale`, `exo/one-slider`), `glass-controls.mjs --check` (every `--exo-fill` rule is listed for its glass; `npm run glass:controls` rewrites the list) and `icons.mjs --check`. Also in `mouse/`'s `npm run check`.
- `npm run check`: contracts, ESLint (Tailwind `text-*` sizes, `hover:bg-white/*`, `hover:opacity-*`, `title=`) and `lint:css`.
- The Impeccable detector on the CSS you touched: `npx impeccable detect <files>`.
- E2E on the installed app (`node tools/e2e/run.mjs --suites appearance,rooms`): `appearance.glass-<theme>` (every glass has its backdrop and filters, the Bar changes over a sheet of the other brightness), contrast measured from the screen's pixels in `rooms.<theme>-<room>` and `appearance.overlays-<theme>`. It restarts Exo; run it for broad changes, not every tweak.

## Checklist for a visual change

1. Read the DESIGN.md section for what you touch, and reuse an existing class, component or token before adding one.
2. Build it in tokens; no literal colors, sizes or durations.
3. `npm run check` in `ui/` (and `mouse/` if touched); `npx impeccable detect` on the changed CSS. Fix the code, not the check.
4. Look at the changed part on screen in the installed Exo after a deploy (AGENTS.md › Ship; CDP on port 9339), not the dev preview, in Light, Dark and Void, with glass over bright and dark art. Before any reload or restart, check for a Discord call; a reload ends it.
5. Read every word on glass: 4.5:1 or better. Press, hover and keyboard focus each look right; Reduce motion only fades.
6. Spelling, sentence case, no hint or source copy. Say in the reply what you saw on screen and what you could not check.
