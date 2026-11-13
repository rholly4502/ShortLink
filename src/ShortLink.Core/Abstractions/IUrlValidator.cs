namespace ShortLink.Core.Abstractions;

/// <summary>Validasi URL: hanya http/https, cegah open-redirect.</summary>
public interface IUrlValidator
{
    bool IsValid(string url);

    bool TryNormalize(string input, out Uri normalized);
}
