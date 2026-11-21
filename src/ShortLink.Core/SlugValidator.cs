namespace ShortLink.Core;

/// <summary>Validasi alias/custom slug dan daftar path terreservasi agar tidak bertabrakan dengan route API, auth, atau SPA.</summary>
public static class SlugValidator
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Backend/API/infra routes
        "api",
        "auth",
        "openapi",
        "swagger",
        "health",
        "healthz",
        "ready",
        "readyz",
        "live",
        "livez",
        "metrics",

        // Subpath/static prefixes & assets
        "shortlink",
        "assets",
        "static",
        "public",
        "wwwroot",
        "index",
        "favicon",
        "icons",
        "robots",
        "sitemap",
        "manifest",

        // SPA routes & app segments
        "login",
        "register",
        "logout",
        "links",
        "analytics",
        "dashboard",
        "admin",
        "app",
        "home",
        "account",
        "profile",
        "settings"
    };

    public static IReadOnlySet<string> ReservedPaths => ReservedSlugs;

    public static bool IsReserved(string? slug)
        => !string.IsNullOrWhiteSpace(slug) && ReservedSlugs.Contains(slug.Trim());

    public static bool TryValidateCustomSlug(string? input, out string normalizedSlug, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            normalizedSlug = string.Empty;
            error = "Custom slug tidak boleh kosong.";
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            normalizedSlug = string.Empty;
            error = $"Custom slug harus terdiri dari {MinLength} hingga {MaxLength} karakter.";
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                normalizedSlug = string.Empty;
                error = "Custom slug hanya boleh berisi huruf ASCII dan angka (a-z, A-Z, 0-9).";
                return false;
            }
        }

        if (ReservedSlugs.Contains(trimmed))
        {
            normalizedSlug = string.Empty;
            error = $"Custom slug '{trimmed}' tidak dapat digunakan karena merupakan path terreservasi.";
            return false;
        }

        normalizedSlug = trimmed;
        error = null;
        return true;
    }
}
