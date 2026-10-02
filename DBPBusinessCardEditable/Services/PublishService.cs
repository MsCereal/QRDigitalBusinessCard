using System.Text;
using System.Text.Encodings.Web;
using DBPBusinessCardEditable.Models;

namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Generates a fully self-contained static .html card that looks identical
/// to Card.cshtml — same background, flip card, buttons, dark mode, etc.
/// No server calls needed — vCard download and QR are handled client-side.
/// </summary>
public sealed class PublishService
{
    private readonly string _outputPath;
    private readonly string _publicBaseUrl;
    private readonly ILogger<PublishService> _logger;

    public PublishService(IConfiguration config, ILogger<PublishService> logger)
    {
        _logger = logger;
        var configured = config["Publishing:OutputPath"];
        _outputPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DBPBizCard", "published")
            : configured;
        var configuredUrl = config["Publishing:PublicBaseUrl"];
        _publicBaseUrl = string.IsNullOrWhiteSpace(configuredUrl)
            ? "" : configuredUrl.TrimEnd('/');

        try
        {
            Directory.CreateDirectory(_outputPath);
            _logger.LogInformation("Publish output path: {Path}", _outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot create publish output folder: {Path}", _outputPath);
        }
    }

    public string GetPublicUrl(string token) => $"{_publicBaseUrl}/{token}.html";

    public void Publish(CardProfile p)
    {
        var html = BuildHtml(p);
        var path = Path.Combine(_outputPath, $"{p.CardToken}.html");
        var tmp  = path + ".tmp";
        File.WriteAllText(tmp, html, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        _logger.LogInformation("Published card {Token} => {Path}", p.CardToken, path);
    }

    private string BuildHtml(CardProfile p)
    {
        var H = HtmlEncoder.Default;
        string E(string? s) => H.Encode(s ?? "");
        string J(string? s) => System.Text.Json.JsonSerializer.Serialize(s ?? "");

        bool hasNickname = p.ShowPreferredName && !string.IsNullOrWhiteSpace(p.PreferredName);
        bool hasContact  = (p.ShowPhone && !string.IsNullOrWhiteSpace(p.Phone)) ||
                           (p.ShowEmail && !string.IsNullOrWhiteSpace(p.Email));
        bool hasLinks    = (p.ShowGitHub    && !string.IsNullOrWhiteSpace(p.GitHub))   ||
                           (p.ShowLinkedIn  && !string.IsNullOrWhiteSpace(p.LinkedIn)) ||
                           (p.ShowPortfolio && !string.IsNullOrWhiteSpace(p.Portfolio));
        var validExtra   = p.ExtraInfo?.Where(e => !string.IsNullOrWhiteSpace(e.Label) && !string.IsNullOrWhiteSpace(e.Value)).ToList()
                           ?? new List<ExtraInfoItem>();

        var displayName  = hasNickname ? p.PreferredName! : (p.ShowName && !string.IsNullOrWhiteSpace(p.Name) ? p.Name : "");
        var publicUrl    = GetPublicUrl(p.CardToken);
        var vcfB64       = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildVcf(p)));

        // Find wwwroot — works both in dev (project root) and published (beside the exe)
        var wwwrootBase = FindWwwroot();

        // Inline background image
        var bgPath   = Path.Combine(wwwrootBase, "bg.png");
        var bgB64    = File.Exists(bgPath) ? Convert.ToBase64String(File.ReadAllBytes(bgPath)) : "";
        var bgInline = bgB64.Length > 0
            ? $"linear-gradient(155deg,rgba(10,25,70,0.95) 0%,rgba(30,64,175,0.90) 45%,rgba(15,40,100,0.97) 100%),url('data:image/png;base64,{bgB64}') center/cover no-repeat"
            : "linear-gradient(155deg,rgba(10,25,70,0.95) 0%,rgba(30,64,175,0.90) 45%,rgba(15,40,100,0.97) 100%)";

        // Dark mode background — darker gradient but building still visible behind it
        var bgInlineDark = bgB64.Length > 0
            ? $"linear-gradient(155deg,rgba(3,7,25,0.90) 0%,rgba(8,18,45,0.85) 45%,rgba(2,6,20,0.92) 100%),url('data:image/png;base64,{bgB64}') center/cover no-repeat"
            : "linear-gradient(155deg,rgba(3,7,25,0.94) 0%,rgba(8,18,45,0.92) 45%,rgba(2,6,20,0.96) 100%)";

        string ExtraIconCss(string label)
        {
            var l = label.ToLower();
            if (l.Contains("facebook"))  return "fa-brands fa-facebook";
            if (l.Contains("instagram")) return "fa-brands fa-instagram";
            if (l.Contains("twitter") || l.Contains("x.com")) return "fa-brands fa-x-twitter";
            if (l.Contains("tiktok"))    return "fa-brands fa-tiktok";
            if (l.Contains("youtube"))   return "fa-brands fa-youtube";
            if (l.Contains("messenger")) return "fa-brands fa-facebook-messenger";
            if (l.Contains("whatsapp"))  return "fa-brands fa-whatsapp";
            if (l.Contains("viber"))     return "fa-brands fa-viber";
            if (l.Contains("telegram"))  return "fa-brands fa-telegram";
            if (l.Contains("skype"))     return "fa-brands fa-skype";
            if (l.Contains("discord"))   return "fa-brands fa-discord";
            if (l.Contains("snapchat"))  return "fa-brands fa-snapchat";
            if (l.Contains("phone") || l.Contains("mobile")) return "fa-solid fa-phone";
            if (l.Contains("email") || l.Contains("mail"))   return "fa-solid fa-envelope";
            if (l.Contains("department") || l.Contains("dept")) return "fa-solid fa-building";
            if (l.Contains("birthday") || l.Contains("bday"))   return "fa-solid fa-cake-candles";
            return "fa-solid fa-link";
        }

        string ExtraHref(string val) =>
            val.StartsWith("http")  ? val :
            (val.StartsWith("+") || (val.Length > 0 && char.IsDigit(val[0]))) ? $"tel:{val}" : "#";

        string ExtraTarget(string val) => val.StartsWith("http") ? "_blank" : "_self";

        var sb = new StringBuilder();

        sb.Append($@"<!DOCTYPE html>
<html lang=""en"" id=""htmlRoot"">
<head>
  <meta charset=""utf-8""/>
  <meta name=""viewport"" content=""width=device-width,initial-scale=1.0""/>
  <title>{E(displayName)} - DBP Digital Card</title>
  <meta http-equiv=""Content-Security-Policy"" content=""default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline' https://cdnjs.cloudflare.com; font-src https://cdnjs.cloudflare.com; img-src data: 'self'; connect-src 'none'; frame-ancestors 'none';""/>
  <link rel=""stylesheet"" href=""https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.0/css/all.min.css""/>
  <style>
    *,*::before,*::after{{box-sizing:border-box;margin:0;padding:0}}
    :root{{--card-bg:#fff;--card-text:#0f172a;--card-sub:#64748b;--card-border:#e8edf2;--info-bg:#eff6ff;--info-hover:#f0f7ff;--info-icon-col:#2563eb;--info-label:#94a3b8;--info-value:#0f172a;--back-close:#94a3b8;--divider:#e8edf2;--btn-bg:#fff;--btn-border:#2563eb;--btn-text:#2563eb;--shadow:0 16px 48px rgba(0,0,0,0.35)}}
    html.dark{{--card-bg:#111;--card-text:#f5f5f5;--card-sub:#a3a3a3;--card-border:#2a2a2a;--info-bg:#1a1a1a;--info-hover:#222;--info-icon-col:#e5e5e5;--info-label:#6b6b6b;--info-value:#f5f5f5;--back-close:#6b6b6b;--divider:#2a2a2a;--btn-bg:#111;--btn-border:#e5e5e5;--btn-text:#f5f5f5;--shadow:0 16px 48px rgba(0,0,0,0.8)}}
    html.dark .page-bg{{background:{bgInlineDark}}}
    html.dark .card-face{{background:var(--card-bg)}}
    html.dark .card-face::before{{background:linear-gradient(90deg,#333,#666,#999)}}
    html.dark h1{{color:var(--card-text)}}
    html.dark .btn-share{{border-color:#e5e5e5;color:#f5f5f5}}
    html.dark .btn-share:hover{{background:#e5e5e5;color:#111}}
    html.dark .profile-avatar{{background:#1a1a1a;border-color:#333}}
    html.dark .dbp-badge{{background:#111;border-color:#333}}
    body{{font-family:Arial,sans-serif;min-height:100vh;display:flex;flex-direction:column;align-items:center;padding:30px 16px 60px;position:relative;overflow-y:auto;overflow-x:hidden}}
    .page-bg{{position:fixed;inset:0;z-index:0;background:{bgInline};transition:background 0.5s}}
    .dark-toggle{{position:relative;z-index:2;align-self:flex-end;margin-bottom:8px;background:rgba(255,255,255,0.12);border:1px solid rgba(255,255,255,0.2);color:rgba(255,255,255,0.75);border-radius:20px;padding:5px 12px;font-size:12px;font-weight:600;cursor:pointer;display:flex;align-items:center;gap:6px;transition:background 0.15s}}
    .dark-toggle:hover{{background:rgba(255,255,255,0.22)}}
    .flip-hint{{position:relative;z-index:1;color:rgba(255,255,255,0.35);font-size:11px;letter-spacing:1px;margin-bottom:12px;cursor:pointer;user-select:none;text-align:center}}
    .card-scene{{position:relative;z-index:1;width:100%;max-width:380px;perspective:1000px}}
    .card-flipper{{width:100%;position:relative;transform-style:preserve-3d;transition:transform 0.55s cubic-bezier(0.4,0.2,0.2,1)}}
    .card-flipper.flipped{{transform:rotateY(180deg)}}
    .card-face{{width:100%;backface-visibility:hidden;-webkit-backface-visibility:hidden;background:var(--card-bg);border-radius:20px;box-shadow:var(--shadow);overflow:hidden;transition:background 0.3s,box-shadow 0.3s}}
    .card-face::before{{content:'';display:block;height:5px;background:linear-gradient(90deg,#1e3a8a,#2563eb,#60a5fa);position:absolute;bottom:0;left:0;right:0;border-radius:0 0 20px 20px}}
    .card-front{{padding:28px 20px 32px;text-align:center;position:relative}}
    .card-back{{position:absolute;top:0;left:0;padding:22px 20px 32px;transform:rotateY(180deg);text-align:left;width:100%}}
    .profile-header{{text-align:center;margin-bottom:14px}}
    .profile-photo-wrap{{position:relative;display:inline-block}}
    .profile-photo{{width:90px;height:90px;border-radius:50%;object-fit:cover;border:3px solid var(--card-border);box-shadow:0 4px 16px rgba(0,0,0,0.12);display:block}}
    .profile-avatar{{width:90px;height:90px;border-radius:50%;background:var(--info-bg);border:3px solid var(--card-border);display:flex;align-items:center;justify-content:center;overflow:hidden}}
    .profile-avatar img{{width:100%;height:100%;object-fit:cover;display:block;border-radius:50%}}
    .dbp-badge{{position:absolute;bottom:2px;right:-4px;width:30px;height:30px;border-radius:50%;background:var(--card-bg);border:2px solid var(--card-border);box-shadow:0 2px 6px rgba(0,0,0,0.15);display:flex;align-items:center;justify-content:center;overflow:hidden}}
    .dbp-badge img{{width:26px;height:26px;object-fit:contain}}
    h1{{color:var(--card-text);font-size:21px;font-weight:800;margin-bottom:2px}}
    .full-name-sub{{color:var(--card-sub);font-size:12px;margin-bottom:4px}}
    .job-title{{color:var(--card-text);font-size:13px;font-weight:500;margin-bottom:2px}}
    .dept-name{{color:var(--card-sub);font-size:12px;font-weight:500;margin-bottom:2px}}
    .org-name{{color:var(--card-sub);font-size:12px}}
    .office-link{{display:inline-flex;align-items:center;gap:5px;color:var(--info-icon-col);font-size:12px;font-weight:600;text-decoration:none;margin-top:5px}}
    .office-link:hover{{text-decoration:underline}}
    .divider{{height:1px;background:var(--divider);margin:8px 0}}
    .btn{{display:flex;align-items:center;justify-content:center;gap:9px;padding:11px 14px;margin:4px 0;border-radius:10px;text-decoration:none;font-size:13px;font-weight:700;background:var(--btn-bg);border:1.5px solid var(--btn-border);color:var(--btn-text);transition:background 0.16s,color 0.16s,box-shadow 0.16s,transform 0.1s;cursor:pointer;width:100%}}
    .btn i{{font-size:14px;width:16px;text-align:center;flex-shrink:0}}
    .btn:hover{{background:var(--btn-border);color:var(--card-bg)}}
    .btn:active{{transform:scale(0.97)}}
    .btn-share{{border-color:var(--btn-border);color:var(--btn-text);opacity:0.8}}
    .btn-share:hover{{background:var(--btn-border);color:var(--card-bg);opacity:1}}
    .back-header{{display:flex;align-items:center;gap:10px;margin-bottom:16px;padding-bottom:14px;border-bottom:1px solid var(--divider)}}
    .back-logo{{width:32px;height:32px;object-fit:contain}}
    .back-header-text strong{{display:block;color:var(--card-text);font-size:14px;font-weight:800}}
    .back-header-text span{{color:var(--info-label);font-size:11px}}
    .back-close{{margin-left:auto;background:none;border:none;color:var(--back-close);font-size:17px;cursor:pointer;padding:4px;line-height:1}}
    .back-close:hover{{color:var(--card-text)}}
    .info-hint{{font-size:11px;color:var(--info-label);margin-bottom:12px;display:flex;align-items:center;gap:5px}}
    .info-row{{display:flex;align-items:flex-start;gap:10px;padding:9px 10px;border-radius:9px;margin-bottom:5px;cursor:pointer;border:1px solid var(--card-border);transition:background 0.14s,border-color 0.14s;text-decoration:none}}
    .info-row:hover{{background:var(--info-hover);border-color:var(--info-icon-col)}}
    .info-icon{{width:30px;height:30px;border-radius:7px;background:var(--info-bg);display:flex;align-items:center;justify-content:center;flex-shrink:0}}
    .info-icon i{{font-size:12px;color:var(--info-icon-col)}}
    .info-content{{flex:1;min-width:0}}
    .info-label-text{{font-size:10px;font-weight:700;color:var(--info-label);text-transform:uppercase;letter-spacing:0.5px;margin-bottom:1px}}
    .info-value-text{{font-size:13px;color:var(--info-value);font-weight:500;word-break:break-all}}
    .copy-tick{{font-size:12px;color:#16a34a;opacity:0;transition:opacity 0.2s;flex-shrink:0;align-self:center}}
    .copy-tick.show{{opacity:1}}
    .back-actions{{margin-top:14px;padding-top:12px;border-top:1px solid var(--divider)}}
    .last-updated{{text-align:center;font-size:10px;color:var(--info-label);margin-top:6px}}
    @media (max-width:480px){{body{{padding:20px 12px 50px}}h1{{font-size:19px}}}}
  </style>
</head>
<body>
<div class=""page-bg""></div>
<button class=""dark-toggle"" onclick=""toggleDark()"" id=""darkBtn"">
  <i class=""fa-solid fa-moon"" id=""darkIcon""></i><span id=""darkLabel"">Dark</span>
</button>
<div class=""flip-hint"" onclick=""flipCard()"" id=""flipHint"">
  <i class=""fa-solid fa-rotate""></i> Tap card to flip &amp; view details
</div>
<div class=""card-scene"" id=""cardScene"">
<div class=""card-flipper"" id=""cardFlipper"">
");

        // ── FRONT ────────────────────────────────────────────────────────────
        sb.AppendLine(@"<div class=""card-face card-front"" id=""cardFront"" onclick=""flipCard()"">");

        // Photo
        if (p.ShowPhoto && !string.IsNullOrWhiteSpace(p.Photo))
        {
            sb.AppendLine(@"<div class=""profile-header""><div class=""profile-photo-wrap"">");

            var wwwroot = wwwrootBase; // use the correctly resolved path

            if (p.Photo.StartsWith("data:"))
            {
                sb.AppendLine($@"<img src=""{E(p.Photo)}"" alt=""{E(p.Name)}"" class=""profile-photo""/>");
            }
            else if (p.Photo == "avatar:male")
            {
                var boyPath = Path.Combine(wwwroot, "dbp-boy.png");
                var boyB64  = File.Exists(boyPath) ? Convert.ToBase64String(File.ReadAllBytes(boyPath)) : "";
                sb.AppendLine($@"<div class=""profile-photo profile-avatar""><img src=""data:image/png;base64,{boyB64}"" alt=""Male"" style=""width:100%;height:100%;object-fit:cover;display:block;border-radius:50%;""/></div>");
            }
            else if (p.Photo == "avatar:female")
            {
                var girlPath = Path.Combine(wwwroot, "dbp-girl.png");
                var girlB64  = File.Exists(girlPath) ? Convert.ToBase64String(File.ReadAllBytes(girlPath)) : "";
                sb.AppendLine($@"<div class=""profile-photo profile-avatar""><img src=""data:image/png;base64,{girlB64}"" alt=""Female"" style=""width:100%;height:100%;object-fit:cover;display:block;border-radius:50%;""/></div>");
            }

            var logoPath = Path.Combine(wwwroot, "dbp-logo.png");
            var logoB64  = File.Exists(logoPath) ? Convert.ToBase64String(File.ReadAllBytes(logoPath)) : "";
            sb.AppendLine($@"<div class=""dbp-badge""><img src=""data:image/png;base64,{logoB64}"" alt=""DBP""/></div>");
            sb.AppendLine("</div></div>");
        }

        // Name
        if (hasNickname)
        {
            sb.AppendLine($"<h1>{E(p.PreferredName)}</h1>");
            if (p.ShowName && !string.IsNullOrWhiteSpace(p.Name))
                sb.AppendLine($@"<p class=""full-name-sub"">{E(p.Name)}</p>");
        }
        else if (p.ShowName && !string.IsNullOrWhiteSpace(p.Name))
            sb.AppendLine($"<h1>{E(p.Name)}</h1>");

        if (p.ShowTitle      && !string.IsNullOrWhiteSpace(p.Title))      sb.AppendLine($@"<p class=""job-title"">{E(p.Title)}</p>");
        if (p.ShowDepartment && !string.IsNullOrWhiteSpace(p.Department)) sb.AppendLine($@"<p class=""dept-name"">{E(p.Department)}</p>");
        if (p.ShowOrg        && !string.IsNullOrWhiteSpace(p.Org))        sb.AppendLine($@"<p class=""org-name"">{E(p.Org)}</p>");
        if (p.ShowOffice     && !string.IsNullOrWhiteSpace(p.Office))
            sb.AppendLine($@"<a class=""office-link"" href=""https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(p.Office)}"" target=""_blank"" rel=""noopener"" onclick=""event.stopPropagation()""><i class=""fa-solid fa-location-dot""></i> {E(p.Office)}</a>");

        if (hasContact || hasLinks) sb.AppendLine(@"<div class=""divider""></div>");

        if (p.ShowPhone && !string.IsNullOrWhiteSpace(p.Phone))
            sb.AppendLine($@"<a class=""btn"" href=""tel:{E(p.Phone)}"" onclick=""event.stopPropagation()""><i class=""fa-solid fa-phone""></i> Call Now</a>");
        if (p.ShowEmail && !string.IsNullOrWhiteSpace(p.Email))
            sb.AppendLine($@"<a class=""btn"" href=""mailto:{E(p.Email)}"" onclick=""event.stopPropagation()""><i class=""fa-solid fa-envelope""></i> Send Email</a>");
        if (hasContact)
            sb.AppendLine(@"<button class=""btn"" onclick=""saveContact(); event.stopPropagation();""><i class=""fa-solid fa-address-card""></i> Save Contact</button>");

        if (hasContact && hasLinks) sb.AppendLine(@"<div class=""divider""></div>");

        if (p.ShowGitHub    && !string.IsNullOrWhiteSpace(p.GitHub))
            sb.AppendLine($@"<a class=""btn"" href=""{E(p.GitHub)}"" target=""_blank"" rel=""noopener"" onclick=""event.stopPropagation()""><i class=""fa-brands fa-github""></i> View GitHub</a>");
        if (p.ShowLinkedIn  && !string.IsNullOrWhiteSpace(p.LinkedIn))
            sb.AppendLine($@"<a class=""btn"" href=""{E(p.LinkedIn)}"" target=""_blank"" rel=""noopener"" onclick=""event.stopPropagation()""><i class=""fa-brands fa-linkedin""></i> View LinkedIn</a>");
        if (p.ShowPortfolio && !string.IsNullOrWhiteSpace(p.Portfolio))
            sb.AppendLine($@"<a class=""btn"" href=""{E(p.Portfolio)}"" target=""_blank"" rel=""noopener"" onclick=""event.stopPropagation()""><i class=""fa-solid fa-globe""></i> Visit Website</a>");

        if (validExtra.Count > 0)
        {
            if (hasContact || hasLinks) sb.AppendLine(@"<div class=""divider""></div>");
            foreach (var ex in validExtra)
            {
                var dt = !string.IsNullOrWhiteSpace(ex.Title) ? ex.Title : ex.Label;
                sb.AppendLine($@"<a class=""btn"" href=""{E(ExtraHref(ex.Value))}"" target=""{ExtraTarget(ex.Value)}"" rel=""noopener"" onclick=""event.stopPropagation()""><i class=""{ExtraIconCss(ex.Label)}""></i> {E(dt)}</a>");
            }
        }

        sb.AppendLine(@"<div class=""divider""></div>");
        sb.AppendLine(@"<button class=""btn btn-share"" onclick=""shareCard(); event.stopPropagation();"" id=""shareBtn""><i class=""fa-solid fa-share-nodes""></i> Share This Card</button>");
        sb.AppendLine(@"<button class=""btn"" style=""border-color:#e5e7eb;color:#94a3b8;margin-top:2px;font-size:12px;"" onclick=""openQrModal(); event.stopPropagation();""><i class=""fa-solid fa-qrcode""></i> View My QR Code</button>");
        sb.AppendLine("</div><!-- /front -->");

        // ── BACK ─────────────────────────────────────────────────────────────
        var logoPathBack = Path.Combine(wwwrootBase, "dbp-logo.png");
        var logoB64Back  = File.Exists(logoPathBack) ? Convert.ToBase64String(File.ReadAllBytes(logoPathBack)) : "";
        sb.AppendLine(@"<div class=""card-face card-back"" id=""cardBack"">");
        sb.AppendLine($@"<div class=""back-header""><img src=""data:image/png;base64,{logoB64Back}"" alt=""DBP"" class=""back-logo""/><div class=""back-header-text""><strong>{E(displayName)}</strong><span>Tap any row to copy</span></div><button class=""back-close"" onclick=""flipCard()""><i class=""fa-solid fa-xmark""></i></button></div>");
        sb.AppendLine(@"<p class=""info-hint""><i class=""fa-solid fa-circle-info""></i> Tap a row to copy to clipboard</p>");

        void InfoRow(string icon, string label, string val, string? href = null)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            var tag  = href is not null ? "a" : "div";
            var hAttr = href is not null ? $@" href=""{E(href)}"" target=""_blank"" rel=""noopener""" : "";
            sb.AppendLine($@"<{tag} class=""info-row""{hAttr} onclick=""copyInfo(this,{J(val)})""><div class=""info-icon""><i class=""{icon}""></i></div><div class=""info-content""><p class=""info-label-text"">{E(label)}</p><p class=""info-value-text"">{E(val)}</p></div><i class=""fa-solid fa-check copy-tick""></i></{tag}>");
        }

        if (p.ShowName      && !string.IsNullOrWhiteSpace(p.Name))       InfoRow("fa-solid fa-user",            "Full Name",    p.Name);
        if (hasNickname                                                  ) InfoRow("fa-solid fa-id-badge",        "Nickname",     p.PreferredName!);
        if (p.ShowTitle     && !string.IsNullOrWhiteSpace(p.Title))      InfoRow("fa-solid fa-briefcase",        "Job Title",    p.Title);
        if (p.ShowDepartment&& !string.IsNullOrWhiteSpace(p.Department)) InfoRow("fa-solid fa-building",         "Department",   p.Department);
        if (p.ShowOrg       && !string.IsNullOrWhiteSpace(p.Org))        InfoRow("fa-solid fa-building-columns", "Organization", p.Org);
        if (p.ShowPhone     && !string.IsNullOrWhiteSpace(p.Phone))      InfoRow("fa-solid fa-phone",            "Phone",        p.Phone);
        if (p.ShowOffice    && !string.IsNullOrWhiteSpace(p.Office))     InfoRow("fa-solid fa-location-dot",     "Office",       p.Office, $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(p.Office)}");
        if (p.ShowEmail     && !string.IsNullOrWhiteSpace(p.Email))      InfoRow("fa-solid fa-envelope",         "Email",        p.Email);
        if (p.ShowGitHub    && !string.IsNullOrWhiteSpace(p.GitHub))     InfoRow("fa-brands fa-github",          "GitHub",       p.GitHub);
        if (p.ShowLinkedIn  && !string.IsNullOrWhiteSpace(p.LinkedIn))   InfoRow("fa-brands fa-linkedin",        "LinkedIn",     p.LinkedIn);
        if (p.ShowPortfolio && !string.IsNullOrWhiteSpace(p.Portfolio))  InfoRow("fa-solid fa-globe",            "Website",      p.Portfolio);
        foreach (var ex in validExtra)
            InfoRow(ExtraIconCss(ex.Label), !string.IsNullOrWhiteSpace(ex.Title) ? ex.Title : ex.Label, ex.Value);

        sb.AppendLine(@"<div class=""back-actions""><button class=""btn btn-share"" onclick=""shareCard()""><i class=""fa-solid fa-share-nodes""></i> Share This Card</button></div>");
        sb.AppendLine($@"<p class=""last-updated""><i class=""fa-regular fa-clock""></i> Last updated: {p.LastUpdated:MMM d, yyyy}</p>");
        sb.AppendLine("</div><!-- /back -->");
        sb.AppendLine("</div></div><!-- /flipper/scene -->");

        // ── QR Modal (client-side QR via qrcode.js) ───────────────────────────
// ── QR Modal (QR generated server-side and embedded as base64 PNG) ───────────────
var qrBase64 = GenerateQrBase64(publicUrl + "?src=qr");
var qrImgTag = qrBase64.Length > 0
    ? $@"<img src=""data:image/png;base64,{qrBase64}"" alt=""QR Code"" style=""width:200px;height:200px;display:block;""/>"
    : $@"<p style=""font-size:11px;color:#64748b;padding:20px;word-break:break-all;"">{publicUrl}</p>";

        sb.AppendLine($@"
<div id=""qrModal"" onclick=""closeQrModal()"" style=""display:none;position:fixed;inset:0;z-index:100;background:rgba(0,0,0,0.75);backdrop-filter:blur(4px);align-items:center;justify-content:center;"">
  <div onclick=""event.stopPropagation()"" style=""background:#fff;border-radius:20px;padding:24px 20px 20px;max-width:300px;width:90%;text-align:center;position:relative;box-shadow:0 20px 60px rgba(0,0,0,0.5);"">
    <button onclick=""closeQrModal()"" style=""position:absolute;top:12px;right:12px;background:none;border:none;font-size:18px;color:#94a3b8;cursor:pointer;""><i class=""fa-solid fa-xmark""></i></button>
    <p style=""font-size:11px;font-weight:800;letter-spacing:1.5px;text-transform:uppercase;color:#2563eb;margin-bottom:12px;""><i class=""fa-solid fa-qrcode"" style=""margin-right:5px;""></i>My QR Code</p>
    <div style=""background:#fff;border-radius:12px;padding:10px;display:inline-block;border:2px solid #e5e7eb;"">
      {qrImgTag}
    </div>
    <p style=""font-size:11px;color:#94a3b8;margin-top:10px;"">Scan to open this card</p>
    <button onclick=""downloadQr()"" id=""qrDownloadBtn"" style=""margin-top:12px;width:100%;padding:10px;border:1.5px solid #2563eb;border-radius:9px;background:#fff;color:#2563eb;font-size:13px;font-weight:700;cursor:pointer;display:flex;align-items:center;justify-content:center;gap:7px;"">
      <i class=""fa-solid fa-download""></i> Save QR Code
    </button>
  </div>
</div>
");

        // ── JS ────────────────────────────────────────────────────────────────
        sb.AppendLine("<script>");
        sb.AppendLine($"var CARD_URL = {J(publicUrl)};");
        sb.AppendLine($"var CARD_NAME = {J(displayName)};");
        sb.AppendLine($"var VCF_B64 = '{vcfB64}';");
        sb.AppendLine(@"
function toggleDark(){
  var html=document.getElementById('htmlRoot'),icon=document.getElementById('darkIcon'),label=document.getElementById('darkLabel');
  var d=html.classList.toggle('dark');
  icon.className=d?'fa-solid fa-sun':'fa-solid fa-moon';label.textContent=d?'Light':'Dark';
  try{localStorage.setItem('dbp-dark',d?'1':'0')}catch(e){}
}
(function(){try{if(localStorage.getItem('dbp-dark')==='1'){document.getElementById('htmlRoot').classList.add('dark');document.getElementById('darkIcon').className='fa-solid fa-sun';document.getElementById('darkLabel').textContent='Light'}}catch(e){}})();

function syncHeight(){
  var f=document.getElementById('cardFront'),b=document.getElementById('cardBack'),fl=document.getElementById('cardFlipper'),sc=document.getElementById('cardScene');
  var flipped=fl.classList.contains('flipped');
  var h=flipped?b.scrollHeight:f.offsetHeight;
  fl.style.height=h+'px';sc.style.height=h+'px';b.style.height=b.scrollHeight+'px';
}
window.addEventListener('load',syncHeight);window.addEventListener('resize',syncHeight);

function flipCard(){
  var fl=document.getElementById('cardFlipper'),hint=document.getElementById('flipHint');
  var flipped=fl.classList.toggle('flipped');
  hint.innerHTML=flipped?'<i class=""fa-solid fa-rotate-left""></i> Tap card to flip back':'<i class=""fa-solid fa-rotate""></i> Tap card to flip &amp; view details';
  setTimeout(syncHeight,300);
}

function copyInfo(row,text){
  var tick=row.querySelector('.copy-tick');
  function done(){tick.classList.add('show');row.style.background='#f0fdf4';row.style.borderColor='#86efac';setTimeout(function(){tick.classList.remove('show');row.style.background='';row.style.borderColor=''},2000)}
  if(navigator.clipboard){navigator.clipboard.writeText(text).then(done).catch(function(){var e=document.createElement('textarea');e.value=text;document.body.appendChild(e);e.select();document.execCommand('copy');document.body.removeChild(e);done()})}
  else{var e=document.createElement('textarea');e.value=text;document.body.appendChild(e);e.select();document.execCommand('copy');document.body.removeChild(e);done()}
}

function saveContact(){
  var b=atob(VCF_B64),arr=new Uint8Array(b.length);
  for(var i=0;i<b.length;i++)arr[i]=b.charCodeAt(i);
  var blob=new Blob([arr],{type:'text/vcard'});
  var url=URL.createObjectURL(blob),a=document.createElement('a');
  a.href=url;a.download=(CARD_NAME||'contact')+'.vcf';
  document.body.appendChild(a);a.click();document.body.removeChild(a);URL.revokeObjectURL(url);
}

function shareCard(){
  if(navigator.share){navigator.share({title:CARD_NAME+' - DBP Digital Business Card',url:CARD_URL}).catch(function(){})}
  else{if(navigator.clipboard){navigator.clipboard.writeText(CARD_URL).then(function(){var btn=document.getElementById('shareBtn');if(btn){btn.innerHTML='<i class=""fa-solid fa-check""></i> Copied!';setTimeout(function(){btn.innerHTML='<i class=""fa-solid fa-share-nodes""></i> Share This Card'},2500)}})}else{prompt('Copy this link:',CARD_URL)}}
}

function openQrModal(){document.getElementById('qrModal').style.display='flex';}
function closeQrModal(){document.getElementById('qrModal').style.display='none';}
function downloadQr(){
  var img=document.querySelector('#qrModal img');
  if(!img)return;
  var a=document.createElement('a');
  a.href=img.src;
  a.download=(CARD_NAME||'card')+'-QR.png';
  document.body.appendChild(a);a.click();document.body.removeChild(a);
}
");
        sb.AppendLine("</script>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string BuildVcf(CardProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCARD");
        sb.AppendLine("VERSION:3.0");
        bool hasNickname = p.ShowPreferredName && !string.IsNullOrWhiteSpace(p.PreferredName);
        if (p.ShowName  && !string.IsNullOrWhiteSpace(p.Name))   sb.AppendLine($"FN:{p.Name}");
        if (hasNickname                                          ) sb.AppendLine($"NICKNAME:{p.PreferredName}");
        if (p.ShowTitle && !string.IsNullOrWhiteSpace(p.Title))  sb.AppendLine($"TITLE:{p.Title}");
        if (p.ShowOrg   && !string.IsNullOrWhiteSpace(p.Org))
            sb.AppendLine($"ORG:{p.Org}{(p.ShowDepartment && !string.IsNullOrWhiteSpace(p.Department) ? ";" + p.Department : "")}");
        if (p.ShowPhone  && !string.IsNullOrWhiteSpace(p.Phone))  sb.AppendLine($"TEL;TYPE=WORK:{p.Phone}");
        if (p.ShowEmail  && !string.IsNullOrWhiteSpace(p.Email))  sb.AppendLine($"EMAIL;TYPE=WORK:{p.Email}");
        if (p.ShowOffice && !string.IsNullOrWhiteSpace(p.Office)) sb.AppendLine($"ADR;TYPE=WORK:;;{p.Office};;;;");
        if (p.ShowGitHub    && !string.IsNullOrWhiteSpace(p.GitHub))    sb.AppendLine($"URL;TYPE=GitHub:{p.GitHub}");
        if (p.ShowLinkedIn  && !string.IsNullOrWhiteSpace(p.LinkedIn))  sb.AppendLine($"URL;TYPE=LinkedIn:{p.LinkedIn}");
        if (p.ShowPortfolio && !string.IsNullOrWhiteSpace(p.Portfolio)) sb.AppendLine($"URL;TYPE=Portfolio:{p.Portfolio}");
        if (p.ExtraInfo is { Count: > 0 })
            foreach (var e in p.ExtraInfo.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
                sb.AppendLine($"X-SOCIALPROFILE;TYPE={e.Label}:{e.Value}");
        if (p.ShowPhoto && !string.IsNullOrWhiteSpace(p.Photo) && p.Photo.StartsWith("data:image/jpeg"))
        {
            var b64 = p.Photo.Split(',', 2).LastOrDefault() ?? "";
            if (!string.IsNullOrWhiteSpace(b64)) sb.AppendLine($"PHOTO;ENCODING=b;TYPE=JPEG:{b64}");
        }
        sb.AppendLine($"REV:{p.LastUpdated:yyyyMMddTHHmmssZ}");
        sb.AppendLine("END:VCARD");
        return sb.ToString();
    }

    /// <summary>
    /// Finds the wwwroot folder whether running in dev (project root) or published (beside exe).
    /// </summary>
    private static string FindWwwroot()
    {
        // Check beside the exe first (published mode)
        var beside = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(beside)) return beside;

        // Walk up from the exe looking for wwwroot (dev mode — exe is in bin/Debug/net8.0)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5; i++)
        {
            if (dir == null) break;
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return beside; // fallback — may not exist but won't throw
    }

    /// <summary>
    /// Generates a QR code PNG for the given URL and returns it as base64.
    /// Uses QRCoder which is already a project dependency.
    /// </summary>
    private static string GenerateQrBase64(string url)
    {
        try
        {
            using var gen  = new QRCoder.QRCodeGenerator();
            var data       = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.H);
            using var qr   = new QRCoder.PngByteQRCode(data);
            var bytes      = qr.GetGraphic(10);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return "";
        }
    }
}
