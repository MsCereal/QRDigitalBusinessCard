using System.Net;
using System.Net.Mail;

namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Sends email notifications via SMTP.
/// Config comes from the "Email" section of appsettings.
/// </summary>
public sealed class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config["Email:SmtpHost"]) &&
        !string.IsNullOrWhiteSpace(_config["Email:AdminAddress"]);

    /// <summary>
    /// Notifies the admin that a card field change is pending review.
    /// </summary>
    public void SendPendingReviewNotification(
        string employeeName,
        string sam,
        string oldTitle,  string newTitle,
        string oldDept,   string newDept,
        string oldOrg,    string newOrg,
        string oldOffice, string newOffice,
        string reviewUrl)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Email not configured — skipping pending review notification.");
            return;
        }

        try
        {
            var adminAddress = _config["Email:AdminAddress"]!;
            var fromAddress  = _config["Email:FromAddress"]  ?? "noreply@dbp.gov.ph";
            var fromName     = _config["Email:FromName"]     ?? "DBP Digital Business Card";

            var changes = new List<string>();
            if (oldTitle  != newTitle)  changes.Add($"<tr><td style='padding:6px 12px;color:#64748b;font-size:13px;'>Job Title</td><td style='padding:6px 12px;font-size:13px;'><span style='color:#dc2626;text-decoration:line-through;'>{Esc(oldTitle)}</span> → <strong style='color:#16a34a;'>{Esc(newTitle)}</strong></td></tr>");
            if (oldDept   != newDept)   changes.Add($"<tr><td style='padding:6px 12px;color:#64748b;font-size:13px;'>Department</td><td style='padding:6px 12px;font-size:13px;'><span style='color:#dc2626;text-decoration:line-through;'>{Esc(oldDept)}</span> → <strong style='color:#16a34a;'>{Esc(newDept)}</strong></td></tr>");
            if (oldOrg    != newOrg)    changes.Add($"<tr><td style='padding:6px 12px;color:#64748b;font-size:13px;'>Organization</td><td style='padding:6px 12px;font-size:13px;'><span style='color:#dc2626;text-decoration:line-through;'>{Esc(oldOrg)}</span> → <strong style='color:#16a34a;'>{Esc(newOrg)}</strong></td></tr>");
            if (oldOffice != newOffice) changes.Add($"<tr><td style='padding:6px 12px;color:#64748b;font-size:13px;'>Office / Branch</td><td style='padding:6px 12px;font-size:13px;'><span style='color:#dc2626;text-decoration:line-through;'>{Esc(oldOffice)}</span> → <strong style='color:#16a34a;'>{Esc(newOffice)}</strong></td></tr>");

            if (changes.Count == 0) return; // nothing actually changed

            var changesHtml = string.Join("\n", changes);
            var body = $@"
<!DOCTYPE html>
<html>
<head><meta charset='utf-8'/></head>
<body style='font-family:Arial,sans-serif;background:#f8fafc;padding:32px 16px;'>
  <div style='max-width:540px;margin:0 auto;background:#fff;border-radius:16px;overflow:hidden;box-shadow:0 4px 20px rgba(0,0,0,0.08);'>

    <!-- Header -->
    <div style='background:linear-gradient(135deg,#1e3a8a,#2563eb);padding:28px 32px;text-align:center;'>
      <p style='color:rgba(255,255,255,0.7);font-size:11px;font-weight:700;letter-spacing:2px;text-transform:uppercase;margin:0 0 6px;'>DBP Digital Business Card</p>
      <h1 style='color:#fff;font-size:20px;font-weight:800;margin:0;'>⚠️ Pending Review Required</h1>
    </div>

    <!-- Body -->
    <div style='padding:28px 32px;'>
      <p style='color:#0f172a;font-size:14px;margin:0 0 16px;'>
        <strong>{Esc(employeeName)}</strong> (<code style='background:#f1f5f9;padding:2px 6px;border-radius:4px;font-size:12px;'>{Esc(sam)}</code>)
        has submitted changes to their business card that require your approval.
      </p>

      <table style='width:100%;border-collapse:collapse;background:#f8fafc;border-radius:10px;overflow:hidden;margin-bottom:24px;'>
        <thead>
          <tr style='background:#e2e8f0;'>
            <th style='padding:8px 12px;text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:0.5px;color:#64748b;'>Field</th>
            <th style='padding:8px 12px;text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:0.5px;color:#64748b;'>Change</th>
          </tr>
        </thead>
        <tbody>
          {changesHtml}
        </tbody>
      </table>

      <p style='color:#64748b;font-size:12px;margin:0 0 20px;'>
        Submitted on {DateTime.Now:MMMM d, yyyy} at {DateTime.Now:h:mm tt}
      </p>

      <a href='{reviewUrl}' style='display:inline-block;background:linear-gradient(135deg,#2563eb,#1d4ed8);color:#fff;text-decoration:none;padding:12px 28px;border-radius:10px;font-size:14px;font-weight:700;'>
        Review &amp; Approve →
      </a>
    </div>

    <!-- Footer -->
    <div style='background:#f1f5f9;padding:16px 32px;text-align:center;'>
      <p style='color:#94a3b8;font-size:11px;margin:0;'>
        Development Bank of the Philippines — Digital Business Card System
      </p>
    </div>
  </div>
</body>
</html>";

            Send(adminAddress, fromAddress, fromName,
                $"[DBP BizCard] Pending Review: {employeeName} changed their card", body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send pending review notification for {Sam}.", sam);
            // Note: email failure is non-blocking — the pending approval was still submitted
        }
    }

    /// <summary>Notifies employee their change was approved.</summary>
    public void SendApprovalNotification(string employeeEmail, string employeeName)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(employeeEmail)) return;
        try
        {
            var fromAddress = _config["Email:FromAddress"] ?? "noreply@dbp.gov.ph";
            var fromName    = _config["Email:FromName"]    ?? "DBP Digital Business Card";
            var body = $@"
<!DOCTYPE html><html><head><meta charset='utf-8'/></head>
<body style='font-family:Arial,sans-serif;background:#f8fafc;padding:32px 16px;'>
  <div style='max-width:500px;margin:0 auto;background:#fff;border-radius:16px;overflow:hidden;box-shadow:0 4px 20px rgba(0,0,0,0.08);'>
    <div style='background:linear-gradient(135deg,#065f46,#059669);padding:28px 32px;text-align:center;'>
      <h1 style='color:#fff;font-size:20px;font-weight:800;margin:0;'>✅ Changes Approved</h1>
    </div>
    <div style='padding:28px 32px;'>
      <p style='color:#0f172a;font-size:14px;'>Hi <strong>{Esc(employeeName)}</strong>,</p>
      <p style='color:#374151;font-size:14px;margin-top:8px;'>Your requested card changes have been <strong style='color:#059669;'>approved</strong> and are now live on your digital business card.</p>
    </div>
    <div style='background:#f1f5f9;padding:16px 32px;text-align:center;'>
      <p style='color:#94a3b8;font-size:11px;margin:0;'>Development Bank of the Philippines — Digital Business Card System</p>
    </div>
  </div>
</body></html>";
            Send(employeeEmail, fromAddress, fromName, "[DBP BizCard] Your card changes have been approved", body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send approval notification to {Email}.", employeeEmail);
        }
    }

    /// <summary>Notifies employee their change was rejected.</summary>
    public void SendRejectionNotification(string employeeEmail, string employeeName, string reason)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(employeeEmail)) return;
        try
        {
            var fromAddress = _config["Email:FromAddress"] ?? "noreply@dbp.gov.ph";
            var fromName    = _config["Email:FromName"]    ?? "DBP Digital Business Card";
            var body = $@"
<!DOCTYPE html><html><head><meta charset='utf-8'/></head>
<body style='font-family:Arial,sans-serif;background:#f8fafc;padding:32px 16px;'>
  <div style='max-width:500px;margin:0 auto;background:#fff;border-radius:16px;overflow:hidden;box-shadow:0 4px 20px rgba(0,0,0,0.08);'>
    <div style='background:linear-gradient(135deg,#7f1d1d,#dc2626);padding:28px 32px;text-align:center;'>
      <h1 style='color:#fff;font-size:20px;font-weight:800;margin:0;'>❌ Changes Rejected</h1>
    </div>
    <div style='padding:28px 32px;'>
      <p style='color:#0f172a;font-size:14px;'>Hi <strong>{Esc(employeeName)}</strong>,</p>
      <p style='color:#374151;font-size:14px;margin-top:8px;'>Your requested card changes have been <strong style='color:#dc2626;'>rejected</strong>. Your card will continue to show the previously approved information.</p>
      {(string.IsNullOrWhiteSpace(reason) ? "" : $"<p style='color:#374151;font-size:13px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;padding:12px;margin-top:12px;'><strong>Reason:</strong> {Esc(reason)}</p>")}
      <p style='color:#64748b;font-size:13px;margin-top:16px;'>If you believe this is an error, please contact your HR department to update your Active Directory profile.</p>
    </div>
    <div style='background:#f1f5f9;padding:16px 32px;text-align:center;'>
      <p style='color:#94a3b8;font-size:11px;margin:0;'>Development Bank of the Philippines — Digital Business Card System</p>
    </div>
  </div>
</body></html>";
            Send(employeeEmail, fromAddress, fromName, "[DBP BizCard] Your card changes have been rejected", body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send rejection notification to {Email}.", employeeEmail);
        }
    }

    private void Send(string to, string from, string fromName, string subject, string htmlBody)
    {
        var host    = _config["Email:SmtpHost"]!;
        var port    = _config.GetValue<int>("Email:SmtpPort", 25);
        var useSsl  = _config.GetValue<bool>("Email:UseSsl", false);
        var user    = _config["Email:SmtpUser"]     ?? "";
        var pass    = _config["Email:SmtpPassword"] ?? "";

        using var client = new SmtpClient(host, port)
        {
            EnableSsl      = useSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        if (!string.IsNullOrWhiteSpace(user))
            client.Credentials = new NetworkCredential(user, pass);

        using var msg = new MailMessage
        {
            From       = new MailAddress(from, fromName),
            Subject    = subject,
            Body       = htmlBody,
            IsBodyHtml = true
        };
        msg.To.Add(to);
        client.Send(msg);

        _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
    }

    private static string Esc(string? s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "");
}
