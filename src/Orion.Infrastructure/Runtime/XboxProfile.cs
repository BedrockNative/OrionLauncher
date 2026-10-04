namespace Orion.Infrastructure.Runtime;

/// <summary>Presentation metadata only. No tokens, XUID, real name or credentials.</summary>
public sealed record XboxProfile(string Id, string Gamertag, string? PictureUrl)
{
    public bool IsValidFor(string id) => Id == id && !string.IsNullOrWhiteSpace(Gamertag)
        && Gamertag.Length <= 128 && !Gamertag.Any(char.IsControl)
        && (PictureUrl is null || IsTrustedPicture(PictureUrl));

    public static bool IsTrustedPicture(string value) => value.Length <= 2048
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.Port == 443 && uri.UserInfo.Length == 0
        && (uri.Host is "xboxlive.com" or "xbox.com"
            || uri.Host.EndsWith(".xboxlive.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".xbox.com", StringComparison.OrdinalIgnoreCase));
}
