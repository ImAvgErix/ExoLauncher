// Attack 1 — throwaway "victim" process standing in for a Chromium app
// (Spotify/WebView2) that Exo starts with a loopback DevTools port.
//
// It exposes the SAME debugging surface Exo relies on: a loopback debug port
// speaking the Chrome DevTools Protocol (Node's V8 Inspector is CDP; its
// Runtime.evaluate is the identical primitive SpotifyControls.cs drives via
// Page.addScriptToEvaluateOnNewDocument / Runtime.evaluate). No standalone
// Chromium browser is installed on this machine, so this isolated process is
// used as the throwaway target instead of the user's real Spotify.
//
// It holds a pretend signed-in session secret in memory and does nothing else.
globalThis.__sessionSecret = 'SPotIfY-oauth-token-' + 'DO_NOT_LEAK_' + Date.now();
globalThis.__accessToken = 'BQ_fake_access_token_value_1234567890';
// Keep alive.
setInterval(() => {}, 1 << 30);
process.stdout.write('VICTIM_READY\n');
