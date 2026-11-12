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
    // Admin approval for Title/Department/Org/Office edits.
    // false = users edit freely, admin review is idle (code and pages kept intact).
    // Set to true to bring the approval workflow back.
    private const bool RequireAdminApproval = false;

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
        {
            // PIN is optional. Only cards that HAVE a PIN ask for it, and they ask BEFORE the form opens.
            if (!string.IsNullOrWhiteSpace(profile.CardPin))
            {
                if (!PinSession.IsUnlocked(HttpContext.Session, sam))
                    return View("~/Views/Home/PinGate.cshtml", profile);
                PinSession.Unlock(HttpContext.Session, sam);   // keep the unlock fresh while they work
            }
            return View("~/Views/Home/Edit.cshtml", profile);
        }

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

    // POST /me/pin — enter the PIN to open the edit form (only for cards that have a PIN)
    [HttpPost("/me/pin")]
    [ValidateAntiForgeryToken]
    public IActionResult UnlockWithPin(string? pin)
    {
        var sam  = HttpContext.Session.GetString("SAM")!;
        var card = cards.GetBySam(sam);
        if (card is null || string.IsNullOrWhiteSpace(card.CardPin))
            return RedirectToAction("Edit");               // no PIN on this card: nothing to enter

        var entered = (pin ?? "").Trim();
        if (PinSession.TryVerify(sam, () => cards.VerifyPin(sam, entered), out var wait))
        {
            PinSession.Unlock(HttpContext.Session, sam);
            return RedirectToAction("Edit");
        }
        TempData["PinError"] = wait > TimeSpan.Zero
            ? PinSession.LockoutMessage(wait)
            : "Incorrect PIN. Please try again.";
        return View("~/Views/Home/PinGate.cshtml", card);
    }

    // POST /me/save
    [HttpPost("/me/save")]
    [ValidateAntiForgeryToken]
    public IActionResult Save(CardProfile model, bool resetPin = false,
        [FromForm(Name = "CardExpiryInput")] string? cardExpiryInput = null,
        [FromForm(Name = "NewPin")] string? newPin = null)
    {
        var sam = HttpContext.Session.GetString("SAM")!;
        model.SamAccountName = sam;

        // ── Parse ATM expiry (MM/YY) → store as end-of-month UTC DateTime ──
        if (!string.IsNullOrWhiteSpace(cardExpiryInput))
        {
            var parts = cardExpiryInput.Trim().Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var mm) && mm >= 1 && mm <= 12
                && parts[1].Trim().Length == 2                       // two-digit year only (e.g. 28)
                && int.TryParse(parts[1], out var yy) && yy >= 0 && yy <= 99)
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
                return ShowFormAgain(model, "ATM Card Expiry must be in MM/YY format (e.g. 12/28).");
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
            return ShowFormAgain(model, "Employee ID is required.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(
            model.EmpId.Trim(), @"^\d{7}-[A-Za-z]{3}$"))
        {
            return ShowFormAgain(model, "Employee ID must be in the format: 7 digits – 3 letters (e.g. 0000001-DBP).");
        }
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return ShowFormAgain(model, "Full Name is required.");
        }
        // EmpId uniqueness — no one else can have the same EmpId
        if (cards.EmpIdExists(model.EmpId.Trim(), sam))
        {
            return ShowFormAgain(model, "That Employee ID is already registered to another account.");
        }

        // ── Links: only real web addresses ("github.com/jcruz" becomes "https://github.com/jcruz") ──
        if (!SafeLinks.TryNormalizeWebUrl(model.GitHub, out var gitHub))
            return ShowFormAgain(model, "GitHub must be a web address, for example https://github.com/yourname");
        if (!SafeLinks.TryNormalizeWebUrl(model.LinkedIn, out var linkedIn))
            return ShowFormAgain(model, "LinkedIn must be a web address, for example https://www.linkedin.com/in/yourname");
        if (!SafeLinks.TryNormalizeWebUrl(model.Portfolio, out var portfolio))
            return ShowFormAgain(model, "Website must be a web address, for example https://www.example.com");
        model.GitHub = gitHub; model.LinkedIn = linkedIn; model.Portfolio = portfolio;

        // ── Photo: an uploaded image, one of the two avatars, or none ──
        var photo = (model.Photo ?? "").Trim();
        bool photoOk = photo.Length == 0 || photo == "avatar:male" || photo == "avatar:female" ||
                       System.Text.RegularExpressions.Regex.IsMatch(photo, @"^data:image/(jpeg|jpg|png|webp|gif);base64,[A-Za-z0-9+/=\s]+$");
        if (!photoOk) photo = "";
        if (photo.Length > 2_000_000)
            return ShowFormAgain(model, "The photo is too large. Please choose a smaller picture.");
        model.Photo = photo;

        // ── PIN handling ─────────────────────────────────────────────────────
        // The PIN is OPTIONAL. Employees can have none, set one, change it, or turn it off.
        //   model.CardPin = the PIN the user typed to confirm (their CURRENT PIN)
        //   newPin        = a new PIN to set or change to
        //   resetPin      = true when the user turned the PIN off
        // Turning off or changing an existing PIN requires the current PIN.
        var existing = cards.GetBySam(sam);
        bool hasPin  = existing is not null && !string.IsNullOrWhiteSpace(existing.CardPin);
        var current  = (model.CardPin ?? "").Trim();
        var newPinV  = (newPin ?? "").Trim();

        IActionResult PinError(string message)
        {
            return ShowFormAgain(model, message);
        }

        if (hasPin)
        {
            bool verified = false;
            if (current.Length > 0)
            {
                // Counted on the server per employee, BEFORE checking (see AttemptLimiter)
                verified = PinSession.TryVerify(sam, () => cards.VerifyPin(sam, current), out var wait);
                if (!verified)
                    return PinError(wait > TimeSpan.Zero
                        ? PinSession.LockoutMessage(wait)
                        : "Incorrect PIN. Changes were not saved.");
                PinSession.Unlock(HttpContext.Session, sam);
            }

            // A card with a PIN can only be saved after the PIN was entered (on the Edit gate, or here)
            if (!PinSession.IsUnlocked(HttpContext.Session, sam))
                return PinError("Enter your PIN to save changes.");

            if (resetPin)                    // turn the PIN off
            {
                if (!verified) return PinError("Enter your current PIN to turn the PIN off.");
                model.CardPin     = "";
                model.PinIsHashed = false;
            }
            else if (newPinV.Length > 0)     // change the PIN
            {
                if (!verified) return PinError("Enter your current PIN to change it.");
                model.CardPin     = newPinV;
                model.PinIsHashed = false;
            }
            else                             // no PIN change — keep the stored (hashed) PIN
            {
                model.CardPin     = existing!.CardPin;
                model.PinIsHashed = existing.PinIsHashed;
            }
        }
        else
        {
            // No PIN yet — optional. A new PIN arrives as NewPin (or the legacy CardPin field).
            model.CardPin     = newPinV.Length > 0 ? newPinV : current;
            model.PinIsHashed = false;
        }

        if (!string.IsNullOrWhiteSpace(model.CardPin) && !model.PinIsHashed)
        {
            model.CardPin = model.CardPin.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(model.CardPin, @"^\d{4,6}$"))
                return PinError("PIN must be 4–6 digits only.");
            // DatabaseService.Save() will hash it
        }

        // They know the PIN (just set, changed or confirmed it): don't ask again on the very next Edit
        if (!string.IsNullOrWhiteSpace(model.CardPin))
            PinSession.Unlock(HttpContext.Session, sam);

        // ── Persist ──────────────────────────────────────────────────────────
        var existing2 = cards.GetBySam(sam); // re-fetch after PIN check

        // ── Sensitive field check — Title, Department, Org, Office ────────────
        // If any of these changed from the currently approved value, route to
        // pending approval instead of saving directly.
        bool sensitiveChanged =
            RequireAdminApproval &&
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
            // The admin signs in on /admin first; the password is never put in a link or an email
            var reviewUrl = $"{Request.Scheme}://{Request.Host}/admin/review";
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

    /// <summary>
    /// Shows the edit form again with an error message. Restores the stored card details the form
    /// relies on (token, PIN state, created date) so an existing card still looks like itself:
    /// Employee ID locked, PIN switch in the right position, Reset button visible.
    /// The PIN the user typed is never echoed back.
    /// </summary>
    private IActionResult ShowFormAgain(CardProfile model, string message)
    {
        TempData["Error"] = message;
        var stored = cards.GetBySam(model.SamAccountName);
        model.CardToken   = stored?.CardToken ?? "";
        model.CardPin     = stored?.CardPin ?? "";
        model.PinIsHashed = stored?.PinIsHashed ?? false;
        if (stored is not null)
        {
            model.CreatedAt   = stored.CreatedAt;
            model.IsPublished = stored.IsPublished;
            if (string.IsNullOrWhiteSpace(model.EmpId)) model.EmpId = stored.EmpId;
        }
        return View("~/Views/Home/Edit.cshtml", model);
    }

    // GET /me/qr — the signed-in employee's own QR page (used by the Welcome page "Already have a card?")
    [HttpGet("/me/qr")]
    public IActionResult MyQr()
    {
        var sam  = HttpContext.Session.GetString("SAM")!;
        var card = cards.GetBySam(sam);
        if (card is null || string.IsNullOrWhiteSpace(card.CardToken)) return RedirectToAction("Edit");
        return Redirect($"/me/qr/{card.CardToken}");
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

        if (!string.IsNullOrWhiteSpace(card.CardPin))
        {
            var entered = (pin ?? "").Trim();
            if (!PinSession.TryVerify(sam, () => cards.VerifyPin(sam, entered), out var wait))
            {
                TempData["Error"] = wait > TimeSpan.Zero
                    ? PinSession.LockoutMessage(wait)
                    : "Incorrect PIN. Reset was cancelled.";
                return Redirect($"/me/qr/{card.CardToken}");
            }
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
