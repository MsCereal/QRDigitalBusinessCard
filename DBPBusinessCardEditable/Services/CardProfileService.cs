using DBPBusinessCardEditable.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DBPBusinessCardEditable.Services
{
    public class CardProfileService
    {
        private readonly string _dataPath;
        private readonly object _fileLock = new object();

        private readonly ConcurrentDictionary<string, CardProfile> _store
            = new ConcurrentDictionary<string, CardProfile>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _tokenIndex
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public CardProfileService()
        {
            _dataPath = Path.Combine(AppContext.BaseDirectory, "cards.json");
            LoadFromDisk();
        }

        // ── Persistence ────────────────────────────────────────────────────────

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_dataPath)) return;
                var json  = File.ReadAllText(_dataPath);
                var cards = JsonSerializer.Deserialize<List<CardProfile>>(json);
                if (cards == null) return;
                foreach (var card in cards)
                {
                    if (string.IsNullOrWhiteSpace(card.EmpId)) continue;
                    if (card.ViewHistory == null) card.ViewHistory = new List<DailyView>();
                    if (card.AuditLog    == null) card.AuditLog    = new List<AuditEntry>();
                    _store[card.EmpId] = card;
                    if (!string.IsNullOrWhiteSpace(card.CardToken))
                        _tokenIndex[card.CardToken] = card.EmpId;
                }
            }
            catch { }
        }

        private void SaveToDisk()
        {
            lock (_fileLock)
            {
                try
                {
                    var opts = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(_store.Values.ToList(), opts);
                    var tmp  = _dataPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _dataPath, overwrite: true);
                }
                catch { }
            }
        }

        // ── Audit helpers ──────────────────────────────────────────────────────

        private static string BuildDiff(CardProfile before, CardProfile after)
        {
            var changes = new List<string>();
            void Check(string field, string a, string b) { if (a != b) changes.Add(field); }
            void CheckB(string field, bool a, bool b)    { if (a != b) changes.Add(field + " visibility"); }

            Check("Name",        before.Name,        after.Name);
            Check("PreferredName", before.PreferredName, after.PreferredName);
            Check("Title",       before.Title,       after.Title);
            Check("Department",  before.Department,  after.Department);
            Check("Org",         before.Org,         after.Org);
            Check("Office",      before.Office,      after.Office);
            Check("Phone",       before.Phone,       after.Phone);
            Check("Email",       before.Email,       after.Email);
            Check("GitHub",      before.GitHub,      after.GitHub);
            Check("LinkedIn",    before.LinkedIn,    after.LinkedIn);
            Check("Portfolio",   before.Portfolio,   after.Portfolio);
            if (before.Photo != after.Photo) changes.Add("Photo");

            CheckB("Name",       before.ShowName,       after.ShowName);
            CheckB("Title",      before.ShowTitle,      after.ShowTitle);
            CheckB("Department", before.ShowDepartment, after.ShowDepartment);
            CheckB("Phone",      before.ShowPhone,      after.ShowPhone);
            CheckB("Email",      before.ShowEmail,      after.ShowEmail);
            CheckB("GitHub",     before.ShowGitHub,     after.ShowGitHub);
            CheckB("LinkedIn",   before.ShowLinkedIn,   after.ShowLinkedIn);

            if (before.IsActive != after.IsActive)
                return after.IsActive ? "Card activated" : "Card deactivated";

            return changes.Count == 0 ? "No field changes"
                 : "Changed: " + string.Join(", ", changes.Take(6))
                   + (changes.Count > 6 ? $" (+{changes.Count - 6} more)" : "");
        }

        private static void AppendAudit(CardProfile card, string action, string detail)
        {
            if (card.AuditLog == null) card.AuditLog = new List<AuditEntry>();
            card.AuditLog.Insert(0, new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                Action    = action,
                Detail    = detail
            });
            // Keep last 50 entries
            if (card.AuditLog.Count > 50) card.AuditLog = card.AuditLog.Take(50).ToList();
        }

        // ── Analytics ─────────────────────────────────────────────────────────

        public void RecordView(string empId)
        {
            if (!_store.TryGetValue(empId.Trim(), out var card)) return;
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var bucket = card.ViewHistory.FirstOrDefault(v => v.Date == today);
            if (bucket == null) { bucket = new DailyView { Date = today }; card.ViewHistory.Add(bucket); }
            bucket.Count++;
            card.TotalViews++;
            // Keep last 90 days
            if (card.ViewHistory.Count > 90)
                card.ViewHistory = card.ViewHistory.OrderByDescending(v => v.Date).Take(90).ToList();
            SaveToDisk();
        }

        public (int today, int week, int month, int total) GetViewStats(string empId)
        {
            if (!_store.TryGetValue(empId.Trim(), out var card))
                return (0, 0, 0, 0);
            var now   = DateTime.UtcNow;
            var today = now.ToString("yyyy-MM-dd");
            var week  = now.AddDays(-7).ToString("yyyy-MM-dd");
            var month = now.AddDays(-30).ToString("yyyy-MM-dd");
            return (
                card.ViewHistory.Where(v => v.Date == today).Sum(v => v.Count),
                card.ViewHistory.Where(v => string.Compare(v.Date, week,  StringComparison.Ordinal) >= 0).Sum(v => v.Count),
                card.ViewHistory.Where(v => string.Compare(v.Date, month, StringComparison.Ordinal) >= 0).Sum(v => v.Count),
                card.TotalViews
            );
        }

        // ── CRUD ───────────────────────────────────────────────────────────────

        public CardProfile Get(string empId)
        {
            if (string.IsNullOrWhiteSpace(empId)) return null;
            _store.TryGetValue(empId.Trim(), out var p);
            return p;
        }

        public CardProfile GetByToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (!_tokenIndex.TryGetValue(token.Trim(), out var empId)) return null;
            return Get(empId);
        }

        public CardProfile GetOrCreate(string empId)
            => _store.GetOrAdd(empId.Trim(), id => new CardProfile { EmpId = id });

        public void Save(CardProfile profile)
        {
            profile.EmpId = profile.EmpId.Trim();
            bool isNew = !_store.ContainsKey(profile.EmpId);

            if (string.IsNullOrWhiteSpace(profile.CardToken))
                profile.CardToken = Guid.NewGuid().ToString("N");

            // Preserve audit + analytics from existing record
            if (_store.TryGetValue(profile.EmpId, out var existing))
            {
                var diff = BuildDiff(existing, profile);
                profile.AuditLog    = existing.AuditLog    ?? new List<AuditEntry>();
                profile.ViewHistory = existing.ViewHistory ?? new List<DailyView>();
                profile.TotalViews  = existing.TotalViews;
                profile.CreatedAt   = existing.CreatedAt;
                AppendAudit(profile, isNew ? "Created" : "Updated", diff);
            }
            else
            {
                profile.CreatedAt = DateTime.UtcNow;
                AppendAudit(profile, "Created", "New card created");
            }

            profile.LastUpdated = DateTime.UtcNow;
            _store[profile.EmpId]       = profile;
            _tokenIndex[profile.CardToken] = profile.EmpId;
            SaveToDisk();
        }

        public void Reset(string empId)
        {
            _store.TryGetValue(empId.Trim(), out var existing);
            var fresh = new CardProfile
            {
                EmpId       = empId.Trim(),
                CardToken   = existing?.CardToken ?? Guid.NewGuid().ToString("N"),
                CardPin     = existing?.CardPin   ?? "",
                CreatedAt   = existing?.CreatedAt ?? DateTime.UtcNow,
                AuditLog    = existing?.AuditLog  ?? new List<AuditEntry>(),
                ViewHistory = existing?.ViewHistory ?? new List<DailyView>(),
                TotalViews  = existing?.TotalViews  ?? 0
            };
            AppendAudit(fresh, "Reset", "Card was reset — all fields cleared");
            fresh.LastUpdated = DateTime.UtcNow;
            _store[empId.Trim()]        = fresh;
            _tokenIndex[fresh.CardToken] = fresh.EmpId;
            SaveToDisk();
        }

        public void Delete(string empId)
        {
            if (_store.TryRemove(empId.Trim(), out var removed))
            {
                if (!string.IsNullOrWhiteSpace(removed.CardToken))
                    _tokenIndex.TryRemove(removed.CardToken, out _);
                SaveToDisk();
            }
        }

        public int ClearAll()
        {
            int count = _store.Count;
            _store.Clear(); _tokenIndex.Clear();
            SaveToDisk();
            return count;
        }

        public bool VerifyPin(string empId, string pin)
        {
            var card = Get(empId);
            if (card == null) return false;
            if (string.IsNullOrWhiteSpace(card.CardPin)) return true;
            return card.CardPin == (pin ?? "").Trim();
        }

        public int Count() => _store.Count;

        public List<CardProfile> GetAll()
            => _store.Values.OrderByDescending(p => p.LastUpdated).ToList();
    }
}
