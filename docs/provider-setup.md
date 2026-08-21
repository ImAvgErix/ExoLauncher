# Exo identity provider setup

Google OAuth and email magic links are optional Worker capabilities. The
launcher reports their real health; it never treats a missing credential as a
successful sign-in.

## Google OAuth

Create a Google OAuth client for a **Web application** in the Google Cloud
Console. Add this exact production callback:

```text
https://exo-id.exo-erix.workers.dev/api/auth/callback/google
```

Request only `openid`, `email`, and `profile`. Keep the client secret out of
the repository. Configure the client id as the Worker variable
`GOOGLE_CLIENT_ID` and store the secret with Wrangler:

```powershell
cd services/exo-id
npx wrangler secret put GOOGLE_CLIENT_SECRET
```

Deploy after the variable and secret are present:

```powershell
npx wrangler deploy --keep-vars
```

## Email magic links

In Resend, verify a sending domain first. Choose a sender such as
`Exo <no-reply@your-verified-domain.example>` and set it as the Worker
variable `RESEND_FROM`. Store the API key as a secret:

```powershell
cd services/exo-id
npx wrangler secret put RESEND_API_KEY
npx wrangler deploy --keep-vars
```

The API advertises `providers.email=true` only when both values exist. A failed
send returns an honest error; it does not create a session. Email verification,
password reset, and recovery are intentionally separate future work.

## Verify without exposing secrets

```powershell
$health = Invoke-WebRequest -UseBasicParsing https://exo-id.exo-erix.workers.dev/v1/health
($health.Content | ConvertFrom-Json).capabilities.providers
npx wrangler secret list
```

The health response should show `google=true` and/or `email=true`; `secret list`
should show only names. Never print secret values or paste them into launcher
logs, browser storage, or source control.
