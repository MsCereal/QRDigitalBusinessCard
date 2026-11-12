using System.Collections.Concurrent;

namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Counts wrong attempts (PIN, admin password) on the SERVER, per key — for example "pin:jcruz"
/// or "admin:10.1.2.3". Unlike a browser session, this cannot be reset by signing out,
/// clearing cookies or opening a new browser.
/// After <c>maxFailures</c> wrong tries the key is locked; each further lockout in a row lasts longer
/// (5 min, 15 min, 1 hour). A correct attempt clears the record.
/// Kept in memory: an app restart clears it (acceptable for a single-server internal app).
/// </summary>
public static class AttemptLimiter
{
    private sealed class Entry
    {
        public int Failures;          // wrong tries since the last lockout / success
        public int Lockouts;          // lockouts in a row (makes the next one longer)
        public DateTime LockedUntil;  // UTC
        public DateTime LastSeen;     // UTC, for cleanup
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan[] LockSteps = { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1) };

    /// <summary>True while this key is locked. <paramref name="remaining"/> = time left.</summary>
    public static bool IsLockedOut(string key, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!Entries.TryGetValue(key, out var e)) return false;
        lock (e)
        {
            var now = DateTime.UtcNow;
            if (e.LockedUntil <= now) return false;
            remaining = e.LockedUntil - now;
            return true;
        }
    }

    /// <summary>
    /// Reserve one attempt BEFORE checking the secret, so many requests sent at the same time
    /// cannot all slip through. Returns false (and the wait time) if the key is locked.
    /// Call <see cref="Succeeded"/> if the secret was right; otherwise the attempt stays counted.
    /// </summary>
    public static bool TryBeginAttempt(string key, int maxFailures, out TimeSpan remaining)
    {
        Cleanup();
        var e = Entries.GetOrAdd(key, _ => new Entry());
        lock (e)
        {
            var now = DateTime.UtcNow;
            e.LastSeen = now;
            if (e.LockedUntil > now) { remaining = e.LockedUntil - now; return false; }

            e.Failures++;
            if (e.Failures >= maxFailures)
            {
                var step = LockSteps[Math.Min(e.Lockouts, LockSteps.Length - 1)];
                e.LockedUntil = now.Add(step);
                e.Lockouts++;
                e.Failures = 0;
            }
            remaining = TimeSpan.Zero;
            return true;   // this attempt may proceed (if it was the last allowed one, the lock starts now)
        }
    }

    /// <summary>The secret was right: forget all failures for this key.</summary>
    public static void Succeeded(string key) => Entries.TryRemove(key, out _);

    public static string LockoutMessage(TimeSpan remaining)
        => $"Too many wrong attempts. Please wait {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minute(s) and try again.";

    // Drop old records now and then so memory stays small
    private static DateTime _nextCleanup = DateTime.MinValue;
    private static void Cleanup()
    {
        var now = DateTime.UtcNow;
        if (now < _nextCleanup) return;
        _nextCleanup = now.AddMinutes(10);
        foreach (var kv in Entries)
            if (kv.Value.LockedUntil < now && kv.Value.LastSeen < now.AddHours(-6))
                Entries.TryRemove(kv.Key, out _);
    }
}
