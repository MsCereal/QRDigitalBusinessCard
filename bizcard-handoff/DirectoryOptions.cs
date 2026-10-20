namespace BizCard;

/// <summary>
/// Connection settings for the read-only LDAP lookup.
/// Bound from the "Directory" section of configuration.
/// </summary>
public sealed class DirectoryOptions
{
    /// <summary>Domain controller or LDAP load-balancer host name, e.g. "dc01.corp.example.com".</summary>
    public string Host { get; set; } = "";

    /// <summary>389 for plain LDAP, 636 for LDAPS.</summary>
    public int Port { get; set; } = 389;

    /// <summary>Wrap the connection in TLS. Set Port to 636 when enabling this.</summary>
    public bool UseLdaps { get; set; }

    /// <summary>Search root, e.g. "DC=corp,DC=example,DC=com".</summary>
    public string BaseDn { get; set; } = "";

    /// <summary>Read-only service account, e.g. "CORP\\svc-bizcard-ro" or a UPN.</summary>
    public string ServiceAccountUser { get; set; } = "";

    /// <summary>Never commit this. Supply via user-secrets or the Directory__ServiceAccountPassword env var.</summary>
    public string ServiceAccountPassword { get; set; } = "";

    /// <summary>How long a successful directory lookup is cached, in seconds.</summary>
    public int CacheSeconds { get; set; } = 300;

    /// <summary>True once enough is configured to attempt a bind.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(BaseDn) &&
        !string.IsNullOrWhiteSpace(ServiceAccountUser);
}
