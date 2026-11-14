namespace ShortLink.Infrastructure.Security;

/// <summary>Konfigurasi JWT (section "Jwt").</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    /// <summary>Wajib &gt;= 32 byte untuk HS256.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenTtlMinutes { get; set; } = 15;
    public int RefreshTokenTtlDays { get; set; } = 7;
}
