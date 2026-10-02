using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using DBPBusinessCardEditable.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DBPBusinessCardEditable.Services;

/// <summary>
/// Looks a user up in Active Directory over LDAP, binding as a dedicated
/// read-only service account. The end user's credentials are never used here —
/// the browser only ever sent a Kerberos ticket.
/// </summary>
public sealed class LdapDirectoryService(
    IOptions<DirectoryOptions> options,
    IMemoryCache cache,
    ILogger<LdapDirectoryService> logger)
{
    private readonly DirectoryOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Finds a user by sAMAccountName or userPrincipalName.
    /// Returns null when the directory is not configured or no matching entry exists.
    /// </summary>
    public DirectoryUser? Find(string? samAccountName, string? userPrincipalName)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning("Directory lookup skipped: the Directory section is not configured.");
            return null;
        }

        var cacheKey = $"ad:{samAccountName}|{userPrincipalName}";
        if (cache.TryGetValue(cacheKey, out DirectoryUser? cached))
            return cached;

        var user = Query(samAccountName, userPrincipalName);

        if (user is not null)
            cache.Set(cacheKey, user, TimeSpan.FromSeconds(Math.Max(1, _options.CacheSeconds)));

        return user;
    }

    private DirectoryUser? Query(string? samAccountName, string? userPrincipalName)
    {
        var clauses = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(samAccountName))
            clauses.Append($"(sAMAccountName={EscapeFilterValue(samAccountName)})");

        if (!string.IsNullOrWhiteSpace(userPrincipalName))
            clauses.Append($"(userPrincipalName={EscapeFilterValue(userPrincipalName)})");

        if (clauses.Length == 0)
            return null;

        // Only real, enabled user objects — exclude computers and disabled accounts.
        var filter =
            "(&(objectCategory=person)(objectClass=user)" +
            "(!(userAccountControl:1.2.840.113556.1.4.803:=2))" +
            $"(|{clauses}))";

        using var connection = CreateConnection();
        connection.Bind();

        var request = new SearchRequest(
            _options.BaseDn,
            filter,
            SearchScope.Subtree,
            DirectoryUser.Attributes);

        var response = (SearchResponse)connection.SendRequest(request);

        if (response.Entries.Count == 0)
        {
            logger.LogInformation("No directory entry matched sAMAccountName={Sam}.", samAccountName);
            return null;
        }

        var entry = response.Entries[0];

        return new DirectoryUser(
            GetString(entry, "displayName"),
            GetString(entry, "title"),
            GetString(entry, "department"),
            GetString(entry, "mail"),
            GetString(entry, "telephoneNumber"),
            GetBytes(entry, "thumbnailPhoto"));
    }

    private LdapConnection CreateConnection()
    {
        var identifier = new LdapDirectoryIdentifier(_options.Host, _options.Port);

        var connection = new LdapConnection(identifier)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // If a service account password is configured, bind explicitly as that account.
        // If no password is provided (local dev / integrated Windows session), let the
        // OS pass the current user's Kerberos/NTLM ticket automatically — no password needed.
        if (!string.IsNullOrWhiteSpace(_options.ServiceAccountPassword))
        {
            connection.AuthType  = AuthType.Negotiate;
            connection.Credential = new NetworkCredential(
                _options.ServiceAccountUser,
                _options.ServiceAccountPassword);
        }
        else
        {
            // Pass-through: binds as the Windows account the app process is running under.
            // Works on a domain-joined machine without storing a password anywhere.
            connection.AuthType  = AuthType.Negotiate;
            connection.Credential = null;
        }

        connection.SessionOptions.ProtocolVersion  = 3;
        connection.SessionOptions.ReferralChasing  = ReferralChasingOptions.None;

        if (_options.UseLdaps)
            connection.SessionOptions.SecureSocketLayer = true;

        return connection;
    }

    private static string? GetString(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute))
            return null;

        var value = entry.Attributes[attribute][0]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static byte[]? GetBytes(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute))
            return null;

        var values = entry.Attributes[attribute].GetValues(typeof(byte[]));
        return values.Length > 0 ? (byte[])values[0] : null;
    }

    /// <summary>
    /// RFC 4515 escaping. The account name comes from a Kerberos ticket rather
    /// than raw user input, but escaping keeps the filter injection-proof.
    /// </summary>
    private static string EscapeFilterValue(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\5c",
                '*'  => "\\2a",
                '('  => "\\28",
                ')'  => "\\29",
                '\0' => "\\00",
                '/'  => "\\2f",
                _    => c.ToString()
            });
        }

        return builder.ToString();
    }
}
