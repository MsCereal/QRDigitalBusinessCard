using DBPBusinessCardEditable.Models;
using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Diagnostics;

namespace DBPBusinessCardEditable.Controllers
{
    public class HomeController : Controller
    {
        private readonly CardProfileService _profileService;

        public HomeController(CardProfileService profileService)
        {
            _profileService = profileService;
        }

        // GET /
        public IActionResult Index() => View("Welcome");

        // GET /start
        [HttpGet("/start")]
        public IActionResult Start() => View("Edit", new CardProfile());

        // GET /card/{token} — public card view
        [HttpGet("/card/{token}")]
        public IActionResult ViewCard(string token)
        {
            var profile = _profileService.GetByToken(token);
            if (profile == null) return View("NotFound");
            if (!profile.IsActive) return View("CardUnavailable", profile);
            // Record analytics view
            _profileService.RecordView(profile.EmpId);
            return View("Card", profile);
        }

        // POST /save
        [HttpPost("/save")]
        [ValidateAntiForgeryToken]
        public IActionResult Save(CardProfile model)
        {
            if (string.IsNullOrWhiteSpace(model.EmpId))
            {
                TempData["Error"] = "Employee ID is required.";
                return View("Edit", model);
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(model.EmpId.Trim(), @"^\d{7}-[A-Za-z]{3}$"))
            {
                TempData["Error"] = "Employee ID must be in the format: 7 digits – 3 letters (e.g. 0000001-DBP).";
                return View("Edit", model);
            }
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                TempData["Error"] = "Full Name is required.";
                return View("Edit", model);
            }

            // PIN validation: if card exists and has a PIN, verify before saving
            var existing = _profileService.Get(model.EmpId.Trim());
            if (existing != null && !string.IsNullOrWhiteSpace(existing.CardPin))
            {
                if (!_profileService.VerifyPin(model.EmpId.Trim(), model.CardPin))
                {
                    TempData["Error"] = "Incorrect PIN. Changes were not saved.";
                    model.CardPin = ""; // clear submitted PIN from form
                    return View("Edit", model);
                }
                // Keep the existing PIN if user left it blank on edit
                if (string.IsNullOrWhiteSpace(model.CardPin))
                    model.CardPin = existing.CardPin;
            }

            // PIN format validation (if setting a new PIN)
            if (!string.IsNullOrWhiteSpace(model.CardPin))
            {
                model.CardPin = model.CardPin.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(model.CardPin, @"^\d{4,6}$"))
                {
                    TempData["Error"] = "PIN must be 4–6 digits only.";
                    return View("Edit", model);
                }
            }

            // Preserve existing token
            if (existing != null && !string.IsNullOrWhiteSpace(existing.CardToken))
                model.CardToken = existing.CardToken;

            _profileService.Save(model);
            return RedirectToAction("QRScreen", new { token = model.CardToken });
        }

        // GET /setup — show PIN gate for existing cards
        [HttpGet("/setup")]
        public IActionResult Setup(string empId)
        {
            if (string.IsNullOrWhiteSpace(empId))
                return View("Edit", new CardProfile());

            var existing = _profileService.Get(empId);

            // New card — go straight to edit
            if (existing == null)
                return View("Edit", new CardProfile { EmpId = empId });

            // Existing card with PIN — require PIN gate unless already verified this session
            if (!string.IsNullOrWhiteSpace(existing.CardPin))
            {
                var verified = TempData.Peek("PinVerified") as string;
                if (verified != empId.Trim())
                    return View("PinGate", existing);
            }

            return View("Edit", existing);
        }

        // POST /verify-pin — PIN gate form submission
        [HttpPost("/verify-pin")]
        [ValidateAntiForgeryToken]
        public IActionResult VerifyPin(string empId, string pin)
        {
            var profile = _profileService.Get(empId);
            if (profile == null) return RedirectToAction("Start");

            if (_profileService.VerifyPin(empId, pin))
            {
                TempData["PinVerified"] = empId.Trim();
                // Return to the originally requested URL if set
                var returnUrl = TempData["ReturnUrl"] as string;
                if (!string.IsNullOrWhiteSpace(returnUrl))
                    return Redirect(returnUrl);
                return RedirectToAction("Setup", new { empId });
            }

            TempData["PinError"] = "Incorrect PIN. Try again.";
            return View("PinGate", profile);
        }

        // GET /stats/{empId} — analytics + audit log for card owner
        [HttpGet("/stats/{empId}")]
        public IActionResult Stats(string empId)
        {
            var profile = _profileService.Get(empId);
            if (profile == null) return View("NotFound");
            if (!string.IsNullOrWhiteSpace(profile.CardPin))
            {
                var verified = TempData.Peek("PinVerified") as string;
                if (verified != empId.Trim())
                {
                    TempData["ReturnUrl"] = $"/stats/{empId}";
                    return View("PinGate", profile);
                }
            }
            ViewBag.Stats = _profileService.GetViewStats(empId);
            return View("Stats", profile);
        }

        // GET /check/{empId}
        [HttpGet("/check/{empId}")]
        public IActionResult Check(string empId)
        {
            var profile = _profileService.Get(empId);
            return Json(new { exists = profile != null });
        }

        // GET /qr/{token}
        [HttpGet("/qr/{token}")]
        public IActionResult QRScreen(string token)
        {
            var profile = _profileService.GetByToken(token) ?? _profileService.Get(token);
            if (profile == null) return RedirectToAction("Start");
            return View("QREntrance", profile);
        }

        // POST /reset/{empId}
        [HttpPost("/reset/{empId}")]
        [ValidateAntiForgeryToken]
        public IActionResult Reset(string empId, string pin)
        {
            var profile = _profileService.Get(empId);
            if (profile == null) return RedirectToAction("Start");

            if (!string.IsNullOrWhiteSpace(profile.CardPin) && !_profileService.VerifyPin(empId, pin))
            {
                TempData["PinError"] = "Incorrect PIN. Reset was cancelled.";
                return View("PinGate", profile);
            }

            _profileService.Reset(empId);
            TempData["Reset"] = true;
            return RedirectToAction("Setup", new { empId });
        }

        // GET /download-contact/{token}
        [HttpGet("/download-contact/{token}")]
        public IActionResult DownloadContact(string token)
        {
            var p = _profileService.GetByToken(token) ?? _profileService.Get(token);
            if (p == null) return NotFound();

            // Build vCard respecting Show* flags
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");
            if (p.ShowName   && !string.IsNullOrWhiteSpace(p.Name))   sb.AppendLine($"FN:{p.Name}");
            if (p.ShowOrg    && !string.IsNullOrWhiteSpace(p.Org))    sb.AppendLine($"ORG:{p.Org}");
            if (p.ShowTitle  && !string.IsNullOrWhiteSpace(p.Title))  sb.AppendLine($"TITLE:{p.Title}");
            if (p.ShowDepartment && !string.IsNullOrWhiteSpace(p.Department)) sb.AppendLine($"ORG:{p.Org};{p.Department}");
            if (p.ShowPhone  && !string.IsNullOrWhiteSpace(p.Phone))  sb.AppendLine($"TEL;TYPE=CELL:{p.Phone}");
            if (p.ShowEmail  && !string.IsNullOrWhiteSpace(p.Email))  sb.AppendLine($"EMAIL:{p.Email}");
            if (p.ShowOffice && !string.IsNullOrWhiteSpace(p.Office)) sb.AppendLine($"ADR;TYPE=WORK:;;{p.Office};;;;");
            if (p.ShowGitHub && !string.IsNullOrWhiteSpace(p.GitHub)) sb.AppendLine($"URL;TYPE=GitHub:{p.GitHub}");
            if (p.ShowLinkedIn  && !string.IsNullOrWhiteSpace(p.LinkedIn))  sb.AppendLine($"URL;TYPE=LinkedIn:{p.LinkedIn}");
            if (p.ShowPortfolio && !string.IsNullOrWhiteSpace(p.Portfolio)) sb.AppendLine($"URL;TYPE=Portfolio:{p.Portfolio}");
            // Extra info items
            if (p.ExtraInfo != null)
                foreach (var e in p.ExtraInfo)
                    if (!string.IsNullOrWhiteSpace(e.Value))
                        sb.AppendLine($"X-SOCIALPROFILE;TYPE={e.Label}:{e.Value}");
            sb.AppendLine($"REV:{p.LastUpdated:yyyyMMddTHHmmssZ}");
            sb.AppendLine("END:VCARD");

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            string filename = string.IsNullOrWhiteSpace(p.Name)
                ? $"{p.EmpId}-DBP.vcf"
                : $"{p.Name.Replace(" ", "-")}-DBP.vcf";
            return File(bytes, "text/vcard", filename);
        }

        // GET /api — redirect to Swagger
        [HttpGet("/api")]
        public IActionResult ApiDocs() => Redirect("/swagger");

        // GET /admin/clear
        [HttpGet("/admin/clear")]
        public IActionResult AdminClear(string key)
        {
            string adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "dbpadmin2026";
            if (key != adminKey) return Content(AdminLoginPage(""), "text/html");
            return Content(AdminDashboard(_profileService.Count(), adminKey, null), "text/html");
        }

        // POST /admin/clear
        [HttpPost("/admin/clear")]
        public IActionResult AdminClearPost(string key, string action)
        {
            string adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "dbpadmin2026";
            if (key != adminKey) return Content(AdminLoginPage("Invalid password."), "text/html");
            if (action == "clear")
            {
                int deleted = _profileService.ClearAll();
                return Content(AdminDashboard(0, adminKey, $"{deleted} card(s) deleted."), "text/html");
            }
            return RedirectToAction("AdminClear", new { key });
        }

        // POST /admin/login
        [HttpPost("/admin/login")]
        public IActionResult AdminLogin(string password)
        {
            string adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "dbpadmin2026";
            if (password == adminKey) return Redirect($"/admin/clear?key={adminKey}");
            return Content(AdminLoginPage("⚠️ Incorrect password."), "text/html");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() =>
            View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });

        private string AdminLoginPage(string error) => $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'/><title>Admin</title>
<style>*{{box-sizing:border-box;margin:0;padding:0;}}body{{font-family:Arial,sans-serif;background:#0f172a;display:flex;align-items:center;justify-content:center;min-height:100vh;padding:20px;}}.box{{background:#1e293b;border:1px solid #334155;border-radius:16px;padding:36px 28px;max-width:360px;width:100%;text-align:center;}}h2{{color:#f1f5f9;font-size:20px;font-weight:800;margin-bottom:16px;}}input{{width:100%;padding:12px 14px;border:1.5px solid #334155;border-radius:10px;background:#0f172a;color:#f1f5f9;font-size:15px;outline:none;margin-bottom:12px;text-align:center;letter-spacing:2px;}}input:focus{{border-color:#2563eb;}}.btn{{width:100%;padding:13px;border:none;border-radius:10px;background:#2563eb;color:#fff;font-size:15px;font-weight:700;cursor:pointer;}}.error{{color:#fca5a5;font-size:13px;margin-bottom:14px;}}</style></head>
<body><div class='box'><h2>🔐 Admin Access</h2>{(string.IsNullOrEmpty(error) ? "" : $"<p class='error'>{error}</p>")}<form method='post' action='/admin/login'><input type='password' name='password' placeholder='Enter admin password' autofocus/><button type='submit' class='btn'>Unlock</button></form></div></body></html>";

        private string AdminDashboard(int count, string key, string message) => $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'/><title>Admin Dashboard</title>
<style>*{{box-sizing:border-box;margin:0;padding:0;}}body{{font-family:Arial,sans-serif;background:#0f172a;display:flex;align-items:center;justify-content:center;min-height:100vh;padding:20px;}}.box{{background:#1e293b;border:1px solid #334155;border-radius:16px;padding:36px 28px;max-width:400px;width:100%;text-align:center;}}h2{{color:#f1f5f9;font-size:20px;font-weight:800;margin-bottom:4px;}}.sub{{color:#64748b;font-size:12px;margin-bottom:22px;}}.stat{{background:#0f172a;border:1px solid #334155;border-radius:12px;padding:18px;margin-bottom:22px;}}.num{{font-size:48px;font-weight:800;color:#60a5fa;line-height:1;}}.lbl{{color:#64748b;font-size:13px;margin-top:4px;}}.success{{background:rgba(22,163,74,0.15);color:#4ade80;font-size:13px;padding:10px 14px;border-radius:8px;margin-bottom:18px;}}.btn{{display:block;width:100%;padding:13px;border-radius:10px;border:none;font-size:14px;font-weight:700;cursor:pointer;text-decoration:none;text-align:center;margin-bottom:8px;}}.btn-d{{background:#dc2626;color:#fff;}}.btn-h{{background:#334155;color:#f1f5f9;}}</style></head>
<body><div class='box'><h2>Admin Dashboard</h2><p class='sub'>DBP Digital Business Card</p>{(string.IsNullOrEmpty(message) ? "" : $"<div class='success'>✅ {message}</div>")}<div class='stat'><div class='num'>{count}</div><div class='lbl'>Cards in database</div></div><form method='post' action='/admin/clear?key={key}' style='margin-bottom:8px;'><input type='hidden' name='key' value='{key}'/><input type='hidden' name='action' value='clear'/><button type='submit' class='btn btn-d' onclick=""return confirm('Delete all {count} cards?')"">🗑 Delete All Cards</button></form><a href='/' class='btn btn-h'>← Back to Home</a></div></body></html>";
    }
}
