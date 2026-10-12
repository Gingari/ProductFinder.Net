using System;

namespace ProductFinder.Services;

public static class EtmUrl
{
    public static bool IsAllowed(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host.Equals("etm.ru", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.etm.ru", StringComparison.OrdinalIgnoreCase));
    public static void Validate(string value)
    {
        if (!IsAllowed(value)) throw new ArgumentException("Разрешены только HTTPS-ссылки сайта etm.ru.");
    }
}
