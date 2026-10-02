namespace DBPBusinessCardEditable.Models;

/// <summary>
/// The attributes this app pulls out of Active Directory for a user lookup.
/// </summary>
public sealed record DirectoryUser(
    string? DisplayName,
    string? Title,
    string? Department,
    string? Mail,
    string? TelephoneNumber,
    byte[]? ThumbnailPhoto)
{
    public static readonly string[] Attributes =
    [
        "displayName",
        "title",
        "department",
        "mail",
        "telephoneNumber",
        "thumbnailPhoto"
    ];
}
