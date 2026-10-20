# DBP Digital Business Card — v2 Spec

## Overview

Two separate sites, one shared static output folder:

| Site | Audience | Access | Technology |
|---|---|---|---|
| **Internal app** (`bizcard.dbp.gov.ph`) | DBP employees | Office / domain network only | ASP.NET Core 8 (this project) |
| **Public cards site** (`cards.dbp.gov.ph`) | Anyone (internet) | Public | IIS serving static `.html` files |

The internal app **writes** static `.html` files to a shared folder.
IIS **serves** those files publicly. That is the only connection.

---

## 1. Login Page (`/login`)

### What it does
- First page every user sees when they open the internal app.
- User types their Windows domain username (e.g. `msantos`, `jcruz`).
- No password field — Windows Negotiate (Kerberos/NTLM) already authenticated
  them silently at the network level.
- Server compares the typed username against `User.Identity.Name` from the
  Negotiate ticket (format: `DBP\msantos`).
- If they match → session cookie set → redirect to `/me` (their Edit page).
- If they don't match (typed someone else's username, typo, different domain) → error shown, no access.

### Domain enforcement
- Negotiate only issues a ticket for accounts on the joined domain (`dbp.local`).
- Any request without a valid `dbp.local` ticket is rejected at the auth layer
  before the username check even runs.
- Employees outside the office (no domain session) cannot reach the internal app
  at all — enforced by network, not just by code.

### Security
- No credentials ever transmitted or stored.
- Session cookie: `HttpOnly`, `Secure`, `SameSite=Strict`, short expiry (8 hours).
- CSRF: anti-forgery token on the login form POST.
- Brute-force: irrelevant — there is no password to brute-force.
- Rate limit on `/login` POST (already have middleware, extend to cover this).

---

## 2. Session & Identity

- After login, store `sAMAccountName` in a signed, encrypted session cookie.
- All protected routes (`/me`, `/me/edit`, `/me/publish`) check the session.
- No route can access another user's card — every DB query is scoped to the
  session's `sAMAccountName`.

---

## 3. Edit Page (`/me/edit`)

### Load order
1. Check session → get `sAMAccountName`.
2. Query SQLite for existing card by `sAMAccountName`.
3. **If card exists** → load it into the Edit form (all fields pre-populated).
4. **If no card** → call `LdapDirectoryService.Find()` → pre-fill Name,
   Title, Department, Email, Phone from AD. Show green AD banner.
   EmpId still typed manually (AD doesn't store it in `7digits-3letters` format).
5. User edits fields, clicks **Save**.

### Save
- Validates all inputs server-side (EmpId format, required fields, PIN format).
- Writes to SQLite (`cards` table — see schema below).
- If card was previously published → regenerates the static `.html` file immediately.
- Redirects to `/me` (QR entrance screen).

---

## 4. SQLite Database

Replaces `cards.json`. Single file: `Data/bizcard.db`.

### `cards` table
```sql
CREATE TABLE cards (
    sam_account_name    TEXT NOT NULL PRIMARY KEY,  -- sAMAccountName, immutable
    emp_id              TEXT NOT NULL UNIQUE,
    card_token          TEXT NOT NULL UNIQUE,        -- 32-char crypto-random, immutable once set
    card_pin            TEXT NOT NULL DEFAULT '',    -- hashed with BCrypt (upgrade from plaintext)
    is_active           INTEGER NOT NULL DEFAULT 1,

    -- Personal
    name                TEXT NOT NULL DEFAULT '',
    show_name           INTEGER NOT NULL DEFAULT 1,
    preferred_name      TEXT NOT NULL DEFAULT '',
    show_preferred_name INTEGER NOT NULL DEFAULT 0,
    photo               TEXT NOT NULL DEFAULT '',   -- base64 data URI
    show_photo          INTEGER NOT NULL DEFAULT 1,

    -- Professional
    title               TEXT NOT NULL DEFAULT '',
    show_title          INTEGER NOT NULL DEFAULT 1,
    department          TEXT NOT NULL DEFAULT '',
    show_department     INTEGER NOT NULL DEFAULT 1,
    org                 TEXT NOT NULL DEFAULT 'Development Bank of the Philippines',
    show_org            INTEGER NOT NULL DEFAULT 1,
    office              TEXT NOT NULL DEFAULT '',
    show_office         INTEGER NOT NULL DEFAULT 1,

    -- Contact
    phone               TEXT NOT NULL DEFAULT '',
    show_phone          INTEGER NOT NULL DEFAULT 1,
    email               TEXT NOT NULL DEFAULT '',
    show_email          INTEGER NOT NULL DEFAULT 1,

    -- Links
    github              TEXT NOT NULL DEFAULT '',
    show_github         INTEGER NOT NULL DEFAULT 1,
    linkedin            TEXT NOT NULL DEFAULT '',
    show_linkedin       INTEGER NOT NULL DEFAULT 1,
    portfolio           TEXT NOT NULL DEFAULT '',
    show_portfolio      INTEGER NOT NULL DEFAULT 1,

    -- Extra info (JSON array)
    extra_info          TEXT NOT NULL DEFAULT '[]',

    -- Publishing
    is_published        INTEGER NOT NULL DEFAULT 0,
    published_at        TEXT,                        -- ISO 8601 UTC

    -- Timestamps
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,

    -- Analytics (JSON)
    view_history        TEXT NOT NULL DEFAULT '[]',
    total_views         INTEGER NOT NULL DEFAULT 0,

    -- Audit (JSON array, last 50)
    audit_log           TEXT NOT NULL DEFAULT '[]'
);
```

### Migration
- On startup: if `cards.json` exists → import all records into SQLite → rename
  `cards.json` to `cards.json.migrated` (keep as backup, don't delete).
- One-time, automatic, no manual steps.

### PIN security upgrade
- Current: plaintext. New: BCrypt hash (cost factor 12).
- On migration: existing PINs flagged as `needs_rehash`. On next successful
  PIN entry, rehash transparently.

---

## 5. Publish → Static HTML

### Trigger
- User clicks **Publish** on the Edit page (separate button from Save).
- Or: re-publish happens automatically every time the user clicks Save if the
  card has already been published before.

### What gets generated
A single self-contained `.html` file written to:
```
{PublishRoot}\{token}.html
```
`PublishRoot` is configured in `appsettings.json` → `Publishing:OutputPath`
(e.g. `C:\inetpub\wwwroot\cards\`).

### The static file
- **Fully self-contained** — all CSS and JS inlined, no external dependencies,
  no calls back to the internal app.
- Identical look to the current `Card.cshtml` view.
- All interactive buttons work:
  - **Save Contact** → generates and downloads a `.vcf` vCard file (via JS `Blob`),
    respecting all `Show*` flags.
  - **Copy phone / email** → `navigator.clipboard.writeText()`.
  - **Share** → `navigator.share()` with fallback to clipboard copy.
  - **QR code** → modal showing the public card URL QR (generated client-side
    via a small inline JS QR library — no server call).
  - **Dark mode toggle** → persisted to `localStorage`.
  - **Flip animation** → front/back card flip (CSS, works offline).
- Photo embedded as base64 data URI — no external image requests.
- No analytics calls, no session, no cookies set by the static page.

### Public URL
- `https://cards.dbp.gov.ph/{token}.html`
- Token: 32 cryptographically random URL-safe characters (generated once,
  never changes).
- Impossible to enumerate or brute-force (2^192 space).

### IIS setup (manual, one-time)
- Create IIS site pointing to `PublishRoot` folder.
- No server-side processing needed — pure static file serving.
- Add response headers in IIS:
  - `Content-Security-Policy` (strict)
  - `X-Frame-Options: DENY`
  - `X-Content-Type-Options: nosniff`
  - `Referrer-Policy: no-referrer`

---

## 6. Security — OWASP Top 10 + CSP

### A01 Broken Access Control
- Every data operation scoped to session `sAMAccountName` — no `empId` from URL trusted without session check.
- No route exposes other users' cards in edit mode.
- Published static files are public but contain no PII beyond what the user chose to show.

### A02 Cryptographic Failures
- PIN: BCrypt (cost 12) — no plaintext storage.
- Session cookie: AES-256 encrypted + HMAC signed (ASP.NET Data Protection).
- CardToken: `RandomNumberGenerator.GetBytes(24)` → Base64URL → 32 chars.
- HTTPS enforced in production (`UseHttpsRedirection`, HSTS).

### A03 Injection
- All DB access via parameterized queries (Dapper or EF Core with SQLite).
- LDAP filter values RFC 4515-escaped (already done in `LdapDirectoryService`).
- All Razor output HTML-encoded by default (`@value` not `@Html.Raw`).

### A04 Insecure Design
- Login: no password to steal, no brute-force surface.
- Edit: only your own card, session-scoped.
- Publish: output is static HTML — no server execution on the public side.

### A05 Security Misconfiguration
- `appsettings.Local.json` gitignored.
- Passwords/secrets via user-secrets or env vars only.
- Swagger disabled in production.
- Developer exception page disabled in production.

### A06 Vulnerable Components
- `Microsoft.AspNetCore.Authentication.Negotiate 8.0.29` (patched).
- Dependencies pinned to exact versions.

### A07 Identification & Authentication Failures
- No password = no credential stuffing.
- Session expires after 8 hours idle.
- Session cookie: `HttpOnly`, `Secure`, `SameSite=Strict`.

### A08 Software & Data Integrity
- Anti-forgery tokens on all POST forms.
- Static files served from a separate IIS site with no write access from the internet.

### A09 Logging & Monitoring
- Audit log per card (already exists, moves to SQLite).
- Failed login attempts logged (username + timestamp, no sensitive data).
- ASP.NET Core structured logging → file sink.

### A10 Server-Side Request Forgery
- No user-supplied URLs are fetched server-side.
- LDAP host configured only in `appsettings` — not user-controllable.

### Content Security Policy (internal app)
```
Content-Security-Policy:
  default-src 'self';
  script-src 'self';
  style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com;
  font-src 'self' https://cdnjs.cloudflare.com;
  img-src 'self' data:;
  connect-src 'self';
  frame-ancestors 'none';
  base-uri 'self';
  form-action 'self';
```

### Content Security Policy (public static card)
```
Content-Security-Policy:
  default-src 'none';
  script-src 'self' 'nonce-{generated}';
  style-src 'unsafe-inline';
  img-src data:;
  connect-src 'none';
  frame-ancestors 'none';
  base-uri 'none';
  form-action 'none';
```
(Set via IIS response headers or injected into the `<meta>` tag of each generated file.)

---

## 7. Implementation Tasks (in order)

| # | Task |
|---|---|
| 1 | Add `Microsoft.Data.Sqlite` + `Dapper` packages |
| 2 | Create `Data/DatabaseService.cs` — schema init, CRUD, migration from `cards.json` |
| 3 | Replace `CardProfileService` (JSON-backed) with `SqliteCardService` |
| 4 | Add `LoginController` — `GET /login`, `POST /login`, session management |
| 5 | Add `[SessionRequired]` action filter — protects `/me`, `/me/edit`, `/me/publish` |
| 6 | Update `HomeController` — all card routes scoped to session identity |
| 7 | Add `PublishService` — generates self-contained static `.html` from card data |
| 8 | Add **Publish** button to Edit page, `POST /me/publish` endpoint |
| 9 | Add CSP middleware + security headers to all responses |
| 10 | PIN hashing upgrade (BCrypt) |
| 11 | Update `appsettings.json` with `Publishing:OutputPath` and `Security:AllowedDomain` |
| 12 | End-to-end test: login → edit → publish → verify static file |

---

## 8. Config additions (`appsettings.json`)

```json
"Publishing": {
  "OutputPath": "C:\\inetpub\\wwwroot\\cards\\",
  "PublicBaseUrl": "https://cards.dbp.gov.ph"
},
"Security": {
  "AllowedDomain": "dbp.local",
  "SessionTimeoutMinutes": 480
}
```

---

## Open items (decide before task 7)

- Font Awesome is currently loaded from CDN. The static card must work offline
  (no CDN). Options: (a) inline the icon SVGs used, (b) bundle FA CSS into the
  generated file. **Recommendation: inline only the ~10 SVG icons actually used.**
- Photo size cap: AD `thumbnailPhoto` is max 100 KB. User-uploaded photos are
  currently uncapped. Add a 200 KB limit on upload before base64 encoding.
