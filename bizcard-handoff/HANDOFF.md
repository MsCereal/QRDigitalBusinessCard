# BizCard — Developer Handoff

This document is written for the developer taking over BizCard. It covers what the
app does, how it's put together, how to get it running, what's been verified, and
what still needs doing. For end-user-facing overview and config reference, see
[`README.md`](./README.md).

## What this app does

A user opens the app in a browser on a domain-joined Windows machine. They are
signed in silently from their existing Windows session (no password prompt) and
shown their own Active Directory "business card": name, title, department, email,
phone, and photo.

The design has three stages:

1. **Kerberos handshake** — browser and server negotiate Windows Integrated Auth.
   No password is ever typed into or seen by the app.
2. **Identity from the ticket** — the app reads `sAMAccountName` / UPN off the
   authenticated principal.
3. **Read-only LDAP query** — the app binds to AD as a dedicated read-only service
   account and pulls `displayName`, `title`, `department`, `mail`,
   `telephoneNumber`, `thumbnailPhoto`.

## Tech stack

- ASP.NET Core 8 (minimal APIs)
- `Microsoft.AspNetCore.Authentication.Negotiate` — Kerberos/NTLM
- `System.DirectoryServices.Protocols` — raw LDAP client
- Static `wwwroot/index.html` front end (no build step, no framework)

## File map

| File | Responsibility |
| --- | --- |
| `Program.cs` | Auth setup, config wiring, `/api/me` and `/api/me/photo` endpoints, `ReadIdentity` |
| `LdapDirectoryService.cs` | Read-only LDAP bind, search, attribute mapping, RFC 4515 filter escaping, 5-min cache |
| `DirectoryOptions.cs` | Config shape bound from the `Directory` section |
| `DirectoryUser.cs` | Result record + the list of AD attributes requested |
| `wwwroot/index.html` | The card UI; calls `/api/me`, renders, falls back to initials |
| `appsettings.json` | Defaults, empty `Directory` section |
| `appsettings.Local.json.example` | Copy to `appsettings.Local.json` and fill in |

## Getting set up

### Prerequisites

- **.NET 8 SDK.** Not currently installed on the original machine (`dotnet.exe`
  exists but no SDK). Install from https://aka.ms/dotnet-download.
- A domain-joined Windows machine for real Kerberos testing.
- A read-only AD service account and the LDAP details for your domain.

### First run (identity-only, no AD needed)

You can confirm the Kerberos half works before touching AD. Leave the `Directory`
section empty and:

```powershell
dotnet restore
dotnet run
```

Open the app. It will authenticate you and show your ticket identity with a note
that the directory isn't configured. This isolates auth from AD connectivity.

### Full config

1. Copy the template:

   ```powershell
   Copy-Item appsettings.Local.json.example appsettings.Local.json
   ```

2. Fill in `Host`, `BaseDn`, and `ServiceAccountUser`. Leave the password out of
   the file. Set it via user-secrets:

   ```powershell
   dotnet user-secrets set "Directory:ServiceAccountPassword" "<password>"
   ```

   or the `Directory__ServiceAccountPassword` environment variable.

3. `dotnet run`, open the app, confirm your card loads with real AD data.

`appsettings.Local.json` is gitignored and loaded as an optional override in
`Program.cs`. Config precedence (later wins): `appsettings.json` →
`appsettings.{Environment}.json` → `appsettings.Local.json` → user-secrets → env vars.

## Important gotchas

- **`localhost` gives you NTLM, not Kerberos.** Kerberos needs an SPN matching the
  requested host name. To exercise real Kerberos, register
  `HTTP/bizcard.corp.example.com` against the account the app runs as and browse to
  that name. The app authenticates either way, so it's easy to think Kerberos works
  when you're actually on NTLM.
- **The service account must be read-only.** Its only job is the LDAP bind. It
  should have no write access to the directory.
- **Put TLS in front of the app.** Negotiate over plain HTTP is fine on a trusted
  LAN, but the returned directory data (including the photo) shouldn't travel
  unencrypted in production. Consider LDAPS too (`UseLdaps: true`, `Port: 636`).
- **`index.html` is served anonymously.** The authorization fallback policy covers
  mapped endpoints, not static files. The markup is static and `/api/me` is what
  requires the ticket, but move the page behind auth if that matters to you.
- **Identity-only fallback is deliberate.** An empty `Directory` section is treated
  as "not configured" rather than an error, to allow the auth-only smoke test. If
  you'd rather it fail loudly on misconfiguration, change `LdapDirectoryService.Find`
  and/or add startup validation.

## Verification status

| Item | Status |
| --- | --- |
| Code written | ✅ |
| `dotnet restore` | ❌ not run — no SDK on the original machine |
| `dotnet build` | ❌ not run — no SDK |
| Kerberos handshake tested against a real DC | ❌ not tested |
| LDAP lookup tested against a real AD | ❌ not tested |

**Nothing has been compiled or run.** The code was reviewed by hand only. Treat the
first `dotnet build` as expected work, not a formality — package versions and API
usage haven't been validated by a compiler.

## Suggested first steps for the new owner

1. Install the .NET 8 SDK; run `dotnet restore` and `dotnet build`, fix anything the
   compiler flags.
2. Run in identity-only mode and confirm the Negotiate handshake.
3. Point `Directory` at a test DC and confirm a real card loads.
4. Set up the SPN and test genuine Kerberos (not NTLM via localhost).
5. Decide on the open questions above (anonymous static file, fail-loud config, LDAPS).

## Open decisions left for you

- Whether static assets should require auth.
- Whether missing/invalid config should fail startup instead of degrading gracefully.
- LDAPS vs. plain LDAP + Negotiate for your environment.
- Whether to add automated tests (none exist yet).
