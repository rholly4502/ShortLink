using ShortLink.Core.Abstractions;
using ShortLink.Core.Auth;
using ShortLink.Core.Entities;

namespace ShortLink.Core;

/// <summary>Domain logic auth: register/login/refresh.</summary>
public sealed class AuthService
{
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

        if (await _users.ExistsAsync(username, email, ct))
            throw new InvalidOperationException("Username atau email sudah terdaftar.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username.Trim(),
            Email = email.Trim(),
            PasswordHash = _hasher.Hash(password),
            Role = UserRole.User,
            CreatedAt = _clock.UtcNow
        };

        await _users.AddAsync(user, ct);
        return user;
    }

    public async Task<TokenPair> LoginAsync(string identifier, string password, CancellationToken ct = default)
    {
        var user = await _users.GetByUsernameAsync(identifier, ct)
                   ?? await _users.GetByEmailAsync(identifier, ct);

        if (user is null || !_hasher.Verify(user.PasswordHash, password))
            throw new UnauthorizedAccessException("Kredensial salah.");

        return _tokens.Generate(user);
    }

    public async Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var userId = _tokens.ValidateRefreshToken(refreshToken);
        if (userId is null)
            throw new UnauthorizedAccessException("Refresh token tidak valid.");

        var user = await _users.GetByIdAsync(userId.Value, ct);
        if (user is null)
            throw new UnauthorizedAccessException("User tidak ditemukan.");

        return _tokens.Generate(user);
    }
}
