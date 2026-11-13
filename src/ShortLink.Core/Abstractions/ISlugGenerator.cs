namespace ShortLink.Core.Abstractions;

/// <summary>Menghasilkan slug kolisi-rendah untuk URL pendek.</summary>
public interface ISlugGenerator
{
    string Generate(int length = 7);
}
