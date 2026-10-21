// Attack 1 (XSS) — LIVE injection against the running Exo dev UI.
//
// How it was run:
//   1. cd ui && npm ci && npm run dev        (serves hostMock at http://localhost:5174)
//   2. Open that URL in a WebView/browser dev console.
//   3. Paste this whole script into the console and read the returned object.
//
// It injects malicious untrusted strings through emitHostEvent('library.updated'),
// the exact event the native WebHostBridge pushes for store manifests / titles,
// then checks whether anything executed.
//
// PROVEN RESULT (2026-10-16): alertsFired=[], __XSS undefined, no img[onerror],
// titles rendered as escaped text ("&lt;img src=x onerror=...&gt;"). No execution.
(async () => {
  const fired = []
  window.alert = (m) => fired.push('alert:' + m)
  const host = await import('/src/lib/host.ts')
  const payload = '<img src=x onerror="window.__XSS=1;alert(1)">'
  const scriptPayload = '<script>window.__XSS2=1<\/script>'
  host.emitHostEvent('library.updated', {
    count: 2,
    games: [
      { id: 'atk:1', title: payload, store: 'steam', installed: true, owned: true,
        primaryAction: 'play', status: 'Ready', deps: [], launchNote: '' },
      { id: 'atk:2', title: scriptPayload, store: 'local', installed: false, owned: true,
        canInstall: true, primaryAction: 'install', status: 'Ready', deps: [], launchNote: '' },
    ],
  })
  await new Promise(r => setTimeout(r, 600))
  const html = document.body.innerHTML
  return {
    executed_xss1: window.__XSS === 1,
    executed_xss2: window.__XSS2 === 1,
    alertsFired: fired,
    liveImgInjected: !!document.querySelector('img[onerror]'),
    titleAppearsEscaped: html.includes('&lt;img src=x onerror'),
    rawPayloadInHtml: html.includes('<img src=x onerror='),
  }
})()
