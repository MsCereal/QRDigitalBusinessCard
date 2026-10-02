using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DBPBusinessCardEditable.Controllers;

/// <summary>
/// Login flow:
///   1. GET  /login        — show the username form (anonymous)
///   2. GET  /auth/windows — [Authorize] endpoint that forces the Negotiate
///                           handshake; browser does NTLM silently, then
///                           this redirects back to POST /login/confirm
///   3. POST /login/confirm — receives the typed username, verifies it
///                            against the now-established Windows identity,
///                            and sets the session cookie
/// </summary>
public class LoginController : Controller
{
    private readonly IConfiguration _config;
    public LoginController(IConfiguration config) => _config = config;

    // ── Step 1: show the login form ───────────────────────────────────────────

    [HttpGet("/login")]
    public IActionResult Index()
    {
        if (!string.IsNullOrWhiteSpace(HttpContext.Session.GetString("SAM")))
            return RedirectToAction("Edit", "Me");

        return View("Login");
    }

    // ── Step 2: force the Negotiate handshake ─────────────────────────────────
    // The form submits to here first. [Authorize] makes Kestrel do the
    // NTLM/Kerberos challenge before the action body runs, so User.Identity.Name
    // is guaranteed to be populated when we get here.

    [HttpGet("/auth/windows")]
    [Authorize]
    public IActionResult WindowsAuth([FromQuery] string username)
    {
        // Bounce to the confirm endpoint, carrying the typed username
        return RedirectToAction("Confirm", new { username });
    }

    // ── Step 3: verify username and set session ───────────────────────────────

    [HttpGet("/login/confirm")]
    public IActionResult Confirm([FromQuery] string username)
    {
        username = (username ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(username))
        {
            ViewBag.Error = "Please enter your domain username.";
            return View("~/Views/Login/Login.cshtml");
        }

        var windowsName = User.Identity?.Name ?? "";
        var windowsSam  = windowsName.Contains('\\')
            ? windowsName.Split('\\', 2)[1].ToLowerInvariant()
            : windowsName.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(windowsSam))
        {
            ViewBag.Error = "Windows authentication failed. Please ensure you are on the office network.";
            return View("~/Views/Login/Login.cshtml");
        }

        var allowedDomain = (_config["Security:AllowedDomain"] ?? "dbp.local").ToLowerInvariant();
        var windowsDomain = windowsName.Contains('\\')
            ? windowsName.Split('\\', 2)[0].ToLowerInvariant() : "";

        bool domainOk = string.IsNullOrWhiteSpace(windowsDomain) ||
                        allowedDomain.StartsWith(windowsDomain, StringComparison.OrdinalIgnoreCase) ||
                        windowsDomain.EndsWith(allowedDomain.Split('.')[0], StringComparison.OrdinalIgnoreCase);

        if (!domainOk)
        {
            ViewBag.Error = "Access is restricted to DBP domain accounts only.";
            return View("~/Views/Login/Login.cshtml");
        }

        if (username != windowsSam)
        {
            ViewBag.Error = "The username you entered does not match your Windows login.";
            return View("~/Views/Login/Login.cshtml");
        }

        HttpContext.Session.SetString("SAM",         windowsSam);
        HttpContext.Session.SetString("DisplayName", User.Identity?.Name ?? windowsSam);
        return RedirectToAction("Edit", "Me");
    }

    // POST /logout
    [HttpPost("/logout")]
    [ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Index");
    }
}
