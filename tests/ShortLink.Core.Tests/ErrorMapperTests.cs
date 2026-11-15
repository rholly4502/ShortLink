using Microsoft.AspNetCore.Mvc;
using ShortLink.Api.Errors;
using ShortLink.Api.Models;

namespace ShortLink.Core.Tests;

public class ErrorMapperTests
{
    [Fact]
    public void Map_ArgumentException_Returns400()
    {
        var (status, message) = ErrorMapper.Map(new ArgumentException("URL tidak valid"));
        Assert.Equal(400, status);
        Assert.Equal("URL tidak valid", message);
    }

    [Fact]
    public void Map_InvalidOperationException_Returns409()
    {
        var (status, message) = ErrorMapper.Map(new InvalidOperationException("conflict"));
        Assert.Equal(409, status);
        Assert.Equal("conflict", message);
    }

    [Fact]
    public void Map_KeyNotFoundException_Returns404()
    {
        var (status, message) = ErrorMapper.Map(new KeyNotFoundException("tidak ditemukan"));
        Assert.Equal(404, status);
        Assert.Equal("tidak ditemukan", message);
    }

    [Fact]
    public void Map_UnauthorizedAccessException_Returns404Masked()
    {
        var (status, message) = ErrorMapper.Map(new UnauthorizedAccessException());
        Assert.Equal(404, status);
        Assert.Equal("Sumber daya tidak ditemukan.", message);
    }

    [Fact]
    public void Map_UnknownException_Returns500()
    {
        var (status, message) = ErrorMapper.Map(new Exception("boom"));
        Assert.Equal(500, status);
        Assert.Equal("Terjadi kesalahan internal.", message);
    }

    [Fact]
    public void Result_ArgumentException_ReturnsBadRequestWithEnvelope()
    {
        var result = Assert.IsType<BadRequestObjectResult>(ErrorMapper.Result<LinkResponse>(new ArgumentException("x")).Result);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("x", Assert.IsType<ApiError>(result.Value).Error);
    }

    [Fact]
    public void Result_InvalidOperation_ReturnsConflictWithEnvelope()
    {
        var result = Assert.IsType<ConflictObjectResult>(ErrorMapper.Result<LinkResponse>(new InvalidOperationException("c")).Result);
        Assert.Equal(409, result.StatusCode);
    }

    [Fact]
    public void Result_KeyNotFound_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(ErrorMapper.Result<LinkResponse>(new KeyNotFoundException("x")).Result);

    [Fact]
    public void Result_Unauthorized_ReturnsNotFound() =>
        Assert.IsType<NotFoundResult>(ErrorMapper.Result<LinkResponse>(new UnauthorizedAccessException()).Result);

    [Fact]
    public void Result_Unknown_Returns500Envelope()
    {
        var result = Assert.IsType<ObjectResult>(ErrorMapper.Result<LinkResponse>(new Exception("boom")).Result);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("Terjadi kesalahan internal.", Assert.IsType<ApiError>(result.Value).Error);
    }
}
