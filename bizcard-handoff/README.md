# BizCard

Shows the signed-in user their own AD "business card" without ever asking for a password.

> **Taking over this project?** Start with [`HANDOFF.md`](./HANDOFF.md) — it covers
> setup, verification status, gotchas, and open decisions.

## Flow

1. Browser and server negotiate Kerberos (Windows Integrated Auth). The user is
   silently signed in from their existing Windows session.
2. The app reads `sAMAccountName` / UPN off the ticket (`Program.cs` → `ReadIdentity`).
3. The app queries AD over LDAP using a **read-only service account**
   (`LdapDirectoryService.cs`) and pulls `displayName`, `title`, `department`,
   `mail`, `telephoneNumber`, `thumbnailPhoto`.

## Files

| File | Purpose |
| --- | --- |
| `Program.cs` | Negotiate auth setup, `/api/me` and `/api/me/photo` endpoints |
| `LdapDirectoryService.cs` | Read-only LDAP bind, search, attribute mapping, 5-min cache |
| `DirectoryOptions.cs` / `DirectoryUser.cs` | Config and result shapes |
| `wwwroot/index.html` | The card UI |

## Configure

Copy `appsettings.Local.json.example` to `appsettings.Local.json` (gitignored) and
fill in the `Directory` section, or edit the section in `appsettings.json` directly.
Keep the password out of any committed file:

```powershell
dotnet user-secrets set "Directory:ServiceAccountPassword" "<password>"
```

or set `Directory__ServiceAccountPassword` as an environment variable.

```json
"Directory": {
  "Host": "dc01.corp.example.com",
  "Port": 389,
  "UseLdaps": false,
  "BaseDn": "DC=corp,DC=example,DC=com",
  "ServiceAccountUser": "CORP\\svc-bizcard-ro",
  "CacheSeconds": 300
}
```

The bind uses `AuthType.Negotiate`, so the service-account password is not sent in
the clear even on port 389. For LDAPS set `UseLdaps: true` and `Port: 636`.

## Run

```powershell
dotnet restore
dotnet run
```

Then open http://localhost:5080.

If the `Directory` section is left empty the app still runs: it authenticates you
and shows the ticket identity, with a note that the directory is not configured.
That is the quickest way to confirm the Kerberos half works on its own.

## Notes for a real deployment

- **`localhost` gives you NTLM, not Kerberos.** Kerberos needs an SPN matching the
  host name the browser requests. Register `HTTP/bizcard.corp.example.com` against
  the account the app pool runs as, and browse to that name.
- **The service account needs read access only.** Its only job is the LDAP bind;
  it should not be able to write to the directory.
- **`index.html` is served anonymously.** The authorization fallback policy covers
  mapped endpoints, not static files. The page is just markup, and `/api/me` is what
  requires the ticket, but move it behind auth too if that matters to you.
- `thumbnailPhoto` is served as `image/jpeg`, which is what AD normally holds. It is
  also capped at 100 KB by AD, so it is small enough to return inline.
- Add TLS in front of the app. Negotiate over plain HTTP is fine on a trusted LAN
  but you do not want the returned directory data travelling unencrypted.

## Status

Written but **not compiled** — there is no .NET SDK on this machine, so
`dotnet restore` / `dotnet build` could not be run to verify it.
