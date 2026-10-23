---
name: impeccable
description: "Use to review or refine an existing web interface: audit (accessibility, performance, theming, implementation integrity), polish before shipping, make loud UI quieter, distill clutter, harden edge cases, fix typography, layout and spacing, motion, UX copy, or UI performance. Includes the Impeccable anti-pattern detector (npx impeccable detect). Not for backend-only or non-UI tasks."
metadata:
  version: 4.7.0
---

_Changed for Exo Launcher (Apache-2.0, see LICENSE and NOTICE.md): only the review and refine playbooks are kept. The launcher and its engine binary, which it downloads and runs on first use, live mode, hooks, critique storage, new-work, and the native iOS and Android references are not installed. Setup reads the project files directly. Source and commit: `.agents/skills/SOURCES.md`._

This skill gives you the tools and permission to make design that earns the name out-of-distribution craft. Work as an award-winning design director would: production-grade code, a clear POV, a deep grasp of what the client and its users need, and exceptional craft.

Core principles:
- Finish the job. No hedging, no shortcuts: the deliverable is complete, except assets the user must provide.
- Scale ambition to the surface's mode (see Modes). On Operate and Read, ambition shows as precision and restraint, and patterns the visitor already knows earn their place.
- Verify by looking at the rendered result at the sizes it ships at, and batch the checks: fix what one look shows before looking again, rather than screenshotting after every tweak.

## Setup

1. Read `PRODUCT.md` and `DESIGN.md` at the repo root. On Exo Launcher, also load the `exo-design` skill (`.agents/skills/exo-design`): it names the enforced checks and Exo's own rules. Do not invent missing context.
2. Load the request's playbook from the Commands table. Inspect the target's current rendered state and its tokens, CSS and components before editing.
3. Before any UI edit, read [reference/craft-floor.md](reference/craft-floor.md). Do not load it for planning-only work.

The detector is `npx impeccable detect <files>` (add `--json` for machine output). Where a playbook says "the bundled detector" or `impeccable detect`, run that.

**Decide, don't stop.** Where a playbook says "additional context needed" or "ask before continuing", the context is in PRODUCT.md, DESIGN.md and AGENTS.md: decide from them, say what you decided, and ask only for a change to a factual claim or something those files cannot settle.

## How to design

- **The brief wins.** Honor pinned aesthetics, eras, materials, fonts, and palettes even when they conflict with a saturated-pattern warning. Redirecting a clear brief toward your taste is failure. On Exo, DESIGN.md is the brief.
- **Refinement preserves.** Refinement keeps the incumbent identity, behavior, copy, and everything outside scope. Replacing the visual world is a redesign, which these playbooks do not do: say so instead of smuggling one in.
- **Loaded symbols stay out of the decoration.** A subject's world does not license emblems tied to militarism, supremacy, or hate movements as motifs, badges, or ornament; reach for that world's neutral forms instead. Content that documents such a symbol as fact stays as it is.

## Modes

The mode names what the visitor's success looks like on this surface.

- **Persuade:** the visitor decides and acts; design is the product. Landing pages, marketing, pricing.
- **Operate:** the visitor completes a task. App UI, dashboards, editors, settings, tools. Scanability, consistency, native expectations, and the real usage scene outrank expression. Brand lives in precise details. Exo's rooms are Operate.
- **Read:** the visitor understands something. Docs, articles, guides, help, changelogs.
- **Experience:** the visitor is inside the work itself. Portfolios, galleries, showcases.

Choose the mode from the requested surface, not the product. See [operate.md](reference/operate.md) for deeper Operate/Read guidance.

## Commands

| Command | Category | Description | Reference |
|---|---|---|---|
| `audit [target]` | Evaluate | Technical quality checks (a11y, perf, theming, integrity) | [reference/audit.md](reference/audit.md) |
| `polish [target]` | Refine | Final quality pass before shipping | [reference/polish.md](reference/polish.md) |
| `quieter [target]` | Refine | Tone down aggressive or overstimulating designs | [reference/quieter.md](reference/quieter.md) |
| `distill [target]` | Refine | Strip to essence, remove complexity | [reference/distill.md](reference/distill.md) |
| `harden [target]` | Refine | Production-ready: errors, i18n, edge cases | [reference/harden.md](reference/harden.md) |
| `animate [target]` | Enhance | Purposeful motion | [reference/animate.md](reference/animate.md) |
| `typeset [target]` | Enhance | Typography hierarchy | [reference/typeset.md](reference/typeset.md) |
| `layout [target]` | Enhance | Spacing, rhythm, and visual hierarchy | [reference/layout.md](reference/layout.md) |
| `clarify [target]` | Fix | UX copy, labels, and error messages | [reference/clarify.md](reference/clarify.md) |
| `optimize [target]` | Fix | Diagnose and fix UI performance | [reference/optimize.md](reference/optimize.md) |

Routing:

- **Explicit or clearly implied command:** load its reference and follow it.
- **A playbook names a command that is not in this table:** use the closest one here, or say the finding needs a redesign.
- **No command:** treat the request as a review of the named surface: `audit`, then `polish` for what it finds.
