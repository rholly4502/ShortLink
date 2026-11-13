using System.Reflection;
using ShortLink.Core.Abstractions;

namespace ShortLink.Core.Tests;

public class AbstractionsTests
{
    [Fact]
    public void CoreAbstractions_AllExistAsPublicInterfaces()
    {
        var types = new[]
        {
            typeof(ISlugGenerator),
            typeof(IUrlValidator),
            typeof(IClock),
            typeof(IShortLinkRepository),
            typeof(IClickRepository),
            typeof(IUserRepository),
        };

        Assert.All(types, t => Assert.True(t.IsInterface && t.IsPublic));
    }

    [Fact]
    public void ISlugGenerator_DeclaresGenerateReturningString()
    {
        var method = typeof(ISlugGenerator).GetMethod(nameof(ISlugGenerator.Generate));

        Assert.NotNull(method);
        Assert.Equal(typeof(string), method!.ReturnType);
    }

    [Fact]
    public void IClock_ExposesUtcNow()
    {
        var prop = typeof(IClock).GetProperty(nameof(IClock.UtcNow));

        Assert.NotNull(prop);
        Assert.Equal(typeof(DateTimeOffset), prop!.PropertyType);
    }

    [Fact]
    public void IUrlValidator_ValidatesAndNormalizes()
    {
        Assert.NotNull(typeof(IUrlValidator).GetMethod(nameof(IUrlValidator.IsValid)));
        Assert.NotNull(typeof(IUrlValidator).GetMethod(nameof(IUrlValidator.TryNormalize)));
    }

    [Fact]
    public void IShortLinkRepository_DeclaresCoreOperations()
    {
        var members = typeof(IShortLinkRepository).GetMethods().Select(m => m.Name).ToHashSet();

        Assert.Contains(nameof(IShortLinkRepository.GetBySlugAsync), members);
        Assert.Contains(nameof(IShortLinkRepository.GetByIdAsync), members);
        Assert.Contains(nameof(IShortLinkRepository.GetByOwnerAsync), members);
        Assert.Contains(nameof(IShortLinkRepository.AddAsync), members);
        Assert.Contains(nameof(IShortLinkRepository.UpdateAsync), members);
        Assert.Contains(nameof(IShortLinkRepository.DeleteAsync), members);
    }

    [Fact]
    public void IClickRepository_DeclaresWriteOperations()
    {
        var members = typeof(IClickRepository).GetMethods().Select(m => m.Name).ToHashSet();

        Assert.Contains(nameof(IClickRepository.AddAsync), members);
        Assert.Contains(nameof(IClickRepository.AddAggregateAsync), members);
    }

    [Fact]
    public void IUserRepository_DeclaresLookupAndWrite()
    {
        var members = typeof(IUserRepository).GetMethods().Select(m => m.Name).ToHashSet();

        Assert.Contains(nameof(IUserRepository.GetByIdAsync), members);
        Assert.Contains(nameof(IUserRepository.GetByUsernameAsync), members);
        Assert.Contains(nameof(IUserRepository.GetByEmailAsync), members);
        Assert.Contains(nameof(IUserRepository.AddAsync), members);
    }
}
