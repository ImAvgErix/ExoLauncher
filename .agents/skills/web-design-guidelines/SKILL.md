---
name: web-design-guidelines
description: Review UI code for Web Interface Guidelines compliance. Use when asked to "review my UI", "check accessibility", "audit design", "review UX", or "check my site against best practices".
metadata:
  author: vercel
  version: "1.0.0"
  argument-hint: <file-or-pattern>
---

# Web Interface Guidelines

_Changed for Exo Launcher: the guidelines are a pinned local copy instead of a fetch from the network before each review, and an Exo section is added. Source and commit: `.agents/skills/SOURCES.md`._

Review files for compliance with Web Interface Guidelines.

## How It Works

1. Read the guidelines in [references/guidelines.md](references/guidelines.md)
2. Read the specified files (or the files the current change touches)
3. Check against all rules in the guidelines
4. Output findings in the terse `file:line` format the guidelines give

## Exo Launcher

`DESIGN.md` is the brief and wins where a guideline disagrees. On this repo that means: sentence case for headings and buttons (not Title Case), no native `title=` tooltips, no letter-spacing on lowercase, Exo's own motion tokens, and no URL state or hydration rules (one WebView2 page, no server rendering, no router URLs). A rule that does not apply is skipped, not reported.
