namespace Orion.Infrastructure.Games;

public static class GamePackageSource
{
    public static bool IsSupported(Uri uri) => uri.IsAbsoluteUri && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsDefaultPort &&
            uri.Host is "assets1.xboxlive.com" or "assets2.xboxlive.com");
}
