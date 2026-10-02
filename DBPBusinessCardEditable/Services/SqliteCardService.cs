using DBPBusinessCardEditable.Data;
using DBPBusinessCardEditable.Models;

namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Thin facade over DatabaseService that preserves the same method signatures
/// as the old CardProfileService so controller code changes are minimal.
/// </summary>
public sealed class SqliteCardService(DatabaseService db)
{
    // ── Read ──────────────────────────────────────────────────────────────────
    public CardProfile? Get(string empId)             => db.GetByEmpId(empId);
    public CardProfile? GetBySam(string sam)          => db.GetBySam(sam);
    public CardProfile? GetByToken(string token)      => db.GetByToken(token);
    public List<CardProfile> GetAll()                 => db.GetAll();
    public int Count()                                => db.Count();
    public bool EmpIdExists(string empId, string sam) => db.EmpIdExists(empId, sam);

    // ── Write ─────────────────────────────────────────────────────────────────
    public void Save(CardProfile profile)    => db.Save(profile);
    public void Reset(string sam)            => db.Reset(sam);
    public void Delete(string sam)           => db.Delete(sam);
    public int  ClearAll()                   => db.ClearAll();
    public void MarkPublished(string sam, bool published) => db.MarkPublished(sam, published);

    // ── PIN ───────────────────────────────────────────────────────────────────
    public bool VerifyPin(string sam, string pin) => db.VerifyPin(sam, pin);

    // ── Analytics ─────────────────────────────────────────────────────────────
    public void RecordView(string token, string? ip = null, string? ua = null, string scanType = "direct")
        => db.RecordView(token, ip, ua, scanType);
    public (int today, int week, int month, int total) GetViewStats(string sam)
        => db.GetViewStats(sam);

    // ── Pending approval ─────────────────────────────────────────────────────
    public void SubmitPending(string sam, string title, string dept, string org, string office)
        => db.SubmitPending(sam, title, dept, org, office);
    public void ApprovePending(string sam) => db.ApprovePending(sam);
    public void RejectPending(string sam, string reason = "") => db.RejectPending(sam, reason);
    public List<CardProfile> GetPending()  => db.GetPending();
}
