using ShortLink.Core.Entities;

namespace ShortLink.Core.Abstractions;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);

    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);

    Task<bool> ExistsAsync(string username, string email, CancellationToken ct = default);

    Task AddAsync(User user, CancellationToken ct = default);
}
