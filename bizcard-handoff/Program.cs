using System.Security.Claims;
using BizCard;
using Microsoft.AspNetCore.Authentication.Negotiate;

var builder = WebApplication.CreateBuilder(args);

// Optional local overrides for a dev's own directory settings. Gitignored, so it
// never lands in a shared checkout. See appsettings.Local.json.example.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ---------------------------------------------------------------------------
// 1. Browser + server negotiate Kerberos (Windows Integrated Auth).
//    The user is signed in silently from their existing Windows session; no
//    password is ever typed into this app, and the app never sees one.
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

builder.Services.AddAuthorization(options =>
{
    // Every mapped endpoint requires an authenticated Windows identity by default.
    options.FallbackPolicy = options.DefaultPolicy;
});

builder.Services.Configure<DirectoryOptions>(builder.Configuration.GetSection("Directory"));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<LdapDirectoryService>();

var app = builder.Build();

// index.html is served anonymously; it calls /api/me, which is what triggers
// the Negotiate handshake.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------
// 2. Read the caller's identity straight off the ticket.
// 3. Query AD over LDAP using the read-only service account.
// ---------------------------------------------------------------------------
app.MapGet("/api/me", (HttpContext http, LdapDirectoryService directory) =>
{
    var (sam, upn) = ReadIdentity(http.User);

    // Use dummy data when directory is not configured (dev / demo mode)
    DirectoryUser? user = directory.IsConfigured
        ? directory.Find(sam, upn)
        : new DirectoryUser(
            DisplayName: "Juan dela Cruz",
            Title: "Software Engineer",
            Department: "Information Technology",
            Mail: "juan.delacruz@dbp.com.ph",
            TelephoneNumber: "+63 2 8818 9511",
            ThumbnailPhoto: null);

    return Results.Ok(new
    {
        // straight from the Kerberos ticket
        samAccountName = sam ?? "jdelacruz",
        userPrincipalName = upn ?? "juan.delacruz@dbp.com.ph",
        authenticationType = http.User.Identity?.AuthenticationType ?? "Demo",

        // from the LDAP lookup (or dummy)
        directoryConfigured = directory.IsConfigured,
        found = true,
        displayName = user?.DisplayName,
        title = user?.Title,
        department = user?.Department,
        mail = user?.Mail,
        telephoneNumber = user?.TelephoneNumber,
        hasPhoto = user?.ThumbnailPhoto is { Length: > 0 }
    });
});

app.MapGet("/api/me/photo", (HttpContext http, LdapDirectoryService directory) =>
{
    var (sam, upn) = ReadIdentity(http.User);
    var photo = directory.Find(sam, upn)?.ThumbnailPhoto;

    return photo is { Length: > 0 }
        ? Results.File(photo, "image/jpeg")
        : Results.NotFound();
});

app.Run();

// ---------------------------------------------------------------------------

static (string? Sam, string? Upn) ReadIdentity(ClaimsPrincipal principal)
{
    // Negotiate hands us "DOMAIN\samaccountname" as the identity name.
    var name = principal.Identity?.Name;

    var sam = name is not null && name.Contains('\\')
        ? name.Split('\\', 2)[1]
        : name;

    // A UPN claim is not always present; fall back to null and let the LDAP
    // filter match on sAMAccountName alone.
    var upn = principal.FindFirst(ClaimTypes.Upn)?.Value;

    if (upn is null && name is not null && name.Contains('@'))
    {
        upn = name;
    }

    return (sam, upn);
}
