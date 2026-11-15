using Microsoft.AspNetCore.Mvc;

namespace ShortLink.Api.Errors;

/// <summary>Pemetaan sentral exception → status HTTP + hasil controller agar konsisten di semua endpoint.</summary>
public static class ErrorMapper
{
    public static (int Status, string Message) Map(Exception ex) => ex switch
    {
        KeyNotFoundException => (404, ex.Message),
        UnauthorizedAccessException => (404, "Sumber daya tidak ditemukan."),
        InvalidOperationException => (409, ex.Message),
        ArgumentException => (400, ex.Message),
        _ => (500, "Terjadi kesalahan internal.")
    };

    /// <summary>Peta exception ke ActionResult standar: 400/409 berenvelope ApiError, 404 bare, lainnya 500.</summary>
    public static ActionResult<T> Result<T>(Exception ex) where T : class
    {
        var (status, message) = Map(ex);
        return status switch
        {
            400 => new BadRequestObjectResult(new ApiError(message)),
            409 => new ConflictObjectResult(new ApiError(message)),
            404 => new NotFoundResult(),
            _ => new ObjectResult(new ApiError(message)) { StatusCode = 500 }
        };
    }

    /// <summary>Varian non-generik untuk action bertipe IActionResult (mis. DELETE).</summary>
    public static IActionResult Result(Exception ex)
    {
        var (status, message) = Map(ex);
        return status switch
        {
            400 => new BadRequestObjectResult(new ApiError(message)),
            409 => new ConflictObjectResult(new ApiError(message)),
            404 => new NotFoundResult(),
            _ => new ObjectResult(new ApiError(message)) { StatusCode = 500 }
        };
    }
}
