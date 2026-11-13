using ShortLink.Core.Entities;

namespace ShortLink.Core.Tests;

public class EntitiesTests
{
    [Fact]
    public void User_DefaultsToRoleUser()
    {
        var user = new User();

        Assert.Equal(UserRole.User, user.Role);
    }

    [Fact]
    public void ShortenedUrl_DefaultsClickCountToZero()
    {
        var link = new ShortenedUrl();

        Assert.Equal(0L, link.ClickCount);
    }

    [Theory]
    [InlineData(0, UserRole.User)]
    [InlineData(1, UserRole.Admin)]
    public void UserRole_HasExpectedValues(int value, UserRole role)
    {
        Assert.Equal(value, (int)role);
    }

    [Fact]
    public void DeviceType_DefinesDesktopMobileTabletOther()
    {
        Assert.True(Enum.IsDefined(typeof(DeviceType), DeviceType.Desktop));
        Assert.True(Enum.IsDefined(typeof(DeviceType), DeviceType.Mobile));
        Assert.True(Enum.IsDefined(typeof(DeviceType), DeviceType.Tablet));
        Assert.True(Enum.IsDefined(typeof(DeviceType), DeviceType.Other));
    }

    [Fact]
    public void AggregatePeriod_DefinesHourlyAndDaily()
    {
        Assert.True(Enum.IsDefined(typeof(AggregatePeriod), AggregatePeriod.Hourly));
        Assert.True(Enum.IsDefined(typeof(AggregatePeriod), AggregatePeriod.Daily));
    }
}
