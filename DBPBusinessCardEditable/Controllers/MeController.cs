using System.Security.Claims;
using DBPBusinessCardEditable.Data;
using DBPBusinessCardEditable.Filters;
using DBPBusinessCardEditable.Models;
using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DBPBusinessCardEditable.Controllers;

/// <summary>
/// Handles the authenticated employee's own card — edit, save, publish.
/// All actions require a valid login session ([SessionRequired]).
/// </summary>
[SessionRequired]
public class MeController(
    SqliteCardService cards,
    LdapDirectoryService directory,
    PublishService publisher,
    EmailService email) : Controller
{
    // GET /me — redirect to edit
    [HttpGet("/me")]
    public IActionResult Index() => RedirectToAction("Edit");

    // GET /me/edit
    [HttpGet("/me/edit")]
    public IActionResult Edit()
    {
        var sam     = HttpContext.Session.GetString("SAM")!;
        var profile = cards.GetBySam(sam);

        if (profile is not null)
            return View("~/Views/Home/Edit.cshtml", profile);

        // New card — pre-fill from AD server-side (no client fetch needed)
        profile = new CardProfile { SamAccountName = sam, IsActive = true };

        if (directory.IsConfigured)
        {
            var adUser = directory.Find(sam, null);
            if (adUser is not null)
            {
                profile.ApplyFromDirectory(adUser);
                TempData["AdPrefilled"] = "true";
            }
        }

        TempData["IsNewCard"] = "true";
        return View("~/Views/Home/Edit.cshtml", profile);
    }

    // POST /me/save
    [HttpPost("/me/save")]
    [ValidateAntiForgeryToken]
    public IActionResult Save(CardProfile model, bool resetPin = false,
        [FromForm(Name = "CardExpiryInput")] string? cardExpiryInput = null)
    {
        var sam = HttpContext.Session.GetString("SAM")!;
        model.SamAccountName = sam;

        // ── Parse ATM expiry (MM/YY) → store as end-of-month UTC DateTime ──
        if (!string.IsNullOrWhiteSpace(cardExpiryInput))
        {
            var parts = cardExpiryInput.Trim().Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var mm) && mm >= 1 && mm <= 12
                && int.TryParse(parts[1], out var yy))
            {
                var fullYear = yy < 100 ? 2000 + yy : yy;
                // Build end-of-month in PHT (UTC+8), then convert to UTC properly
                var phtZone     = TimeZoneInfo.GetSystemTimeZones()
                                      .FirstOrDefault(z => z.Id == "Asia/Manila"
                                                        || z.Id == "Singapore Standard Time")
                                  ?? TimeZoneInfo.Utc;
                var lastDayPht  = new DateTime(fullYear, mm,
                                      DateTime.DaysInMonth(fullYear, mm),
                                      23, 59, 59,
                                      DateTimeKind.Unspecified);
                model.CardExpiresAt = TimeZoneInfo.ConvertTimeToUtc(lastDayPht, phtZone);
            }
            else
            {
                TempData["Error"] = "ATM Card Expiry must be in MM/YY format (e.g. 12/28).";
                return View("~/Views/Home/Edit.cshtml", model);
            }
        }
        else
        {
            // Keep existing expiry if field left blank
            var existing0 = cards.GetBySam(sam);
            if (existing0 is not null) model.CardExpiresAt = existing0.CardExpiresAt;
        }

        // ── Validation ──────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(model.EmpId))
        {
            TempData["Error"] = "Employee ID is required.";
            return View("~/Views/Home/Edit.cshtml", model);
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(
            model.EmpId.Trim(), @"^\d{7}-[A-Za-z]{3}$"))
        {
            TempData["Error"] = "Employee ID must be in the format: 7 digits – 3 letters (e.g. 0000001-DBP).";
            return View("~/Views/Home/Edit.cshtml", model);
        }
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            TempData["Error"] = "Full Name is required.";
            return View("~/Views/Home/Edit.cshtml", model);
        }
        // EmpId uniqueness — no one else can have the same EmpId
        if (cards.EmpIdExists(model.EmpId.Trim(), sam))
        {
            TempData["Error"] = "That Employee ID is already registered to another account.";
            return View("~/Views/Home/Edit.cshtml", model);
        }

        // ── PIN handling ─────────────────────────────────────────────────────
        var existing = cards.GetBySam(sam);

        if (existing is not null && !string.IsNullOrWhiteSpace(existing.CardPin))
        {
            if (!string.IsNullOrWhiteSpace(model.CardPin))
            {
                // User entered a PIN — verify it against the stored hash
                if (!cards.VerifyPin(sam, model.CardPin))
                {
                    TempData["Error"] = "Incorrect PIN. Changes were not saved.";
                    model.CardPin = "";
                    return View("~/Views/Home/Edit.cshtml", model);
                }
            }
            else if (!resetPin)
            {
                // User left PIN field blank — keep the existing hashed PIN unchanged
                model.CardPin     = existing.CardPin;
                model.PinIsHashed = existing.PinIsHashed;
            }
        }

        if (!string.IsNullOrWhiteSpace(model.CardPin) && !model.PinIsHashed)
        {
            model.CardPin     = model.CardPin.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(model.CardPin, @"^\d{4,6}$"))
            {
                TempData["Error"] = "PIN must be 4–6 digits only.";
                return View("~/Views/Home/Edit.cshtml", model);
            }
            // DatabaseService.Save() will hash it
        }

        // ── Persist ──────────────────────────────────────────────────────────
        var existing2 = cards.GetBySam(sam); // re-fetch after PIN check

        // ── Sensitive field check — Title, Department, Org, Office ────────────
        // If any of these changed from the currently approved value, route to
        // pending approval instead of saving directly.
        bool sensitiveChanged =
            existing2 is not null && (
                (model.Title      ?? "") != (existing2.Title      ?? "") ||
                (model.Department ?? "") != (existing2.Department ?? "") ||
                (model.Org        ?? "") != (existing2.Org        ?? "") ||
                (model.Office     ?? "") != (existing2.Office     ?? ""));

        if (sensitiveChanged)
        {
            // Capture submitted sensitive values before we override them
            var pendTitle  = model.Title      ?? "";
            var pendDept   = model.Department ?? "";
            var pendOrg    = model.Org        ?? "";
            var pendOffice = model.Office     ?? "";

            // Save personal/contact fields immediately — keep existing approved values
            model.Title      = existing2!.Title;
            model.Department = existing2.Department;
            model.Org        = existing2.Org;
            model.Office     = existing2.Office;
            cards.Save(model);

            // Store pending values for admin review
            cards.SubmitPending(sam, pendTitle, pendDept, pendOrg, pendOffice);

            // Email admin
            var savedCard = cards.GetBySam(sam)!;
            var reviewUrl = $"{Request.Scheme}://{Request.Host}/admin/review?key={System.Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "dbpadmin2026"}";
            email.SendPendingReviewNotification(
                savedCard.Name, sam,
                existing2!.Title,      pendTitle,
                existing2.Department,  pendDept,
                existing2.Org,         pendOrg,
                existing2.Office,      pendOffice,
                reviewUrl);

            TempData["PendingSubmitted"] = "true";

            if (savedCard.IsPublished)
            {
                try { publisher.Publish(savedCard); }
                catch { /* non-blocking */ }
            }

            return Redirect($"/me/qr/{savedCard.CardToken}");
        }

        // No sensitive changes — save everything directly
        cards.Save(model);

        // Re-publish automatically if previously published
        var saved = cards.GetBySam(sam)!;
        if (saved.IsPublished)
        {
            try { publisher.Publish(saved); }
            catch { /* log but don't block save */ }
        }

        return Redirect($"/me/qr/{saved.CardToken}");
    }

    // GET /me/qr/{token}
    [HttpGet("/me/qr/{token}")]
    public IActionResult QRScreen(string token)
    {
        var sam  = HttpContext.Session.GetString("SAM")!;
        var card = cards.GetBySam(sam);
        if (card is null || card.CardToken != token) return RedirectToAction("Edit");
        return View("~/Views/Home/QREntrance.cshtml", card);
    }

    // POST /me/publish
    [HttpPost("/me/publish")]
    [ValidateAntiForgeryToken]
    public IActionResult Publish()
    {
        var sam  = HttpContext.Session.GetString("SAM")!;
        var card = cards.GetBySam(sam);
        if (card is null) return RedirectToAction("Edit");

        // Block publishing inactive cards
        if (!card.IsActive)
        {
            TempData["Error"] = "Your card is inactive. Activate it first before publishing.";
            return Redirect($"/me/qr/{card.CardToken}");
        }

        // Block publishing empty cards (after reset)
        if (string.IsNullOrWhiteSpace(card.Name))
        {
            TempData["Error"] = "Your card has no content. Fill in your details before publishing.";
            return Redirect($"/me/qr/{card.CardToken}");
        }

        try
        {
            publisher.Publish(card);
            cards.MarkPublished(sam, true);
            card = cards.GetBySam(sam)!;
            var pubUrl = publisher.GetPublicUrl(card.CardToken);
            // Fallback to local served URL if no public base URL configured
            if (string.IsNullOrWhiteSpace(pubUrl) || pubUrl.StartsWith("/"))
                pubUrl = $"{Request.Scheme}://{Request.Host}/published/{card.CardToken}.html";
            TempData["Published"] = pubUrl;
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Publish failed: {ex.Message} — Check that the output folder is accessible.";
        }

        return Redirect($"/me/qr/{card.CardToken}");
    }

    // POST /me/reset
    [HttpPost("/me/reset")]
    [ValidateAntiForgeryToken]
    public IActionResult Reset(string? pin)
    {
        var sam  = HttpContext.Session.GetString("SAM")!;
        var card = cards.GetBySam(sam);
        if (card is null) return RedirectToAction("Edit");

        if (!string.IsNullOrWhiteSpace(card.CardPin) && !cards.VerifyPin(sam, pin ?? ""))
        {
            TempData["Error"] = "Incorrect PIN. Reset was cancelled.";
            return Redirect($"/me/qr/{card.CardToken}");
        }

        var token = card.CardToken; // preserve token before reset
        cards.Reset(sam);
        TempData["Reset"] = "true";
        // Redirect back to QR screen so user sees the reset confirmation
        return Redirect($"/me/qr/{token}");
    }

    // GET /me/stats
    [HttpGet("/me/stats")]
    public IActionResult Stats()
    {
        var sam  = HttpContext.Session.GetString("SAM")!;
        var card = cards.GetBySam(sam);
        if (card is null) return RedirectToAction("Edit");
        ViewBag.Stats = cards.GetViewStats(sam);
        return View("~/Views/Home/Stats.cshtml", card);
    }

    // ── /api/me endpoints (Negotiate auth, JSON) ─────────────────────────────

    /// <summary>Returns live card stats for the signed-in user's card.</summary>
    [HttpGet("/api/me/stats")]
    [SessionRequired]
    public IActionResult GetStats()
    {
        var sam  = HttpContext.Session.GetString("SAM")!;
        var card = cards.GetBySam(sam);
        if (card is null) return NotFound();
        var (today, week, month, total) = cards.GetViewStats(sam);
        return Ok(new { today, week, month, total, totalViews = total });
    }

    /// <summary>Returns the signed-in user's AD data. Used by Edit page JS for pre-fill.</summary>
    [HttpGet("/api/me")]
    [Authorize]
    public IActionResult GetMe()
    {
        var (sam, upn) = ReadIdentity();
        var user = directory.Find(sam, upn);
        return Ok(new
        {
            samAccountName     = sam,
            userPrincipalName  = upn,
            authenticationType = User.Identity?.AuthenticationType,
            directoryConfigured = directory.IsConfigured,
            found              = user is not null,
            displayName        = user?.DisplayName,
            title              = user?.Title,
            department         = user?.Department,
            mail               = user?.Mail,
            telephoneNumber    = user?.TelephoneNumber,
            hasPhoto           = user?.ThumbnailPhoto is { Length: > 0 }
        });
    }

    [HttpGet("/api/me/photo")]
    [Authorize]
    public IActionResult GetMyPhoto()
    {
        var (sam, upn) = ReadIdentity();
        var photo = directory.Find(sam, upn)?.ThumbnailPhoto;
        return photo is { Length: > 0 }
            ? File(photo, "image/jpeg")
            : NotFound(new { error = "No photo in Active Directory for this account." });
    }

    private (string? Sam, string? Upn) ReadIdentity()
    {
        var name = User.Identity?.Name;
        var sam  = name is not null && name.Contains('\\')
            ? name.Split('\\', 2)[1] : name;
        var upn  = User.FindFirst(ClaimTypes.Upn)?.Value;
        if (upn is null && name is not null && name.Contains('@')) upn = name;
        return (sam, upn);
    }
}
