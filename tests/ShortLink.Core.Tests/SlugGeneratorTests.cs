using ShortLink.Core;
using ShortLink.Core.Abstractions;

namespace ShortLink.Core.Tests;

public class SlugGeneratorTests
{
    private const string Base62Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [Fact]
    public void Generate_DefaultLengthIsSeven()
    {
        var sut = new SlugGenerator();

        Assert.Equal(7, sut.Generate().Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12)]
    public void Generate_RespectsRequestedLength(int length)
    {
        var sut = new SlugGenerator();

        Assert.Equal(length, sut.Generate(length).Length);
    }

    [Fact]
    public void Generate_UsesOnlyBase62Alphabet()
    {
        var sut = new SlugGenerator();

        for (var i = 0; i < 1000; i++)
        {
            Assert.All(sut.Generate(), c => Assert.Contains(c, Base62Alphabet));
        }
    }

    [Fact]
    public void Generate_ProducesDistinctSlugs()
    {
        var sut = new SlugGenerator();
        var seen = new HashSet<string>();

        for (var i = 0; i < 10000; i++)
        {
            Assert.True(seen.Add(sut.Generate()), $"Duplicate slug at iteration {i}");
        }
    }
}
