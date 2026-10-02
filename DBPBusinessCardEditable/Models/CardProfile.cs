using System;
using System.Collections.Generic;

namespace DBPBusinessCardEditable.Models
{
    // -----------------------------------------------------------------------
    // WindowsIdentity — set when a card is first loaded via /api/me so the
    // UI can show the signed-in Windows account alongside the card data.
    // Not persisted to cards.json (runtime-only / ignored by the serialiser
    // through the [System.Text.Json.Serialization.JsonIgnore] attribute).
    // -----------------------------------------------------------------------
    public class ExtraInfoItem
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>One entry in the card's audit trail.</summary>
    public class AuditEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string   Action    { get; set; } = "";   // "Created", "Updated", "Reset", "Activated", "Deactivated"
        public string   Detail    { get; set; } = "";   // human-readable summary of what changed
    }

    /// <summary>Lightweight daily view count bucket.</summary>
    public class DailyView
    {
        public string Date  { get; set; } = "";   // yyyy-MM-dd
        public int    Count { get; set; } = 0;
    }

    /// <summary>Individual card view event with IP and timestamp.</summary>
    public class ViewEvent
    {
        public string Timestamp  { get; set; } = "";   // ISO 8601 UTC
        public string IpAddress  { get; set; } = "";
        public string UserAgent  { get; set; } = "";
        public string ScanType   { get; set; } = "direct"; // "qr" or "direct"
    }

    public class CardProfile
    {
        // Permanent — SQLite primary key scope
        public string SamAccountName { get; set; } = ""; // Windows sAMAccountName (owner)
        public string EmpId     { get; set; } = "";
        public string CardToken { get; set; } = "";

        // Security — PIN stored as BCrypt hash (prefix "bcrypt:" distinguishes hashed from legacy plaintext)
        public string CardPin      { get; set; } = "";
        public bool   PinIsHashed  { get; set; } = false;
        public bool   IsActive     { get; set; } = true;

        // ATM card expiry — card auto-deactivates when this date is reached.
        // Format stored as UTC DateTime (end of the expiry month).
        // Displayed to the user as MM/YY (same as physical ATM card).
        public DateTime? CardExpiresAt { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsExpired => CardExpiresAt.HasValue && CardExpiresAt.Value < DateTime.UtcNow;

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsEffectivelyActive => IsActive && !IsExpired;

        // Publishing
        public bool      IsPublished { get; set; } = false;
        public DateTime? PublishedAt { get; set; }

        // ── Pending approval fields ──────────────────────────────────────────
        // When an employee changes Title/Department/Org/Office, the new value
        // goes here and waits for admin approval before going live on the card.
        public string  PendingTitle       { get; set; } = "";
        public string  PendingDepartment  { get; set; } = "";
        public string  PendingOrg         { get; set; } = "";
        public string  PendingOffice      { get; set; } = "";
        public string  PendingStatus      { get; set; } = "none"; // none | pending | approved | rejected
        public DateTime? PendingSubmittedAt { get; set; }
        public string  PendingSubmittedBy { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasPending => PendingStatus == "pending";

        // Personal
        public string Name              { get; set; } = "";
        public bool   ShowName          { get; set; } = true;
        public string PreferredName     { get; set; } = "";
        public bool   ShowPreferredName { get; set; } = false;
        public string Photo             { get; set; } = "";
        public bool   ShowPhoto         { get; set; } = true;

        // Professional
        public string Title          { get; set; } = "";
        public bool   ShowTitle      { get; set; } = true;
        public string Department     { get; set; } = "";
        public bool   ShowDepartment { get; set; } = true;
        public string Org            { get; set; } = "Development Bank of the Philippines";
        public bool   ShowOrg        { get; set; } = true;
        public string Office         { get; set; } = "";
        public bool   ShowOffice     { get; set; } = true;

        // Contact
        public string Phone     { get; set; } = "";
        public bool   ShowPhone { get; set; } = true;
        public string Email     { get; set; } = "";
        public bool   ShowEmail { get; set; } = true;

        // Links
        public string GitHub        { get; set; } = "";
        public bool   ShowGitHub    { get; set; } = true;
        public string LinkedIn      { get; set; } = "";
        public bool   ShowLinkedIn  { get; set; } = true;
        public string Portfolio     { get; set; } = "";
        public bool   ShowPortfolio { get; set; } = true;

        // Extra info
        public List<ExtraInfoItem> ExtraInfo { get; set; } = new List<ExtraInfoItem>();

        // Timestamps
        public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        // Analytics — daily view counts (capped at 90 days)
        public List<DailyView>  ViewHistory { get; set; } = new List<DailyView>();
        public int              TotalViews  { get; set; } = 0;

        // Viewer log — last 100 individual view events (IP + timestamp)
        public List<ViewEvent>  ViewLog     { get; set; } = new List<ViewEvent>();

        // Audit log (last 50 entries)
        public List<AuditEntry> AuditLog { get; set; } = new List<AuditEntry>();

        // -------------------------------------------------------------------
        // Runtime-only AD identity fields.
        // These are never written to cards.json — they are populated on the
        // fly by MeController when the signed-in Windows user is identified.
        // -------------------------------------------------------------------

        /// <summary>Windows sAMAccountName from the Kerberos/NTLM ticket.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? AdSamAccountName { get; set; }

        /// <summary>userPrincipalName from the ticket (may be null).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? AdUserPrincipalName { get; set; }

        /// <summary>Auth type reported by ASP.NET (e.g. "Kerberos" or "NTLM").</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? AdAuthType { get; set; }

        // -------------------------------------------------------------------
        // Convenience helper — copy AD values into the card's editable fields
        // only when those fields are currently blank. Lets the user still
        // override anything that was pre-populated from AD.
        // -------------------------------------------------------------------
        public void ApplyFromDirectory(DirectoryUser adUser)
        {
            if (adUser == null) return;

            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(adUser.DisplayName))
                Name = adUser.DisplayName;

            if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(adUser.Title))
                Title = adUser.Title;

            if (string.IsNullOrWhiteSpace(Department) && !string.IsNullOrWhiteSpace(adUser.Department))
                Department = adUser.Department;

            if (string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(adUser.Mail))
                Email = adUser.Mail;

            if (string.IsNullOrWhiteSpace(Phone) && !string.IsNullOrWhiteSpace(adUser.TelephoneNumber))
                Phone = adUser.TelephoneNumber;
        }
    }
}
