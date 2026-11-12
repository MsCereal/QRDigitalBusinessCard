namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Keeps user-entered links safe. Only real web addresses (http/https) become clickable links,
/// so a value like "javascript:..." can never run code when a visitor clicks it.
/// Used when saving (to clean/check input) and when showing a card (as a second safety net).
/// </summary>
public static class SafeLinks
{
    /// <summary>True when the value is an absolute http:// or https:// address.</summary>
    public static bool IsWebUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>The value if it is a web address, otherwise "#" (a link that goes nowhere).</summary>
    public static string SafeUrl(string? value) => IsWebUrl(value) ? value!.Trim() : "#";

    /// <summary>
    /// Link for an "extra info" row: web address, phone number (tel:), or "#" for plain text
    /// such as a username.
    /// </summary>
    public static string ExtraHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#";
        var v = value.Trim();
        if (IsWebUrl(v)) return v;
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\+?[0-9][0-9 ()\-]{2,}$")) return "tel:" + v;
        return "#";
    }

    /// <summary>
    /// Cleans a link the employee typed. "github.com/jcruz" becomes "https://github.com/jcruz".
    /// Returns false when the value is not a usable web address (e.g. "javascript:...").
    /// </summary>
    public static bool TryNormalizeWebUrl(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return true;           // empty is fine (optional field)
        var v = value.Trim();
        if (!v.Contains("://", StringComparison.Ordinal))
        {
            // No scheme typed. Refuse anything that looks like another scheme ("javascript:", "data:")
            var colon = v.IndexOf(':');
            var slash = v.IndexOf('/');
            if (colon >= 0 && (slash < 0 || colon < slash)) return false;
            v = "https://" + v;
        }
        if (!IsWebUrl(v)) return false;
        normalized = v;
        return true;
    }
}
