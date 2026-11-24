using System.Text;
using DBPBusinessCardEditable.Models;

namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Builds a contact (vCard 3.0) from a card, for the "Save Contact" QR code.
/// The contact details are stored INSIDE the QR code, so a phone can save the contact
/// even with no internet and no access to the eCard website.
/// Only fields the employee chose to show are included. The photo is left out
/// (too big for a QR code).
/// </summary>
public static class VCard
{
    public static string Build(CardProfile p, string? cardUrl)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");
        static bool Has(bool show, string? v) => show && !string.IsNullOrWhiteSpace(v);

        bool hasNick = Has(p.ShowPreferredName, p.PreferredName);
        string fullName = Has(p.ShowName, p.Name) ? p.Name.Trim()
                        : hasNick ? p.PreferredName.Trim()
                        : "DBP Employee";

        Line("BEGIN:VCARD");
        Line("VERSION:3.0");
        Line("N:;" + Esc(fullName) + ";;;");      // full name as given name (names are not split reliably)
        Line("FN:" + Esc(fullName));
        if (hasNick && Has(p.ShowName, p.Name)) Line("NICKNAME:" + Esc(p.PreferredName));

        if (Has(p.ShowOrg, p.Org))
            Line("ORG:" + Esc(p.Org) + (Has(p.ShowDepartment, p.Department) ? ";" + Esc(p.Department) : ""));
        else if (Has(p.ShowDepartment, p.Department))
            Line("ORG:;" + Esc(p.Department));
        if (Has(p.ShowTitle, p.Title))   Line("TITLE:" + Esc(p.Title));
        if (Has(p.ShowPhone, p.Phone))   Line("TEL;TYPE=CELL,VOICE:" + Esc(p.Phone));
        if (Has(p.ShowEmail, p.Email))   Line("EMAIL;TYPE=INTERNET,WORK:" + Esc(p.Email));
        if (Has(p.ShowOffice, p.Office)) Line("ADR;TYPE=WORK:;;" + Esc(p.Office) + ";;;;");

        // Link to the full online card (works whenever the phone can reach the eCard site)
        if (!string.IsNullOrWhiteSpace(cardUrl)) Line("URL:" + Esc(cardUrl));
        if (Has(p.ShowGitHub, p.GitHub)       && SafeLinks.IsWebUrl(p.GitHub))    Line("URL:" + Esc(p.GitHub));
        if (Has(p.ShowLinkedIn, p.LinkedIn)   && SafeLinks.IsWebUrl(p.LinkedIn))  Line("URL:" + Esc(p.LinkedIn));
        if (Has(p.ShowPortfolio, p.Portfolio) && SafeLinks.IsWebUrl(p.Portfolio)) Line("URL:" + Esc(p.Portfolio));

        Line("END:VCARD");
        return sb.ToString();
    }

    /// <summary>vCard text escaping: \ ; , and line breaks (so a field cannot add fake lines).</summary>
    private static string Esc(string? v) =>
        (v ?? "").Trim()
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
}
