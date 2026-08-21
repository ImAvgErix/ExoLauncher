function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

export function page(title: string, body: string): string {
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escapeHtml(title)}</title>
  <style>
    :root { color-scheme: dark; }
    body { margin: 0; min-height: 100vh; display: grid; place-items: center;
      font: 15px/1.45 ui-sans-serif, system-ui, sans-serif; background: #000; color: #ddd; }
    main { max-width: 28rem; padding: 2rem; }
    h1 { font-size: 1.1rem; font-weight: 600; color: #fff; margin: 0 0 .5rem; }
    p { margin: 0; color: #aaa; }
  </style>
</head>
<body><main>${body}</main></body>
</html>`;
}

export function checkEmailPage(): string {
  return page(
    "Exo",
    "<h1>Check your email</h1><p>Open the sign-in link we sent. This window can close.</p>",
  );
}

export function authErrorPage(message: string): string {
  return page("Exo", `<h1>Sign-in did not finish</h1><p>${escapeHtml(message)}</p>`);
}

export function storeLinkErrorPage(message: string): string {
  return page("Exo", `<h1>Store link did not finish</h1><p>${escapeHtml(message)}</p>`);
}
