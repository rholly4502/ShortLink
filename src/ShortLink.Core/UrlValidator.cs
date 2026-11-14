using ShortLink.Core.Abstractions;

namespace ShortLink.Core;

/// <summary>Validasi URL: hanya http/https absolute, cegah open-redirect/trik userinfo.</summary>
public sealed class UrlValidator : IUrlValidator
{
    public bool IsValid(string url) => TryNormalize(url, out _);

    public bool TryNormalize(string input, out Uri normalized)
    {
        normalized = null!;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Cegah open-redirect/penyalahgunaan: tolak URL berisi kredensial
        // (mis. https://evil.com@trusted.com) — bisa menipu pengguna.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        normalized = uri;
        return true;
    }
}
