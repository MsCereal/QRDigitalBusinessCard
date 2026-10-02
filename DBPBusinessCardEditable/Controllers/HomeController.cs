using DBPBusinessCardEditable.Models;
using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Diagnostics;

namespace DBPBusinessCardEditable.Controllers
{
    public class HomeController : Controller
    {
        private readonly SqliteCardService _cards;

        public HomeController(SqliteCardService cards)
        {
            _cards = cards;
        }

        // GET / — welcome page
        public IActionResult Index() => View("Welcome");

        // GET /card/{token} — public card view (no auth required)
        [HttpGet("/card/{token}")]
        public IActionResult ViewCard(string token, [FromQuery] string? src)
        {
            var profile = _cards.GetByToken(token);
            if (profile == null) return View("NotFound");
            if (!profile.IsEffectivelyActive)
                return View("CardUnavailable", profile);

            var ip       = Request.Headers["X-Forwarded-For"].FirstOrDefault()
                           ?? HttpContext.Connection.RemoteIpAddress?.ToString()
                           ?? "Unknown";
            var ua       = Request.Headers["User-Agent"].ToString();
            var scanType = (src == "qr") ? "qr" : "direct";
            _cards.RecordView(token, ip, ua, scanType);

            return View("Card", profile);
        }

        // GET /check/{empId} — used by Welcome page JS to check if card exists
        [HttpGet("/check/{empId}")]
        public IActionResult Check(string empId)
        {
            var profile = _cards.Get(empId);
            return Json(new { exists = profile != null });
        }

        // GET /download-contact/{token} — vCard download from public card
        [HttpGet("/download-contact/{token}")]
        public IActionResult DownloadContact(string token)
        {
            var p = _cards.GetByToken(token);
            if (p == null) return NotFound();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");
            if (p.ShowName   && !string.IsNullOrWhiteSpace(p.Name))   sb.AppendLine($"FN:{p.Name}");
            if (p.ShowPreferredName && !string.IsNullOrWhiteSpace(p.PreferredName))
                sb.AppendLine($"NICKNAME:{p.PreferredName}");
            if (p.ShowTitle  && !string.IsNullOrWhiteSpace(p.Title))  sb.AppendLine($"TITLE:{p.Title}");
            // Single ORG line — include department as second component when both are shown
            if (p.ShowOrg && !string.IsNullOrWhiteSpace(p.Org))
            {
                var dept = p.ShowDepartment && !string.IsNullOrWhiteSpace(p.Department)
                           ? $";{p.Department}" : "";
                sb.AppendLine($"ORG:{p.Org}{dept}");
            }
            if (p.ShowPhone  && !string.IsNullOrWhiteSpace(p.Phone))  sb.AppendLine($"TEL;TYPE=CELL:{p.Phone}");
            if (p.ShowEmail  && !string.IsNullOrWhiteSpace(p.Email))  sb.AppendLine($"EMAIL:{p.Email}");
            if (p.ShowOffice && !string.IsNullOrWhiteSpace(p.Office)) sb.AppendLine($"ADR;TYPE=WORK:;;{p.Office};;;;");
            if (p.ShowGitHub    && !string.IsNullOrWhiteSpace(p.GitHub))    sb.AppendLine($"URL;TYPE=GitHub:{p.GitHub}");
            if (p.ShowLinkedIn  && !string.IsNullOrWhiteSpace(p.LinkedIn))  sb.AppendLine($"URL;TYPE=LinkedIn:{p.LinkedIn}");
            if (p.ShowPortfolio && !string.IsNullOrWhiteSpace(p.Portfolio)) sb.AppendLine($"URL;TYPE=Portfolio:{p.Portfolio}");
            if (p.ExtraInfo != null)
                foreach (var e in p.ExtraInfo)
                    if (!string.IsNullOrWhiteSpace(e.Value))
                        sb.AppendLine($"X-SOCIALPROFILE;TYPE={e.Label}:{e.Value}");
            if (p.ShowPreferredName && !string.IsNullOrWhiteSpace(p.PreferredName))
                sb.AppendLine($"NICKNAME:{p.PreferredName}");
            sb.AppendLine($"REV:{p.LastUpdated:yyyyMMddTHHmmssZ}");
            sb.AppendLine("END:VCARD");

            var bytes    = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            var filename = string.IsNullOrWhiteSpace(p.Name)
                ? $"{p.EmpId}-DBP.vcf"
                : $"{p.Name.Replace(" ", "-")}-DBP.vcf";
            return File(bytes, "text/vcard", filename);
        }

        // GET /api — redirect to Swagger docs
        [HttpGet("/api")]
        public IActionResult ApiDocs() => Redirect("/swagger");

        // ── Redirect legacy routes to new session-based equivalents ───────────

        // Old /start → /login (new flow starts with login)
        [HttpGet("/start")]
        public IActionResult Start() => RedirectToAction("Index", "Login");

        // Old /setup → /me/edit (session required)
        [HttpGet("/setup")]
        public IActionResult Setup() => Redirect("/me/edit");

        // Old /qr/{token} → /me/qr/{token}
        // Also handles /qr/{empId} from the Welcome page lookup
        [HttpGet("/qr/{token}")]
        public IActionResult QRScreen(string token)
        {
            // First try by token
            var card = _cards.GetByToken(token);
            // Fallback: try by Employee ID (Welcome page passes empId here)
            if (card == null) card = _cards.Get(token);
            if (card == null) return View("NotFound");
            return Redirect($"/me/qr/{card.CardToken}");
        }

        // ── Admin (session-based — key never appears in the URL) ──────────────

        private static string AdminKey =>
            Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "dbpadmin2026";

        private bool IsAdminSession() =>
            HttpContext.Session.GetString("ADMIN") == "1";

        // GET /admin — dashboard (or login page if not authenticated)
        [HttpGet("/admin")]
        public IActionResult Admin()
        {
            if (!IsAdminSession())
                return Content(AdminLoginPage(""), "text/html");
            int pendingCount = _cards.GetPending().Count;
            return Content(AdminDashboard(_cards.Count(), null, pendingCount), "text/html");
        }

        // POST /admin/login — verify password, set session
        [HttpPost("/admin/login")]
        [IgnoreAntiforgeryToken]
        public IActionResult AdminLogin(string password)
        {
            if (password == AdminKey)
            {
                HttpContext.Session.SetString("ADMIN", "1");
                return Redirect("/admin");
            }
            return Content(AdminLoginPage("⚠️ Incorrect password."), "text/html");
        }

        // POST /admin/logout
        [HttpPost("/admin/logout")]
        [IgnoreAntiforgeryToken]
        public IActionResult AdminLogout()
        {
            HttpContext.Session.Remove("ADMIN");
            return Redirect("/admin");
        }

        [HttpPost("/admin/logout-home")]
        [IgnoreAntiforgeryToken]
        public IActionResult AdminLogoutHome()
        {
            HttpContext.Session.Remove("ADMIN");
            return Redirect("/");
        }

        // POST /admin/clear — delete all cards (session required)
        [HttpPost("/admin/clear")]
        [IgnoreAntiforgeryToken]
        public IActionResult AdminClearPost(string action)
        {
            if (!IsAdminSession()) return Redirect("/admin");
            if (action == "clear")
            {
                int deleted = _cards.ClearAll();
                return Content(AdminDashboard(0, $"{deleted} card(s) deleted.", 0), "text/html");
            }
            return Redirect("/admin");
        }

        // Legacy redirect — old bookmarks with ?key= still work but strip the key
        [HttpGet("/admin/clear")]
        public IActionResult AdminClearLegacy(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key) && key == AdminKey)
                HttpContext.Session.SetString("ADMIN", "1");
            return Redirect("/admin");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() =>
            View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });

        private string AdminLoginPage(string error) => $@"<!DOCTYPE html>
<html lang='en'><head><meta charset='utf-8'/><meta name='viewport' content='width=device-width,initial-scale=1.0'/><title>Admin Sign In — DBP</title>
<link rel='icon' type='image/png' href='/dbp-icon.png'/>
<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css'/>
<style>
*,*::before,*::after{{box-sizing:border-box;margin:0;padding:0;}}
body{{
  font-family:Arial,sans-serif;min-height:100vh;
  display:flex;flex-direction:column;align-items:center;
  background:url('/bg.png') center center/cover no-repeat fixed;
  position:relative;
}}
body::before{{
  content:'';position:fixed;inset:0;
  background:rgba(255,255,255,0.72);
  backdrop-filter:blur(2px);
  -webkit-backdrop-filter:blur(2px);
  z-index:0;
}}
.warning-bar{{
  position:relative;z-index:1;
  width:100%;background:rgba(254,226,226,0.95);border-bottom:2px solid #fca5a5;
  padding:14px 32px;text-align:left;
  box-shadow:0 2px 12px rgba(185,28,28,0.12);
}}
.warning-title{{display:flex;align-items:center;gap:8px;color:#b91c1c;font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:0.5px;margin-bottom:5px;}}
.warning-text{{color:#7f1d1d;font-size:11px;line-height:1.65;}}
.page-wrap{{position:relative;z-index:1;display:flex;flex-direction:column;align-items:center;width:100%;max-width:420px;padding:48px 20px 32px;}}
.box{{
  background:rgba(255,255,255,0.88);border:1px solid rgba(226,232,240,0.7);
  border-radius:20px;padding:36px 34px;width:100%;text-align:center;
  box-shadow:0 12px 40px rgba(15,40,100,0.13);border-top:5px solid #2563eb;
  backdrop-filter:blur(6px);-webkit-backdrop-filter:blur(6px);
}}
.logo{{width:60px;margin-bottom:14px;}}
.org{{color:#94a3b8;font-size:11px;letter-spacing:1.5px;text-transform:uppercase;font-weight:700;margin-bottom:16px;}}
h1{{color:#0f172a;font-size:22px;font-weight:800;margin-bottom:6px;}}
.sub{{color:#64748b;font-size:13px;margin-bottom:22px;}}
label{{display:block;text-align:left;color:#374151;font-size:11px;font-weight:700;letter-spacing:0.5px;text-transform:uppercase;margin-bottom:6px;}}
input{{width:100%;padding:13px 16px;background:#f9fafb;border:1.5px solid #e5e7eb;border-radius:11px;color:#0f172a;font-size:15px;outline:none;letter-spacing:2px;text-align:center;margin-bottom:16px;transition:border-color 0.15s,background 0.15s;}}
input:focus{{border-color:#2563eb;background:#fff;box-shadow:0 0 0 3px rgba(37,99,235,0.1);}}
.btn{{width:100%;padding:14px;background:linear-gradient(135deg,#2563eb,#1d4ed8);border:none;border-radius:11px;color:#fff;font-size:15px;font-weight:800;cursor:pointer;box-shadow:0 4px 14px rgba(37,99,235,0.3);transition:box-shadow 0.15s;}}
.btn:hover{{box-shadow:0 6px 20px rgba(37,99,235,0.45);}}
.error{{background:#fee2e2;border:1px solid #fecaca;color:#b91c1c;font-size:13px;font-weight:700;border-radius:9px;padding:10px 14px;margin-bottom:18px;}}
.back{{display:inline-block;margin-top:28px;color:#64748b;font-size:12px;text-decoration:none;}}
.back:hover{{color:#1e40af;}}
.footer-note{{color:#94a3b8;font-size:10px;margin-top:16px;line-height:1.5;}}
</style></head>
<body>

  <div class='warning-bar'>
    <div class='warning-title'><i class='fa-solid fa-triangle-exclamation'></i> Restricted Access</div>
    <p class='warning-text'>
      This is a secure administrative area for <strong>authorized personnel only</strong>.
      Any person who accesses this system without proper consent and authorization is
      in violation of company policy and applicable laws. Unauthorized access may result
      in <strong>disciplinary action, termination of employment, and legal prosecution</strong>.
      All access attempts are logged and monitored.
    </p>
  </div>

  <div class='page-wrap'>
  <div class='box'>
    <img src='/dbp-logo.png' alt='DBP' class='logo'/>
    <p class='org'>Development Bank of the Philippines</p>
    <h1><i class='fa-solid fa-shield-halved' style='margin-right:6px;color:#2563eb;'></i>Admin Console</h1>
    <p class='sub'>Enter your administrator password to continue.</p>
    {(string.IsNullOrEmpty(error) ? "" : $"<div class='error'>{error}</div>")}
    <form method='post' action='/admin/login' autocomplete='off'>
      <label>Administrator Password</label>
      <input type='password' name='password' placeholder='••••••••' autofocus autocomplete='off'/>
      <button type='submit' class='btn'>Sign In <i class='fa-solid fa-arrow-right' style='margin-left:4px;'></i></button>
    </form>
    <form method='post' action='/admin/logout-home' style='text-align:center;margin-top:28px;'>
      <button type='submit' style='background:none;border:none;padding:0;color:#64748b;font-size:12px;cursor:pointer;font-family:inherit;'>← Back to Home</button>
    </form>
    <p class='footer-note'>DBP Digital Business Card System · Administrative Console</p>
  </div>
  </div>

</body></html>";

        private string AdminDashboard(int count, string? message, int pendingCount)
        {
            var reviewBtn = pendingCount > 0
                ? $@"<a href='/admin/review' class='btn btn-review'><i class='fa-solid fa-clock-rotate-left'></i> Review Pending Changes <span class='badge'>{pendingCount}</span></a>"
                : $@"<a href='/admin/review' class='btn btn-review'><i class='fa-solid fa-clock-rotate-left'></i> Review Pending Changes</a>";

            var pendingAlert = pendingCount > 0
                ? $@"<div class='alert-pending'><i class='fa-solid fa-triangle-exclamation'></i> You have <strong>{pendingCount}</strong> change(s) awaiting review.</div>"
                : "";

            return $@"<!DOCTYPE html>
<html lang='en'><head><meta charset='utf-8'/><meta name='viewport' content='width=device-width,initial-scale=1.0'/><title>Admin Console — DBP</title>
<link rel='icon' type='image/png' href='/dbp-icon.png'/>
<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css'/>
<style>
*,*::before,*::after{{box-sizing:border-box;margin:0;padding:0;}}
body{{
  font-family:Arial,sans-serif;min-height:100vh;
  background:url('/bg.png') center center/cover no-repeat fixed;
  position:relative;
}}
body::before{{
  content:'';position:fixed;inset:0;
  background:rgba(255,255,255,0.75);
  backdrop-filter:blur(2px);-webkit-backdrop-filter:blur(2px);
  z-index:0;
}}
.topbar{{position:relative;z-index:1;display:flex;align-items:center;gap:14px;padding:16px 28px;background:linear-gradient(135deg,rgba(30,58,138,0.95),rgba(37,99,235,0.95));}}
.topbar img{{width:38px;height:38px;object-fit:contain;}}
.topbar h1{{color:#fff;font-size:16px;font-weight:800;}}
.topbar .badge-top{{margin-left:auto;}}
.logout{{background:rgba(255,255,255,0.15);border:1px solid rgba(255,255,255,0.25);color:#fff;font-size:12px;font-weight:700;padding:7px 14px;border-radius:20px;cursor:pointer;}}
.logout:hover{{background:rgba(255,255,255,0.28);}}
.wrap{{max-width:460px;margin:40px auto;padding:0 20px;position:relative;z-index:1;}}
.box{{background:rgba(255,255,255,0.88);border:1px solid rgba(226,232,240,0.7);border-radius:20px;padding:32px 28px;text-align:center;box-shadow:0 12px 40px rgba(15,40,100,0.12);border-top:5px solid #2563eb;backdrop-filter:blur(6px);-webkit-backdrop-filter:blur(6px);}}
h2{{color:#0f172a;font-size:20px;font-weight:800;margin-bottom:4px;}}
.sub{{color:#94a3b8;font-size:12px;margin-bottom:24px;}}
.stat{{background:#f8fafc;border:1px solid #e5e7eb;border-radius:14px;padding:22px;margin-bottom:20px;}}
.num{{font-size:52px;font-weight:800;color:#2563eb;line-height:1;}}
.lbl{{color:#64748b;font-size:13px;margin-top:6px;font-weight:600;}}
.success{{background:#dcfce7;border:1px solid #bbf7d0;color:#15803d;font-size:13px;font-weight:700;padding:11px 14px;border-radius:10px;margin-bottom:18px;}}
.alert-pending{{background:#fef3c7;border:1px solid #fde68a;color:#b45309;font-size:13px;font-weight:700;padding:11px 14px;border-radius:10px;margin-bottom:18px;}}
.btn{{display:flex;align-items:center;justify-content:center;gap:8px;width:100%;padding:14px;border-radius:12px;border:none;font-size:14px;font-weight:800;cursor:pointer;text-decoration:none;margin-bottom:10px;position:relative;transition:transform 0.1s,box-shadow 0.1s;}}
.btn:active{{transform:scale(0.98);}}
.btn-review{{background:#f59e0b;color:#fff;box-shadow:0 4px 14px rgba(245,158,11,0.3);}}
.btn-review:hover{{background:#d97706;}}
.btn-d{{background:#fff;border:1.5px solid #fecaca;color:#dc2626;}}
.btn-d:hover{{background:#fee2e2;}}
.btn-h{{background:#f1f5f9;border:1px solid #e2e8f0;color:#334155;}}
.btn-h:hover{{background:#e2e8f0;}}
.badge{{position:absolute;top:-8px;right:14px;background:#dc2626;color:#fff;font-size:12px;font-weight:800;padding:2px 9px;border-radius:12px;border:2px solid #fff;}}
</style></head>
<body>
<div class='topbar'>
<form method='post' action='/admin/logout-home' style='display:flex;align-items:center;'>
  <button type='submit' style='background:none;border:none;padding:0;cursor:pointer;display:flex;align-items:center;'>
    <img src='/dbp-logo.png' alt='DBP' style='width:38px;height:38px;object-fit:contain;'/>
  </button>
</form>
<h1>DBP Admin Console</h1>
<div class='badge-top'><form method='post' action='/admin/logout' style='display:inline;'><button type='submit' class='logout'><i class='fa-solid fa-right-from-bracket'></i> Sign Out</button></form></div>
</div>
<div class='wrap'><div class='box'>
<h2>Dashboard</h2>
<p class='sub'>DBP Digital Business Card System</p>
{(string.IsNullOrEmpty(message) ? "" : $"<div class='success'><i class='fa-solid fa-circle-check'></i> {message}</div>")}
{pendingAlert}
<div class='stat'><div class='num'>{count}</div><div class='lbl'>Cards in Database</div></div>
{reviewBtn}
<form method='post' action='/admin/clear' style='margin-bottom:0;'>
<input type='hidden' name='action' value='clear'/>
<button type='submit' class='btn btn-d' onclick=""return confirm('Delete all {count} cards? This cannot be undone.')""><i class='fa-solid fa-trash'></i> Delete All Cards</button>
</form>
<form method='post' action='/admin/logout-home' style='margin-bottom:0;'>
<button type='submit' class='btn btn-h'><i class='fa-solid fa-house'></i> Back to Home</button>
</form>
</div></div>
</body></html>";
        }
    }
}
