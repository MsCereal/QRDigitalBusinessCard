using System;
using System.Collections.Generic;

namespace DBPBusinessCardEditable.Models
{
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

    public class CardProfile
    {
        // Permanent
        public string EmpId     { get; set; } = "";
        public string CardToken { get; set; } = "";

        // Security
        public string CardPin  { get; set; } = "";
        public bool   IsActive { get; set; } = true;

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
        public List<DailyView> ViewHistory { get; set; } = new List<DailyView>();
        public int TotalViews { get; set; } = 0;

        // Audit log (last 50 entries)
        public List<AuditEntry> AuditLog { get; set; } = new List<AuditEntry>();
    }
}
