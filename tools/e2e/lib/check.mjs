// Checks and the report. A check is one claim about Exo with its evidence: PASS, FAIL, WARN (works, but a budget or
// a soft rule was missed) or SKIP (couldn't be tried here, and why). Nothing personal reaches the report: e-mail
// addresses and long digit runs are masked.
import { createHash } from 'node:crypto'
import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises'
import { join, relative } from 'node:path'
import { settle } from './page.mjs'

const mask = (text) => String(text)
  .replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, '<email>')
  .replace(/\b\d{15,20}\b/g, '<id>')

const redact = (value) => typeof value === 'string' ? mask(value)
  : Array.isArray(value) ? value.map(redact)
  : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([k, v]) => [k, redact(v)]))
  : value

export class Run {
  constructor(out) {
    this.out = out
    this.shots = join(out, 'screenshots')
    this.checks = []
    this.started = Date.now()
    this.meta = {}
  }

  async init() { await mkdir(this.shots, { recursive: true }) }

  /**
   * Runs one check. `fn(c)` returns nothing or a list of failure reasons; c.warn(reason) marks it WARN,
   * c.skip(reason) SKIP, c.metric(name, value, unit, budget) records a number (over budget: WARN), c.shot(label)
   * saves the page, c.file(name) is a path for any other picture.
   */
  async check(suite, id, name, fn, { shell } = {}) {
    const entry = { suite, id: `${suite}.${id}`, name, status: 'PASS', reasons: [], warnings: [], metrics: {}, evidence: {}, screenshots: [], ms: 0 }
    const started = Date.now()
    const c = {
      evidence: entry.evidence,
      warn: (reason) => entry.warnings.push(reason),
      skip: (reason) => { const e = new Error(reason); e.skip = true; throw e },
      metric: (key, value, unit = '', budget = null, higherIsBetter = false) => {
        entry.metrics[key] = { value: typeof value === 'number' ? +value.toFixed(2) : value, unit, budget }
        if (budget != null && (higherIsBetter ? value < budget : value > budget)) entry.warnings.push(`${key} ${typeof value === 'number' ? +value.toFixed(1) : value}${unit} (budget ${budget}${unit})`)
      },
      file: (label, ext = 'png') => {
        const file = `${entry.id}-${label}.${ext}`.replace(/[^a-z0-9.-]+/gi, '-')
        entry.screenshots.push('screenshots/' + file)
        return join(this.shots, file)
      },
      // A picture of where things landed, never of them mid-move (a menu fading in, a page sliding, cards arriving).
      shot: async (label = 'page', page = shell) => { if (page) { await page.call(settle, 120, 1500).catch(() => {}); await page.screenshot(c.file(label)) } },
    }
    try {
      const failures = (await fn(c)) ?? []
      entry.reasons.push(...failures.filter(Boolean))
      entry.status = entry.reasons.length ? 'FAIL' : entry.warnings.length ? 'WARN' : 'PASS'
    } catch (error) {
      entry.status = error.skip ? 'SKIP' : 'FAIL'
      entry.reasons.push(error.skip ? error.message : 'error: ' + (error.stack || error.message).split('\n').slice(0, 3).join(' | '))
    }
    entry.ms = Date.now() - started
    Object.assign(entry, redact({ reasons: entry.reasons, warnings: entry.warnings, evidence: entry.evidence }))
    this.checks.push(entry)
    const mark = { PASS: 'ok  ', WARN: 'warn', FAIL: 'FAIL', SKIP: 'skip' }[entry.status]
    console.log(`${mark} ${entry.id} (${entry.ms} ms)${entry.reasons.length ? ' — ' + entry.reasons.join('; ') : ''}${entry.warnings.length ? ' — ' + entry.warnings.join('; ') : ''}`)
    return entry
  }

  counts() {
    const n = { PASS: 0, WARN: 0, FAIL: 0, SKIP: 0 }
    for (const c of this.checks) n[c.status]++
    return n
  }

  async write() {
    const counts = this.counts()
    const report = { ...redact(this.meta), startedAt: new Date(this.started).toISOString(), ms: Date.now() - this.started, counts, checks: this.checks }
    await writeFile(join(this.out, 'report.json'), JSON.stringify(report, null, 2))
    const lines = [
      `# Exo end-to-end run`, '',
      `${new Date(this.started).toLocaleString()} · ${Math.round(report.ms / 1000)} s · Exo ${this.meta.version ?? '?'}`, '',
      `**${counts.PASS} passed, ${counts.WARN} warnings, ${counts.FAIL} failed, ${counts.SKIP} skipped**`, '',
    ]
    for (const status of ['FAIL', 'WARN', 'SKIP', 'PASS']) {
      const group = this.checks.filter(c => c.status === status)
      if (!group.length) continue
      lines.push(`## ${{ FAIL: 'Failed', WARN: 'Warnings', SKIP: 'Skipped', PASS: 'Passed' }[status]}`, '')
      for (const c of group) {
        const notes = [...c.reasons, ...c.warnings].join('; ')
        const metrics = Object.entries(c.metrics).map(([k, m]) => `${k} ${m.value}${m.unit}`).join(', ')
        lines.push(`- \`${c.id}\` ${c.name}${notes ? ` — ${notes}` : ''}${metrics ? ` _(${metrics})_` : ''}`)
      }
      lines.push('')
    }
    await writeFile(join(this.out, 'summary.md'), lines.join('\n'))
    // A manifest of every file, so the artifact can be checked later (sha256sum -c SHA256SUMS.txt).
    const files = []
    const walk = async (dir) => { for (const e of await readdir(dir, { withFileTypes: true })) e.isDirectory() ? await walk(join(dir, e.name)) : files.push(join(dir, e.name)) }
    await walk(this.out)
    const sums = await Promise.all(files.filter(f => !f.endsWith('SHA256SUMS.txt')).map(async f =>
      `${createHash('sha256').update(await readFile(f)).digest('hex')}  ${relative(this.out, f).replace(/\\/g, '/')}`))
    await writeFile(join(this.out, 'SHA256SUMS.txt'), sums.join('\n') + '\n')
    return report
  }
}
