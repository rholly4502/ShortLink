using System.ComponentModel.DataAnnotations;
using ShortLink.Api.Models;

namespace ShortLink.Core.Tests;

public class DtoValidationTests
{
    private static bool IsValid(object dto)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
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
    public void Register_InvalidPayload_Fails(string u, string e, string p) =>
        Assert.False(IsValid(new RegisterRequest(u, e, p)));

    // --- Login ---

    [Fact]
    public void Login_ValidPayload_Passes() => Assert.True(IsValid(new LoginRequest("user01", "pass12345")));

    [Theory]
    [InlineData(null, "pass12345")]
    [InlineData("user01", null)]
    public void Login_InvalidPayload_Fails(string i, string p) =>
        Assert.False(IsValid(new LoginRequest(i, p)));

    // --- Refresh ---

    [Fact]
    public void Refresh_ValidPayload_Passes() => Assert.True(IsValid(new RefreshRequest("token-abc")));

    [Fact]
    public void Refresh_MissingToken_Fails() => Assert.False(IsValid(new RefreshRequest(null!)));

    // --- CreateLink / UpdateLink ---

    [Fact]
    public void CreateLink_ValidUrl_Passes() => Assert.True(IsValid(new CreateLinkRequest("https://example.com", null)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateLink_MissingUrl_Fails(string url) => Assert.False(IsValid(new CreateLinkRequest(url, null)));

    [Fact]
    public void UpdateLink_MissingUrl_Fails() => Assert.False(IsValid(new UpdateLinkRequest(null!, null)));
}
