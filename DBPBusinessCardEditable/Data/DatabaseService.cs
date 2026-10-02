using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using DBPBusinessCardEditable.Models;
using Microsoft.Data.Sqlite;

namespace DBPBusinessCardEditable.Data;

/// <summary>
/// SQLite-backed persistence for card profiles.
/// Replaces the flat cards.json approach.
/// </summary>
public sealed class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public DatabaseService(IConfiguration config, ILogger<DatabaseService> logger)
    {
        _logger = logger;
        var configured = config["Database:Path"];
        string dbPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            dbPath = configured;
        }
        else
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(dataDir);
            dbPath = Path.Combine(dataDir, "bizcard.db");
        }
        _logger.LogInformation("SQLite database path: {Path}", dbPath);
        _connectionString = $"Data Source={dbPath};Cache=Shared;";
        InitSchema();
        MigrateFromJson(config);
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void InitSchema()
    {
        try
        {
            using var conn = Open();
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS cards (
                    sam_account_name    TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    emp_id              TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                    card_token          TEXT NOT NULL DEFAULT '',
                    card_pin            TEXT NOT NULL DEFAULT '',
                    pin_is_hashed       INTEGER NOT NULL DEFAULT 0,
                    is_active           INTEGER NOT NULL DEFAULT 1,
                    is_published        INTEGER NOT NULL DEFAULT 0,
                    published_at        TEXT,
                    name                TEXT NOT NULL DEFAULT '',
                    show_name           INTEGER NOT NULL DEFAULT 1,
                    preferred_name      TEXT NOT NULL DEFAULT '',
                    show_preferred_name INTEGER NOT NULL DEFAULT 0,
                    photo               TEXT NOT NULL DEFAULT '',
                    show_photo          INTEGER NOT NULL DEFAULT 1,
                    title               TEXT NOT NULL DEFAULT '',
                    show_title          INTEGER NOT NULL DEFAULT 1,
                    department          TEXT NOT NULL DEFAULT '',
                    show_department     INTEGER NOT NULL DEFAULT 1,
                    org                 TEXT NOT NULL DEFAULT 'Development Bank of the Philippines',
                    show_org            INTEGER NOT NULL DEFAULT 1,
                    office              TEXT NOT NULL DEFAULT '',
                    show_office         INTEGER NOT NULL DEFAULT 1,
                    phone               TEXT NOT NULL DEFAULT '',
                    show_phone          INTEGER NOT NULL DEFAULT 1,
                    email               TEXT NOT NULL DEFAULT '',
                    show_email          INTEGER NOT NULL DEFAULT 1,
                    github              TEXT NOT NULL DEFAULT '',
                    show_github         INTEGER NOT NULL DEFAULT 1,
                    linkedin            TEXT NOT NULL DEFAULT '',
                    show_linkedin       INTEGER NOT NULL DEFAULT 1,
                    portfolio           TEXT NOT NULL DEFAULT '',
                    show_portfolio      INTEGER NOT NULL DEFAULT 1,
                    extra_info          TEXT NOT NULL DEFAULT '[]',
                    view_history        TEXT NOT NULL DEFAULT '[]',
                    total_views         INTEGER NOT NULL DEFAULT 0,
                    audit_log           TEXT NOT NULL DEFAULT '[]',
                    created_at          TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at          TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_cards_emp_id
                    ON cards(emp_id) WHERE emp_id != '';
                CREATE UNIQUE INDEX IF NOT EXISTS idx_cards_token
                    ON cards(card_token) WHERE card_token != '';
            ");

            // Add pending-approval columns — ALTER TABLE is idempotent via try/catch
            var pendingCols = new[]
            {
                "ALTER TABLE cards ADD COLUMN pending_title       TEXT NOT NULL DEFAULT ''",
                "ALTER TABLE cards ADD COLUMN pending_department  TEXT NOT NULL DEFAULT ''",
                "ALTER TABLE cards ADD COLUMN pending_org         TEXT NOT NULL DEFAULT ''",
                "ALTER TABLE cards ADD COLUMN pending_office      TEXT NOT NULL DEFAULT ''",
                "ALTER TABLE cards ADD COLUMN pending_status      TEXT NOT NULL DEFAULT 'none'",
                "ALTER TABLE cards ADD COLUMN pending_submitted_at TEXT",
                "ALTER TABLE cards ADD COLUMN pending_submitted_by TEXT NOT NULL DEFAULT ''",
                "ALTER TABLE cards ADD COLUMN view_log            TEXT NOT NULL DEFAULT '[]'",
                "ALTER TABLE cards ADD COLUMN card_expires_at     TEXT",
            };
            foreach (var col in pendingCols)
            {
                try { conn.Execute(col); } catch { /* column already exists — ignore */ }
            }
            _logger.LogInformation("SQLite schema initialised successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite schema initialisation failed.");
            throw;
        }
    }

    // ── Migration from cards.json ─────────────────────────────────────────────

    private void MigrateFromJson(IConfiguration config)
    {
        var jsonPath = config["Database:LegacyJsonPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "cards.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            var json  = File.ReadAllText(jsonPath);
            var cards = JsonSerializer.Deserialize<List<CardProfile>>(json);
            if (cards is null || cards.Count == 0) return;

            int migrated = 0;
            foreach (var card in cards)
            {
                if (string.IsNullOrWhiteSpace(card.EmpId)) continue;
                if (string.IsNullOrWhiteSpace(card.SamAccountName))
                    card.SamAccountName = card.EmpId;

                using var conn = Open();
                var exists = conn.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM cards WHERE sam_account_name = @Sam COLLATE NOCASE",
                    new { Sam = card.SamAccountName });
                if (exists == 0) { Upsert(card, conn); migrated++; }
            }

            if (migrated > 0)
            {
                _logger.LogInformation("Migrated {Count} card(s) from cards.json to SQLite.", migrated);
                File.Move(jsonPath, jsonPath + ".migrated", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate cards.json.");
        }
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // WAL mode for better concurrent read performance
        conn.Execute("PRAGMA journal_mode=WAL;");
        return conn;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public CardProfile? GetBySam(string sam)
    {
        if (string.IsNullOrWhiteSpace(sam)) return null;
        using var conn = Open();
        var row = conn.QueryFirstOrDefault<CardRow>(
            "SELECT * FROM cards WHERE sam_account_name = @Sam COLLATE NOCASE",
            new { Sam = sam.Trim() });
        return row is null ? null : Map(row);
    }

    public CardProfile? GetByEmpId(string empId)
    {
        if (string.IsNullOrWhiteSpace(empId)) return null;
        using var conn = Open();
        var row = conn.QueryFirstOrDefault<CardRow>(
            "SELECT * FROM cards WHERE emp_id = @EmpId COLLATE NOCASE",
            new { EmpId = empId.Trim() });
        return row is null ? null : Map(row);
    }

    public CardProfile? GetByToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var conn = Open();
        var row = conn.QueryFirstOrDefault<CardRow>(
            "SELECT * FROM cards WHERE card_token = @Token",
            new { Token = token.Trim() });
        return row is null ? null : Map(row);
    }

    public List<CardProfile> GetAll()
    {
        using var conn = Open();
        return conn.Query<CardRow>("SELECT * FROM cards ORDER BY updated_at DESC")
                   .Select(Map).ToList();
    }

    public int Count()
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(1) FROM cards");
    }

    public bool EmpIdExists(string empId, string excludeSam = "")
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM cards WHERE emp_id = @EmpId COLLATE NOCASE AND sam_account_name != @Exc COLLATE NOCASE",
            new { EmpId = empId.Trim(), Exc = excludeSam }) > 0;
    }

    public void Save(CardProfile profile)
    {
        profile.EmpId          = profile.EmpId.Trim();
        profile.SamAccountName = profile.SamAccountName.Trim();

        using var conn = Open();
        var existing = conn.QueryFirstOrDefault<CardRow>(
            "SELECT * FROM cards WHERE sam_account_name = @Sam COLLATE NOCASE",
            new { Sam = profile.SamAccountName });

        bool isNew = existing is null;

        if (isNew)
        {
            profile.CardToken = GenerateToken();
            profile.CreatedAt = DateTime.UtcNow;
            AppendAudit(profile, "Created", "New card created");
        }
        else
        {
            var prev = Map(existing!);
            profile.CardToken   = prev.CardToken;   // token is immutable once set
            profile.CreatedAt   = prev.CreatedAt;
            profile.ViewHistory = prev.ViewHistory;
            profile.TotalViews  = prev.TotalViews;
            profile.AuditLog    = prev.AuditLog;
            AppendAudit(profile, "Updated", BuildDiff(prev, profile));
        }

        // Hash PIN if it was changed and is still plaintext
        if (!string.IsNullOrWhiteSpace(profile.CardPin) && !profile.PinIsHashed)
        {
            profile.CardPin    = HashPin(profile.CardPin);
            profile.PinIsHashed = true;
        }

        profile.LastUpdated = DateTime.UtcNow;
        Upsert(profile, conn);
    }

    public void RecordView(string token, string? ipAddress = null, string? userAgent = null, string scanType = "direct")
    {
        var profile = GetByToken(token);
        if (profile is null) return;

        var today  = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var bucket = profile.ViewHistory.FirstOrDefault(v => v.Date == today);
        if (bucket is null) { bucket = new DailyView { Date = today }; profile.ViewHistory.Add(bucket); }
        bucket.Count++;
        profile.TotalViews++;
        if (profile.ViewHistory.Count > 90)
            profile.ViewHistory = profile.ViewHistory.OrderByDescending(v => v.Date).Take(90).ToList();

        // Append to viewer log (cap at 200)
        profile.ViewLog ??= new();
        profile.ViewLog.Insert(0, new ViewEvent
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            IpAddress = ipAddress ?? "Unknown",
            UserAgent = (userAgent ?? "").Length > 120 ? (userAgent ?? "")[..120] : (userAgent ?? ""),
            ScanType  = scanType
        });
        if (profile.ViewLog.Count > 200)
            profile.ViewLog = profile.ViewLog.Take(200).ToList();

        using var conn = Open();
        conn.Execute(
            "UPDATE cards SET view_history=@V, total_views=@T, view_log=@L WHERE card_token=@Token",
            new { V = Serialize(profile.ViewHistory), T = profile.TotalViews, L = Serialize(profile.ViewLog), Token = token });
    }

    public (int today, int week, int month, int total) GetViewStats(string sam)
    {
        var p = GetBySam(sam);
        if (p is null) return (0, 0, 0, 0);
        var now   = DateTime.UtcNow;
        var today = now.ToString("yyyy-MM-dd");
        var week  = now.AddDays(-7).ToString("yyyy-MM-dd");
        var month = now.AddDays(-30).ToString("yyyy-MM-dd");
        return (
            p.ViewHistory.Where(v => v.Date == today).Sum(v => v.Count),
            p.ViewHistory.Where(v => string.Compare(v.Date, week,  StringComparison.Ordinal) >= 0).Sum(v => v.Count),
            p.ViewHistory.Where(v => string.Compare(v.Date, month, StringComparison.Ordinal) >= 0).Sum(v => v.Count),
            p.TotalViews);
    }

    public void MarkPublished(string sam, bool published)
    {
        using var conn = Open();
        conn.Execute(
            "UPDATE cards SET is_published=@P, published_at=@At, updated_at=@Now WHERE sam_account_name=@Sam COLLATE NOCASE",
            new
            {
                P   = published ? 1 : 0,
                At  = published ? DateTime.UtcNow.ToString("o") : (string?)null,
                Now = DateTime.UtcNow.ToString("o"),
                Sam = sam.Trim()
            });
    }

    public void Reset(string sam)
    {
        var existing = GetBySam(sam);
        if (existing is null) return;
        var fresh = new CardProfile
        {
            SamAccountName = existing.SamAccountName,
            EmpId          = existing.EmpId,
            CardToken      = existing.CardToken,
            CardPin        = existing.CardPin,
            PinIsHashed    = existing.PinIsHashed,
            CreatedAt      = existing.CreatedAt,
            AuditLog       = existing.AuditLog,
            ViewHistory    = existing.ViewHistory,
            TotalViews     = existing.TotalViews,
            ViewLog        = existing.ViewLog,
            IsPublished    = existing.IsPublished,
            PublishedAt    = existing.PublishedAt,
            Org            = "Development Bank of the Philippines",
            // Explicitly clear pending approval on reset
            PendingTitle       = "",
            PendingDepartment  = "",
            PendingOrg         = "",
            PendingOffice      = "",
            PendingStatus      = "none",
            PendingSubmittedAt = null,
            PendingSubmittedBy = ""
        };
        AppendAudit(fresh, "Reset", "Card was reset — all fields cleared");
        fresh.LastUpdated = DateTime.UtcNow;
        using var conn = Open();
        // Upsert the blanked card, then explicitly clear pending columns
        // (Upsert does not include pending columns in ON CONFLICT UPDATE)
        Upsert(fresh, conn);
        conn.Execute(@"
            UPDATE cards SET
                pending_title        = '',
                pending_department   = '',
                pending_org          = '',
                pending_office       = '',
                pending_status       = 'none',
                pending_submitted_at = NULL,
                pending_submitted_by = ''
            WHERE sam_account_name = @Sam COLLATE NOCASE",
            new { Sam = sam.Trim() });
    }

    public void Delete(string sam)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM cards WHERE sam_account_name=@Sam COLLATE NOCASE",
            new { Sam = sam.Trim() });
    }

    public int ClearAll()
    {
        using var conn = Open();
        var n = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM cards");
        conn.Execute("DELETE FROM cards");
        return n;
    }

    // ── Pending approval ──────────────────────────────────────────────────────

    public void SubmitPending(string sam, string pendingTitle, string pendingDepartment,
        string pendingOrg, string pendingOffice)
    {
        using var conn = Open();
        conn.Execute(@"
            UPDATE cards SET
                pending_title       = @T,
                pending_department  = @D,
                pending_org         = @O,
                pending_office      = @Of,
                pending_status      = 'pending',
                pending_submitted_at = @At,
                pending_submitted_by = @Sam,
                updated_at          = @Now
            WHERE sam_account_name = @Sam COLLATE NOCASE",
            new
            {
                T   = pendingTitle      ?? "",
                D   = pendingDepartment ?? "",
                O   = pendingOrg        ?? "",
                Of  = pendingOffice     ?? "",
                At  = DateTime.UtcNow.ToString("o"),
                Sam = sam.Trim(),
                Now = DateTime.UtcNow.ToString("o")
            });
    }

    public void ApprovePending(string sam)
    {
        var card = GetBySam(sam);
        if (card is null || card.PendingStatus != "pending") return;
        using var conn = Open();
        conn.Execute(@"
            UPDATE cards SET
                title               = pending_title,
                department          = pending_department,
                org                 = pending_org,
                office              = pending_office,
                pending_title       = '',
                pending_department  = '',
                pending_org         = '',
                pending_office      = '',
                pending_status      = 'approved',
                pending_submitted_at = NULL,
                updated_at          = @Now
            WHERE sam_account_name = @Sam COLLATE NOCASE",
            new { Now = DateTime.UtcNow.ToString("o"), Sam = sam.Trim() });

        // Also re-publish the card if it was published
        if (card.IsPublished)
        {
            var updated = GetBySam(sam);
            if (updated is not null) _onApproved?.Invoke(updated);
        }
    }

    public void RejectPending(string sam, string reason = "")
    {
        using var conn = Open();
        conn.Execute(@"
            UPDATE cards SET
                pending_title       = '',
                pending_department  = '',
                pending_org         = '',
                pending_office      = '',
                pending_status      = 'rejected',
                pending_submitted_at = NULL,
                updated_at          = @Now
            WHERE sam_account_name = @Sam COLLATE NOCASE",
            new { Now = DateTime.UtcNow.ToString("o"), Sam = sam.Trim() });
    }

    public List<CardProfile> GetPending()
    {
        using var conn = Open();
        return conn.Query<CardRow>(
            "SELECT * FROM cards WHERE pending_status = 'pending' ORDER BY pending_submitted_at ASC")
            .Select(Map).ToList();
    }

    // Callback invoked after approval so PublishService can regenerate the static file
    private Action<CardProfile>? _onApproved;
    public void SetApprovalCallback(Action<CardProfile> cb) => _onApproved = cb;

    // ── PIN (BCrypt) ──────────────────────────────────────────────────────────
    public bool VerifyPin(string sam, string pin)
    {
        var card = GetBySam(sam);
        if (card is null) return false;
        if (string.IsNullOrWhiteSpace(card.CardPin)) return true;
        pin = (pin ?? "").Trim();
        if (card.PinIsHashed) return BCrypt.Net.BCrypt.Verify(pin, card.CardPin);

        // Legacy plaintext — verify then upgrade in-place
        if (card.CardPin != pin) return false;
        using var conn = Open();
        conn.Execute(
            "UPDATE cards SET card_pin=@H, pin_is_hashed=1 WHERE sam_account_name=@Sam COLLATE NOCASE",
            new { H = HashPin(pin), Sam = sam.Trim() });
        return true;
    }

    public static string HashPin(string pin)
        => BCrypt.Net.BCrypt.HashPassword(pin.Trim(), workFactor: 12);

    // ── Token generation ──────────────────────────────────────────────────────

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void Upsert(CardProfile p, SqliteConnection conn)
    {
        conn.Execute(@"
            INSERT INTO cards (
                sam_account_name,emp_id,card_token,card_pin,pin_is_hashed,
                is_active,is_published,published_at,card_expires_at,
                name,show_name,preferred_name,show_preferred_name,photo,show_photo,
                title,show_title,department,show_department,org,show_org,office,show_office,
                phone,show_phone,email,show_email,
                github,show_github,linkedin,show_linkedin,portfolio,show_portfolio,
                extra_info,view_history,total_views,audit_log,created_at,updated_at
            ) VALUES (
                @SamAccountName,@EmpId,@CardToken,@CardPin,@PinIsHashed,
                @IsActive,@IsPublished,@PublishedAt,@CardExpiresAt,
                @Name,@ShowName,@PreferredName,@ShowPreferredName,@Photo,@ShowPhoto,
                @Title,@ShowTitle,@Department,@ShowDepartment,@Org,@ShowOrg,@Office,@ShowOffice,
                @Phone,@ShowPhone,@Email,@ShowEmail,
                @GitHub,@ShowGitHub,@LinkedIn,@ShowLinkedIn,@Portfolio,@ShowPortfolio,
                @ExtraInfo,@ViewHistory,@TotalViews,@AuditLog,@CreatedAt,@UpdatedAt
            )
            ON CONFLICT(sam_account_name) DO UPDATE SET
                emp_id=excluded.emp_id,card_pin=excluded.card_pin,pin_is_hashed=excluded.pin_is_hashed,
                is_active=excluded.is_active,is_published=excluded.is_published,
                published_at=excluded.published_at,card_expires_at=excluded.card_expires_at,
                name=excluded.name,show_name=excluded.show_name,
                preferred_name=excluded.preferred_name,show_preferred_name=excluded.show_preferred_name,
                photo=excluded.photo,show_photo=excluded.show_photo,
                title=excluded.title,show_title=excluded.show_title,
                department=excluded.department,show_department=excluded.show_department,
                org=excluded.org,show_org=excluded.show_org,
                office=excluded.office,show_office=excluded.show_office,
                phone=excluded.phone,show_phone=excluded.show_phone,
                email=excluded.email,show_email=excluded.show_email,
                github=excluded.github,show_github=excluded.show_github,
                linkedin=excluded.linkedin,show_linkedin=excluded.show_linkedin,
                portfolio=excluded.portfolio,show_portfolio=excluded.show_portfolio,
                extra_info=excluded.extra_info,view_history=excluded.view_history,
                total_views=excluded.total_views,audit_log=excluded.audit_log,
                updated_at=excluded.updated_at",
            new
            {
                p.SamAccountName, p.EmpId, p.CardToken,
                PinIsHashed       = p.PinIsHashed   ? 1 : 0,
                IsActive          = p.IsActive       ? 1 : 0,
                IsPublished       = p.IsPublished     ? 1 : 0,
                PublishedAt       = p.PublishedAt?.ToString("o"),
                CardExpiresAt     = p.CardExpiresAt?.ToString("o"),
                Name              = p.Name             ?? "",
                ShowName          = p.ShowName          ? 1 : 0,
                PreferredName     = p.PreferredName     ?? "",
                ShowPreferredName = p.ShowPreferredName  ? 1 : 0,
                Photo             = p.Photo             ?? "",
                ShowPhoto         = p.ShowPhoto          ? 1 : 0,
                Title             = p.Title             ?? "",
                ShowTitle         = p.ShowTitle          ? 1 : 0,
                Department        = p.Department        ?? "",
                ShowDepartment    = p.ShowDepartment     ? 1 : 0,
                Org               = p.Org               ?? "Development Bank of the Philippines",
                ShowOrg           = p.ShowOrg            ? 1 : 0,
                Office            = p.Office            ?? "",
                ShowOffice        = p.ShowOffice         ? 1 : 0,
                Phone             = p.Phone             ?? "",
                ShowPhone         = p.ShowPhone          ? 1 : 0,
                Email             = p.Email             ?? "",
                ShowEmail         = p.ShowEmail          ? 1 : 0,
                GitHub            = p.GitHub            ?? "",
                ShowGitHub        = p.ShowGitHub         ? 1 : 0,
                LinkedIn          = p.LinkedIn          ?? "",
                ShowLinkedIn      = p.ShowLinkedIn       ? 1 : 0,
                Portfolio         = p.Portfolio         ?? "",
                ShowPortfolio     = p.ShowPortfolio      ? 1 : 0,
                CardPin           = p.CardPin           ?? "",
                ExtraInfo   = Serialize(p.ExtraInfo   ?? new()),
                ViewHistory = Serialize(p.ViewHistory  ?? new()),
                p.TotalViews,
                AuditLog    = Serialize(p.AuditLog    ?? new()),
                CreatedAt   = p.CreatedAt.ToString("o"),
                UpdatedAt   = p.LastUpdated.ToString("o")
            });
    }

    private static CardProfile Map(CardRow r) => new()
    {
        SamAccountName    = r.sam_account_name,
        EmpId             = r.emp_id,
        CardToken         = r.card_token,
        CardPin           = r.card_pin,
        PinIsHashed       = r.pin_is_hashed == 1,
        IsActive          = r.is_active == 1,
        IsPublished       = r.is_published == 1,
        PublishedAt       = r.published_at is null ? null : DateTime.Parse(r.published_at, null, System.Globalization.DateTimeStyles.RoundtripKind),
        Name              = r.name,
        ShowName          = r.show_name == 1,
        PreferredName     = r.preferred_name,
        ShowPreferredName = r.show_preferred_name == 1,
        Photo             = r.photo,
        ShowPhoto         = r.show_photo == 1,
        Title             = r.title,
        ShowTitle         = r.show_title == 1,
        Department        = r.department,
        ShowDepartment    = r.show_department == 1,
        Org               = r.org,
        ShowOrg           = r.show_org == 1,
        Office            = r.office,
        ShowOffice        = r.show_office == 1,
        Phone             = r.phone,
        ShowPhone         = r.show_phone == 1,
        Email             = r.email,
        ShowEmail         = r.show_email == 1,
        GitHub            = r.github,
        ShowGitHub        = r.show_github == 1,
        LinkedIn          = r.linkedin,
        ShowLinkedIn      = r.show_linkedin == 1,
        Portfolio         = r.portfolio,
        ShowPortfolio     = r.show_portfolio == 1,
        ExtraInfo         = Deserialize<List<ExtraInfoItem>>(r.extra_info) ?? new(),
        ViewHistory       = Deserialize<List<DailyView>>(r.view_history)   ?? new(),
        TotalViews        = r.total_views,
        ViewLog           = Deserialize<List<ViewEvent>>(r.view_log)       ?? new(),
        AuditLog          = Deserialize<List<AuditEntry>>(r.audit_log)     ?? new(),
        CreatedAt         = DateTime.Parse(r.created_at,  null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastUpdated       = DateTime.Parse(r.updated_at,  null, System.Globalization.DateTimeStyles.RoundtripKind),
        PendingTitle      = r.pending_title      ?? "",
        PendingDepartment = r.pending_department ?? "",
        PendingOrg        = r.pending_org        ?? "",
        PendingOffice     = r.pending_office     ?? "",
        PendingStatus     = r.pending_status     ?? "none",
        PendingSubmittedAt = r.pending_submitted_at is null ? null : DateTime.Parse(r.pending_submitted_at, null, System.Globalization.DateTimeStyles.RoundtripKind),
        PendingSubmittedBy = r.pending_submitted_by ?? "",
        CardExpiresAt      = r.card_expires_at is null ? null : DateTime.Parse(r.card_expires_at, null, System.Globalization.DateTimeStyles.RoundtripKind)
    };

    private static string Serialize<T>(T obj) => JsonSerializer.Serialize(obj, _json);

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json); } catch { return default; }
    }

    private static string BuildDiff(CardProfile before, CardProfile after)
    {
        var ch = new List<string>();
        void C(string f, string a, string b)  { if (a != b) ch.Add(f); }
        void B(string f, bool a, bool b)       { if (a != b) ch.Add(f + " visibility"); }
        C("Name",        before.Name,        after.Name);
        C("Title",       before.Title,       after.Title);
        C("Department",  before.Department,  after.Department);
        C("Org",         before.Org,         after.Org);
        C("Office",      before.Office,      after.Office);
        C("Phone",       before.Phone,       after.Phone);
        C("Email",       before.Email,       after.Email);
        C("GitHub",      before.GitHub,      after.GitHub);
        C("LinkedIn",    before.LinkedIn,    after.LinkedIn);
        C("Portfolio",   before.Portfolio,   after.Portfolio);
        if (before.Photo != after.Photo) ch.Add("Photo");
        B("Name",        before.ShowName,       after.ShowName);
        B("Title",       before.ShowTitle,      after.ShowTitle);
        B("Department",  before.ShowDepartment, after.ShowDepartment);
        B("Phone",       before.ShowPhone,      after.ShowPhone);
        B("Email",       before.ShowEmail,      after.ShowEmail);
        if (before.IsActive != after.IsActive)
            return after.IsActive ? "Card activated" : "Card deactivated";
        return ch.Count == 0 ? "No field changes"
             : "Changed: " + string.Join(", ", ch.Take(6))
               + (ch.Count > 6 ? $" (+{ch.Count - 6} more)" : "");
    }

    private static void AppendAudit(CardProfile card, string action, string detail)
    {
        card.AuditLog ??= new();
        card.AuditLog.Insert(0, new AuditEntry { Timestamp = DateTime.UtcNow, Action = action, Detail = detail });
        if (card.AuditLog.Count > 50) card.AuditLog = card.AuditLog.Take(50).ToList();
    }

    // ── Dapper row type ───────────────────────────────────────────────────────

#pragma warning disable IDE1006
    private sealed class CardRow
    {
        public string sam_account_name    { get; set; } = "";
        public string emp_id              { get; set; } = "";
        public string card_token          { get; set; } = "";
        public string card_pin            { get; set; } = "";
        public int    pin_is_hashed       { get; set; }
        public int    is_active           { get; set; } = 1;
        public int    is_published        { get; set; }
        public string? published_at       { get; set; }
        public string name                { get; set; } = "";
        public int    show_name           { get; set; } = 1;
        public string preferred_name      { get; set; } = "";
        public int    show_preferred_name { get; set; }
        public string photo               { get; set; } = "";
        public int    show_photo          { get; set; } = 1;
        public string title               { get; set; } = "";
        public int    show_title          { get; set; } = 1;
        public string department          { get; set; } = "";
        public int    show_department     { get; set; } = 1;
        public string org                 { get; set; } = "Development Bank of the Philippines";
        public int    show_org            { get; set; } = 1;
        public string office              { get; set; } = "";
        public int    show_office         { get; set; } = 1;
        public string phone               { get; set; } = "";
        public int    show_phone          { get; set; } = 1;
        public string email               { get; set; } = "";
        public int    show_email          { get; set; } = 1;
        public string github              { get; set; } = "";
        public int    show_github         { get; set; } = 1;
        public string linkedin            { get; set; } = "";
        public int    show_linkedin       { get; set; } = 1;
        public string portfolio           { get; set; } = "";
        public int    show_portfolio      { get; set; } = 1;
        public string extra_info          { get; set; } = "[]";
        public string view_history        { get; set; } = "[]";
        public int    total_views         { get; set; }
        public string audit_log           { get; set; } = "[]";
        public string created_at          { get; set; } = "";
        public string updated_at          { get; set; } = "";
        // Pending approval
        public string pending_title       { get; set; } = "";
        public string pending_department  { get; set; } = "";
        public string pending_org         { get; set; } = "";
        public string pending_office      { get; set; } = "";
        public string pending_status      { get; set; } = "none";
        public string? pending_submitted_at { get; set; }
        public string pending_submitted_by { get; set; } = "";
        public string view_log             { get; set; } = "[]";
        public string? card_expires_at      { get; set; }
    }
#pragma warning restore IDE1006
}
