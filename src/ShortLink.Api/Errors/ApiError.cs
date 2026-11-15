namespace ShortLink.Api.Errors;

/// <summary>Envelope error konsisten untuk body respons berstatus error (4xx/5xx).</summary>
public sealed record ApiError(string Error);
