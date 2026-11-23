using System.Text;
using ShortLink.Core.Abstractions;
using ShortLink.Core.Auth;
using ShortLink.Core.Entities;

namespace ShortLink.Core;

/// <summary>Domain logic auth: register/login/refresh.</summary>
public sealed class AuthService
{
    public const int MaxUsernameLength = 50;
    public const int MaxEmailLength = 254;
    public const int MaxPasswordBytes = 72;

    private const string DummyPassword = "__shortlink_dummy_password__";
    private const string DummyPasswordHash = "$2a$11$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy";

    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly IAuthTokenService _tokens;
    private readonly IClock _clock;

    public AuthService(IUserRepository users, IPasswordHasher hasher, IAuthTokenService tokens, IClock clock)
    {
        _users = users;
        _hasher = hasher;
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<User> RegisterAsync(string username, string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Username, email, dan password wajib diisi.");

        var normalizedUsername = username.Trim();
        var normalizedEmail = email.Trim();

        if (normalizedUsername.Length > MaxUsernameLength || ContainsWhitespaceOrControl(normalizedUsername))
            throw new ArgumentException("Username tidak valid.", nameof(username));

        if (normalizedEmail.Length > MaxEmailLength || ContainsWhitespaceOrControl(normalizedEmail))
            throw new ArgumentException("Email tidak valid.", nameof(email));

        if (ContainsControlChar(password) || Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes)
            throw new ArgumentException("Password tidak valid atau melebihi batas 72 byte UTF-8.", nameof(password));

        if (await _users.ExistsAsync(normalizedUsername, normalizedEmail, ct))
            throw new InvalidOperationException("Username atau email sudah terdaftar.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = normalizedUsername,
            Email = normalizedEmail,
            PasswordHash = _hasher.Hash(password),
            Role = UserRole.User,
            CreatedAt = _clock.UtcNow
        };

        await _users.AddAsync(user, ct);
        return user;
    }

    public async Task<TokenPair> LoginAsync(string identifier, string password, CancellationToken ct = default)
    {
        var isPasswordValid = !string.IsNullOrEmpty(password)
                              && !ContainsControlChar(password)
                              && Encoding.UTF8.GetByteCount(password) <= MaxPasswordBytes;

        if (string.IsNullOrWhiteSpace(identifier))
        {
            _ = _hasher.Verify(DummyPasswordHash, isPasswordValid ? password : DummyPassword);
            throw new UnauthorizedAccessException("Kredensial salah.");
        }

        var normalizedIdentifier = identifier.Trim();
        if (normalizedIdentifier.Length > MaxEmailLength || ContainsControlChar(normalizedIdentifier))
        {
            _ = _hasher.Verify(DummyPasswordHash, isPasswordValid ? password : DummyPassword);
            throw new UnauthorizedAccessException("Kredensial salah.");
        }

        var user = await _users.GetByUsernameAsync(normalizedIdentifier, ct)
                   ?? await _users.GetByEmailAsync(normalizedIdentifier, ct);

        if (user is null)
        {
            _ = _hasher.Verify(DummyPasswordHash, isPasswordValid ? password : DummyPassword);
            throw new UnauthorizedAccessException("Kredensial salah.");
        }

        if (!isPasswordValid)
        {
            _ = _hasher.Verify(user.PasswordHash, DummyPassword);
            throw new UnauthorizedAccessException("Kredensial salah.");
        }

        if (!_hasher.Verify(user.PasswordHash, password))
            throw new UnauthorizedAccessException("Kredensial salah.");

        return _tokens.Generate(user);
    }

    public async Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new UnauthorizedAccessException("Refresh token tidak valid.");

        var userId = _tokens.ValidateRefreshToken(refreshToken);
        if (userId is null)
            throw new UnauthorizedAccessException("Refresh token tidak valid.");

        var user = await _users.GetByIdAsync(userId.Value, ct);
        if (user is null)
            throw new UnauthorizedAccessException("User tidak ditemukan.");

        return _tokens.Generate(user);
    }

    private static bool ContainsWhitespaceOrControl(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch <= 0x1F || ch == 0x7F)
                return true;
        }

        return false;
    }

    private static bool ContainsControlChar(string value)
    {
        foreach (var ch in value)
        {
            if (ch <= 0x1F || ch == 0x7F)
                return true;
        }

        return false;
    }
}
