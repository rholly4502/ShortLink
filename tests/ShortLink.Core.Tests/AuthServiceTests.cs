using ShortLink.Core.Abstractions;
using ShortLink.Core.Auth;
using ShortLink.Core.Entities;

namespace ShortLink.Core.Tests;

public sealed class AuthServiceTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeUserRepository : IUserRepository
    {
        public List<User> Users { get; } = new();
        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Users.FirstOrDefault(u => u.Id == id));
        public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
            => Task.FromResult(Users.FirstOrDefault(u => u.Username == username));
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
            => Task.FromResult(Users.FirstOrDefault(u => u.Email == email));
        public Task<bool> ExistsAsync(string username, string email, CancellationToken ct = default)
            => Task.FromResult(Users.Any(u => u.Username == username || u.Email == email));
        public Task AddAsync(User user, CancellationToken ct = default)
        {
            Users.Add(user);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string hashed, string password) => hashed == "h:" + password;
    }

    private sealed class FakeTokenService : IAuthTokenService
    {
        public Guid? ValidUserId { get; set; }
        public User? LastUser { get; private set; }
        public TokenPair Generate(User user)
        {
            LastUser = user;
            return new TokenPair("access-token", "refresh-token", DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddDays(7));
        }
        public Guid? ValidateRefreshToken(string refreshToken)
            => refreshToken == "valid-refresh" ? ValidUserId : null;
    }

    private readonly FakeUserRepository _users = new();
    private readonly AuthService _svc;

    public AuthServiceTests()
    {
        _svc = new AuthService(_users, new FakeHasher(), new FakeTokenService(), new FakeClock());
    }

    [Fact]
    public async Task Register_CreatesUserWithHashedPasswordAndUserRole()
    {
        var user = await _svc.RegisterAsync("alice", "alice@x.com", "secret");

        Assert.Equal("alice", user.Username);
        Assert.Equal("alice@x.com", user.Email);
        Assert.Equal("h:secret", user.PasswordHash);
        Assert.Equal(UserRole.User, user.Role);
        Assert.Contains(_users.Users, u => u.Id == user.Id);
    }

    [Fact]
    public async Task Register_Throws_WhenUsernameOrEmailExists()
    {
        await _svc.RegisterAsync("alice", "alice@x.com", "secret");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.RegisterAsync("alice", "other@x.com", "pw"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.RegisterAsync("bob", "alice@x.com", "pw"));
    }

    [Theory]
    [InlineData("", "a@x.com", "pw")]
    [InlineData(null, "a@x.com", "pw")]
    [InlineData("  ", "a@x.com", "pw")]
    [InlineData("a", "", "pw")]
    [InlineData("a", "a@x.com", "")]
    public async Task Register_Throws_WhenRequiredFieldEmpty(string username, string email, string password)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _svc.RegisterAsync(username!, email, password));
    }

    [Fact]
    public async Task Login_ReturnsTokenPair_WhenCredentialsValid()
    {
        await _svc.RegisterAsync("alice", "alice@x.com", "secret");

        var pair = await _svc.LoginAsync("alice", "secret");

        Assert.Equal("access-token", pair.AccessToken);
        Assert.Equal("refresh-token", pair.RefreshToken);
    }

    [Fact]
    public async Task Login_Throws_WhenWrongPassword()
    {
        await _svc.RegisterAsync("alice", "alice@x.com", "secret");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.LoginAsync("alice", "wrong"));
    }

    [Fact]
    public async Task Login_Throws_WhenUnknownUser()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.LoginAsync("ghost", "secret"));
    }

    [Fact]
    public async Task Refresh_ReturnsNewTokenPair_WhenTokenValid()
    {
        await _svc.RegisterAsync("alice", "alice@x.com", "secret");
        var alice = _users.Users.Single();
        var tokenSvc = new FakeTokenService { ValidUserId = alice.Id };
        var svc = new AuthService(_users, new FakeHasher(), tokenSvc, new FakeClock());

        var pair = await svc.RefreshAsync("valid-refresh");

        Assert.Equal("access-token", pair.AccessToken);
    }

    [Fact]
    public async Task Refresh_Throws_WhenTokenInvalid()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.RefreshAsync("garbage"));
    }

    [Fact]
    public async Task Refresh_Throws_WhenUserMissing()
    {
        var tokenSvc = new FakeTokenService { ValidUserId = Guid.NewGuid() };
        var svc = new AuthService(_users, new FakeHasher(), tokenSvc, new FakeClock());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync("valid-refresh"));
    }
}
