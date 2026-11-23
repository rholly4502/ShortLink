using System.ComponentModel.DataAnnotations;
using System.Linq;
using ShortLink.Api.Models;

namespace ShortLink.Core.Tests;

public class DtoValidationTests
{
    private static bool IsValid(object dto)
    {
        var type = dto.GetType();
        var ctor = type.GetConstructors().Single();
        var results = new List<ValidationResult>();
        foreach (var parameter in ctor.GetParameters())
        {
            var value = type.GetProperty(parameter.Name!)!.GetValue(dto);
            var attributes = parameter.GetCustomAttributes(true).OfType<ValidationAttribute>();
            var ok = Validator.TryValidateValue(
                value ?? string.Empty,
                new ValidationContext(dto) { MemberName = parameter.Name },
                results,
                attributes);
            if (!ok)
                return false;
        }
        return true;
    }

    // --- Register ---

    [Fact]
    public void Register_ValidPayload_Passes() =>
        Assert.True(IsValid(new RegisterRequest("user01", "user@example.com", "pass12345")));

    [Theory]
    [InlineData(null, "user@example.com", "pass12345")]
    [InlineData("", "user@example.com", "pass12345")]
    [InlineData("us", "user@example.com", "pass12345")]
    [InlineData("user01", null, "pass12345")]
    [InlineData("user01", "not-an-email", "pass12345")]
    [InlineData("user01", "user@example.com", "short")]
    [InlineData("user01", "user@example.com", null)]
    public void Register_InvalidPayload_Fails(string? u, string? e, string? p) =>
        Assert.False(IsValid(new RegisterRequest(u!, e!, p!)));

    // --- Login ---

    [Fact]
    public void Login_ValidPayload_Passes() => Assert.True(IsValid(new LoginRequest("user01", "pass12345")));

    [Theory]
    [InlineData(null, "pass12345")]
    [InlineData("user01", null)]
    public void Login_InvalidPayload_Fails(string? i, string? p) =>
        Assert.False(IsValid(new LoginRequest(i!, p!)));

    [Fact]
    public void Login_OversizedPassword_Fails() =>
        Assert.False(IsValid(new LoginRequest("user01", new string('a', 73))));

    [Fact]
    public void Register_OversizedPassword_Fails() =>
        Assert.False(IsValid(new RegisterRequest("user01", "user@example.com", new string('a', 73))));

    // --- Refresh ---

    [Fact]
    public void Refresh_ValidPayload_Passes() => Assert.True(IsValid(new RefreshRequest("token-abc")));

    [Fact]
    public void Refresh_MissingToken_Fails() => Assert.False(IsValid(new RefreshRequest(null!)));

    [Fact]
    public void Refresh_OversizedToken_Fails() =>
        Assert.False(IsValid(new RefreshRequest(new string('a', 4097))));

    // --- CreateLink / UpdateLink ---

    [Fact]
    public void CreateLink_ValidUrl_Passes() => Assert.True(IsValid(new CreateLinkRequest("https://example.com", null)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateLink_MissingUrl_Fails(string? url) => Assert.False(IsValid(new CreateLinkRequest(url!, null)));

    [Fact]
    public void UpdateLink_MissingUrl_Fails() => Assert.False(IsValid(new UpdateLinkRequest(null!, null)));

    [Fact]
    public void CreateLink_ValidCustomSlug_Passes() =>
        Assert.True(IsValid(new CreateLinkRequest("https://example.com", null, "promo2026")));

    [Fact]
    public void CreateLink_CustomSlugTooLong_Fails() =>
        Assert.False(IsValid(new CreateLinkRequest("https://example.com", null, new string('a', 33))));
}
