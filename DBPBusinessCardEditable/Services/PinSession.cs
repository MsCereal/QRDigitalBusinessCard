namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Remembers, per login session, that the employee has entered their card PIN ("unlocked"),
/// and limits PIN guessing.
/// The PIN itself is OPTIONAL - this only matters for cards that have one.
///
/// - The unlock is tied to the signed-in employee (SAM), so it never carries over to someone
///   else who signs in on the same browser.
/// - Wrong PINs are counted on the SERVER per employee (see <see cref="AttemptLimiter"/>),
///   so signing out, clearing cookies or using another browser does not reset the count.
/// </summary>
public static class PinSession
{
    private const string UnlockKey = "PIN_OK_UNTIL";

    /// <summary>How long a correct PIN keeps the card unlocked (refreshed each time it is used).</summary>
    public static readonly TimeSpan UnlockFor = TimeSpan.FromMinutes(15);
    public const int MaxFailures = 5;

    private static string LimiterKey(string sam) => "pin:" + sam.Trim();

    public static bool IsUnlocked(ISession session, string sam)
    {
        // Stored as "sam|ticks"
        var raw = session.GetString(UnlockKey);
        if (string.IsNullOrEmpty(raw)) return false;
        var bar = raw.LastIndexOf('|');
        if (bar <= 0 || !long.TryParse(raw[(bar + 1)..], out var ticks)) return false;
        if (!string.Equals(raw[..bar], sam.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return new DateTime(ticks, DateTimeKind.Utc) > DateTime.UtcNow;
    }

    /// <summary>Mark the card unlocked for this employee (also clears the wrong-PIN count).</summary>
    public static void Unlock(ISession session, string sam)
    {
        session.SetString(UnlockKey, $"{sam.Trim()}|{DateTime.UtcNow.Add(UnlockFor).Ticks}");
        AttemptLimiter.Succeeded(LimiterKey(sam));
    }

    public static void Lock(ISession session) => session.Remove(UnlockKey);

    /// <summary>True while PIN attempts for this employee are blocked after too many wrong PINs.</summary>
    public static bool IsLockedOut(string sam, out TimeSpan remaining)
        => AttemptLimiter.IsLockedOut(LimiterKey(sam), out remaining);

    /// <summary>
    /// Check a PIN with the attempt limit applied. The attempt is counted BEFORE the PIN is checked.
    /// Returns true only when the PIN is right. When locked, <paramref name="lockedFor"/> is set.
    /// </summary>
    public static bool TryVerify(string sam, Func<bool> verify, out TimeSpan lockedFor)
    {
        if (!AttemptLimiter.TryBeginAttempt(LimiterKey(sam), MaxFailures, out lockedFor))
            return false;
        if (verify())
        {
            AttemptLimiter.Succeeded(LimiterKey(sam));
            lockedFor = TimeSpan.Zero;
            return true;
        }
        AttemptLimiter.IsLockedOut(LimiterKey(sam), out lockedFor);   // was that the last allowed try?
        return false;
    }

    public static string LockoutMessage(TimeSpan remaining)
        => $"Too many wrong PINs. Please wait {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minute(s) and try again.";
}
