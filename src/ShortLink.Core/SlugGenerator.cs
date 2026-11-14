using System.Security.Cryptography;
using ShortLink.Core.Abstractions;

namespace ShortLink.Core;

/// <summary>Menghasilkan slug base62 acak dengan RNG kriptografis.</summary>
public class SlugGenerator : ISlugGenerator
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public string Generate(int length = 7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }
}
