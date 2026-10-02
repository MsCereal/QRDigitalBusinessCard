using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Mvc;

namespace DBPBusinessCardEditable.Controllers;

/// <summary>
/// Admin review page for pending card field changes.
/// Protected by ADMIN_KEY query param — same pattern as the existing admin dashboard.
/// Routes:
///   GET  /admin/review?key=xxx           — list all pending changes
///   POST /admin/review/approve?key=xxx   — approve a pending change
///   POST /admin/review/reject?key=xxx    — reject a pending change
/// </summary>
public class AdminReviewController(
    SqliteCardService cards,
    EmailService email,
    PublishService publisher) : Controller
{
    private bool IsAdminSession() => HttpContext.Session.GetString("ADMIN") == "1";

    // GET /admin/review
    [HttpGet("/admin/review")]
    public IActionResult Index()
    {
        if (!IsAdminSession())
            return Redirect("/admin");

        var pending = cards.GetPending();
        return Content(ReviewPage(pending, null), "text/html");
    }

    // POST /admin/review/approve
    [HttpPost("/admin/review/approve")]
    [IgnoreAntiforgeryToken]
    public IActionResult Approve([FromForm] string sam)
    {
        if (!IsAdminSession()) return Redirect("/admin");

        var card = cards.GetBySam(sam);
        if (card is null) return Redirect("/admin/review");

        cards.ApprovePending(sam);

        // Re-publish if card was published
        var updated = cards.GetBySam(sam);
        if (updated?.IsPublished == true)
        {
            try { publisher.Publish(updated); } catch { /* non-blocking */ }
        }

        // Notify employee
        if (!string.IsNullOrWhiteSpace(card.Email))
            email.SendApprovalNotification(card.Email, card.Name);

        TempData["ReviewMsg"] = $"✅ Approved changes for {card.Name}.";
        return Redirect("/admin/review");
    }

    // POST /admin/review/reject
    [HttpPost("/admin/review/reject")]
    [IgnoreAntiforgeryToken]
    public IActionResult Reject([FromForm] string sam, [FromForm] string? reason)
    {
        if (!IsAdminSession()) return Redirect("/admin");

        var card = cards.GetBySam(sam);
        if (card is null) return Redirect("/admin/review");

        cards.RejectPending(sam, reason ?? "");

        // Notify employee
        if (!string.IsNullOrWhiteSpace(card.Email))
            email.SendRejectionNotification(card.Email, card.Name, reason ?? "");

        TempData["ReviewMsg"] = $"❌ Rejected changes for {card.Name}.";
        return Redirect("/admin/review");
    }

    // ── HTML ─────────────────────────────────────────────────────────────────

    private string ReviewPage(List<DBPBusinessCardEditable.Models.CardProfile> pending, string? msg)
    {
        var flash  = TempData["ReviewMsg"] as string ?? msg ?? "";
        var rows   = new System.Text.StringBuilder();

        if (pending.Count == 0)
        {
            rows.Append(@"<tr><td colspan='6' style='text-align:center;padding:40px;color:#94a3b8;font-size:14px;'>
                <i class='fa-solid fa-circle-check' style='font-size:32px;color:#86efac;display:block;margin-bottom:10px;'></i>
                No pending changes. All caught up!
            </td></tr>");
        }
        else
        {
            foreach (var p in pending)
            {
                // Convert submitted time to PHT
                var phtZone = TimeZoneInfo.GetSystemTimeZones()
                    .FirstOrDefault(z => z.Id == "Asia/Manila" || z.Id == "Singapore Standard Time")
                    ?? TimeZoneInfo.Utc;
                var submittedAt = p.PendingSubmittedAt.HasValue
                    ? TimeZoneInfo.ConvertTimeFromUtc(
                          DateTime.SpecifyKind(p.PendingSubmittedAt.Value, DateTimeKind.Utc), phtZone)
                          .ToString("MMM d, yyyy h:mm tt") + " PHT"
                    : "—";
                rows.Append($@"
                <tr>
                    <td style='padding:14px 16px;'>
                        <div style='font-weight:800;color:#0f172a;font-size:14px;'>{Esc(p.Name)}</div>
                        <div style='color:#64748b;font-size:11px;'>{Esc(p.SamAccountName)} · {Esc(p.EmpId)}</div>
                        <div style='color:#94a3b8;font-size:11px;margin-top:2px;'>{Esc(p.Email)}</div>
                    </td>
                    <td style='padding:14px 16px;'>
                        {DiffCell(p.Title, p.PendingTitle, "Job Title")}
                        {DiffCell(p.Department, p.PendingDepartment, "Department")}
                        {DiffCell(p.Org, p.PendingOrg, "Organization")}
                        {DiffCell(p.Office, p.PendingOffice, "Office")}
                    </td>
                    <td style='padding:14px 16px;color:#94a3b8;font-size:12px;'>{Esc(submittedAt)}</td>
                    <td style='padding:14px 16px;'>
                        <form method='post' action='/admin/review/approve' style='display:inline;margin-right:6px;'>
                            <input type='hidden' name='sam' value='{Esc(p.SamAccountName)}'/>
                            <button type='submit' style='background:#059669;color:#fff;border:none;border-radius:8px;padding:8px 16px;font-size:12px;font-weight:700;cursor:pointer;'
                                    onclick=""return confirm('Approve changes for {Esc(p.Name)}?')"">
                                ✅ Approve
                            </button>
                        </form>
                        <button onclick=""openReject('{Esc(p.SamAccountName)}','{Esc(p.Name)}')"" 
                                style='background:#dc2626;color:#fff;border:none;border-radius:8px;padding:8px 16px;font-size:12px;font-weight:700;cursor:pointer;'>
                            ❌ Reject
                        </button>
                    </td>
                </tr>");
            }
        }

        return $@"<!DOCTYPE html>
<html lang='en'>
<head>
  <meta charset='utf-8'/>
  <meta name='viewport' content='width=device-width,initial-scale=1.0'/>
  <title>Admin Review — DBP BizCard</title>
  <link rel='icon' type='image/png' href='/dbp-icon.png'/>
  <link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css'/>
  <style>
    *,*::before,*::after{{box-sizing:border-box;margin:0;padding:0}}
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
    .topbar{{position:relative;z-index:1;background:linear-gradient(135deg,rgba(30,58,138,0.95),rgba(37,99,235,0.95));padding:16px 32px;display:flex;align-items:center;gap:16px}}
    .topbar img{{width:40px;height:40px;object-fit:contain}}
    .topbar h1{{color:#fff;font-size:18px;font-weight:800}}
    .topbar .sub{{color:rgba(255,255,255,0.6);font-size:12px;margin-left:auto}}
    .container{{position:relative;z-index:1;max-width:1100px;margin:0 auto;padding:32px 24px}}
    .flash{{padding:12px 18px;border-radius:10px;font-size:14px;font-weight:700;margin-bottom:24px}}
    .flash-ok{{background:#dcfce7;color:#14532d;border:1px solid #bbf7d0}}
    .flash-err{{background:#fee2e2;color:#7f1d1d;border:1px solid #fecaca}}
    .card{{background:rgba(255,255,255,0.90);border-radius:16px;box-shadow:0 2px 12px rgba(0,0,0,0.10);overflow:hidden;backdrop-filter:blur(6px);-webkit-backdrop-filter:blur(6px);}}
    .card-header{{padding:18px 24px;border-bottom:1px solid #f1f5f9;display:flex;align-items:center;justify-content:space-between}}
    .card-header h2{{font-size:16px;font-weight:800;color:#0f172a}}
    .badge{{display:inline-flex;align-items:center;gap:6px;background:#fef3c7;color:#92400e;border:1px solid #fde68a;border-radius:20px;padding:4px 12px;font-size:12px;font-weight:700}}
    table{{width:100%;border-collapse:collapse}}
    thead tr{{background:#f8fafc;border-bottom:2px solid #e2e8f0}}
    thead th{{padding:10px 16px;text-align:left;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.5px;color:#64748b}}
    tbody tr{{border-bottom:1px solid #f1f5f9;transition:background 0.1s}}
    tbody tr:hover{{background:#f8fafc}}
    .diff-row{{margin-bottom:4px;font-size:12px}}
    .diff-label{{font-size:10px;font-weight:700;color:#94a3b8;text-transform:uppercase;letter-spacing:0.3px;margin-bottom:1px}}
    .diff-old{{color:#dc2626;text-decoration:line-through;margin-right:6px}}
    .diff-new{{color:#16a34a;font-weight:700}}
    .diff-same{{color:#64748b}}
    .back-link{{display:inline-flex;align-items:center;gap:6px;color:#2563eb;font-size:13px;font-weight:700;text-decoration:none;margin-bottom:20px}}
    .back-link:hover{{text-decoration:underline}}
    /* Reject modal */
    .modal-overlay{{display:none;position:fixed;inset:0;z-index:100;background:rgba(0,0,0,0.5);backdrop-filter:blur(4px);align-items:center;justify-content:center}}
    .modal-overlay.open{{display:flex}}
    .modal{{background:#fff;border-radius:16px;padding:28px;max-width:440px;width:90%;box-shadow:0 20px 60px rgba(0,0,0,0.3)}}
    .modal h3{{font-size:16px;font-weight:800;color:#0f172a;margin-bottom:8px}}
    .modal p{{color:#64748b;font-size:13px;margin-bottom:16px}}
    .modal textarea{{width:100%;padding:10px 13px;border:1.5px solid #e5e7eb;border-radius:9px;font-size:13px;resize:vertical;min-height:80px;outline:none;font-family:inherit}}
    .modal textarea:focus{{border-color:#dc2626}}
    .modal-actions{{display:flex;gap:10px;margin-top:16px}}
    .btn-cancel{{flex:1;padding:11px;border:1.5px solid #e5e7eb;border-radius:9px;background:#fff;color:#374151;font-size:13px;font-weight:700;cursor:pointer}}
    .btn-reject{{flex:1;padding:11px;border:none;border-radius:9px;background:#dc2626;color:#fff;font-size:13px;font-weight:700;cursor:pointer}}
  </style>
</head>
<body>
<div class='topbar'>
  <form method='post' action='/admin/logout-home' style='display:flex;align-items:center;background:none;border:none;padding:0;cursor:pointer;'>
    <button type='submit' style='background:none;border:none;padding:0;cursor:pointer;display:flex;align-items:center;'>
      <img src='/dbp-logo.png' alt='DBP' style='width:40px;height:40px;object-fit:contain;'/>
    </button>
  </form>
  <div>
    <h1>Pending Card Review</h1>
  </div>
  <span class='sub'>{pending.Count} pending · <a href='/admin' style='color:rgba(255,255,255,0.6);text-decoration:none;'>Admin Dashboard</a></span>
</div>

<div class='container'>
  <a href='/admin' class='back-link'><i class='fa-solid fa-arrow-left'></i> Back to Dashboard</a>

  {(string.IsNullOrWhiteSpace(flash) ? "" : $"<div class='flash {(flash.StartsWith("✅") ? "flash-ok" : "flash-err")}'>{Esc(flash)}</div>")}

  <div class='card'>
    <div class='card-header'>
      <h2><i class='fa-solid fa-clock-rotate-left' style='margin-right:8px;color:#f59e0b;'></i>Pending Changes</h2>
      {(pending.Count > 0 ? $"<span class='badge'><i class='fa-solid fa-triangle-exclamation'></i> {pending.Count} awaiting review</span>" : "<span style='color:#16a34a;font-size:13px;font-weight:700;'>All clear ✅</span>")}
    </div>
    <table>
      <thead>
        <tr>
          <th>Employee</th>
          <th>Proposed Changes</th>
          <th>Submitted</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        {rows}
      </tbody>
    </table>
  </div>
</div>

<!-- Reject modal -->
<div class='modal-overlay' id='rejectModal'>
  <div class='modal'>
    <h3>❌ Reject Changes</h3>
    <p id='rejectModalName'>Rejecting changes for this employee.</p>
    <form method='post' id='rejectForm'>
      <input type='hidden' name='sam' id='rejectSam'/>
      <label style='font-size:12px;font-weight:700;color:#374151;display:block;margin-bottom:6px;'>Reason (optional — will be emailed to the employee):</label>
      <textarea name='reason' placeholder='e.g. Title does not match HR records. Please contact HR to update your AD profile.'></textarea>
      <div class='modal-actions'>
        <button type='button' class='btn-cancel' onclick='closeReject()'>Cancel</button>
        <button type='submit' class='btn-reject'>Reject</button>
      </div>
    </form>
  </div>
</div>

<script>
function openReject(sam, name) {{
  document.getElementById('rejectSam').value = sam;
  document.getElementById('rejectModalName').textContent = 'Rejecting changes for ' + name + '.';
  document.getElementById('rejectForm').action = '/admin/review/reject';
  document.getElementById('rejectModal').classList.add('open');
}}
function closeReject() {{
  document.getElementById('rejectModal').classList.remove('open');
}}
</script>
</body>
</html>";
    }

    private static string DiffCell(string? current, string? pending, string label)
    {
        if (string.IsNullOrWhiteSpace(pending) || current == pending) return "";
        var isEmpty = string.IsNullOrWhiteSpace(current);
        return $@"<div class='diff-row'>
            <div class='diff-label'>{Esc(label)}</div>
            <span class='diff-old'>{(isEmpty ? "(empty)" : Esc(current))}</span>
            <span style='color:#94a3b8;margin-right:6px;'>→</span>
            <span class='diff-new'>{Esc(pending)}</span>
        </div>";
    }

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
