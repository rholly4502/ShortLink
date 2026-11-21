using System.Globalization;
using System.Text;

namespace ShortLink.Core;

/// <summary>Mengekspor ringkasan analytics klik menjadi CSV RFC 4180 yang aman terhadap formula injection.</summary>
public static class AnalyticsCsvExporter
{
    public const string Header = "section,label,clicks";

    public static string Export(AnalyticsSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        sb.Append("summary,total,").Append(summary.TotalClicks.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var day in summary.ByDay)
        {
            sb.Append("day,")
                .Append(day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(day.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        foreach (var device in summary.ByDevice)
        {
            sb.Append("device,")
                .Append(EscapeField(device.Device))
                .Append(',')
                .Append(device.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        foreach (var country in summary.ByCountry)
        {
            sb.Append("country,")
                .Append(EscapeField(country.Country))
                .Append(',')
                .Append(country.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return sb.ToString();
    }

    public static byte[] ExportUtf8(AnalyticsSummary summary)
        => Encoding.UTF8.GetBytes(Export(summary));

    public static string EscapeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var trimmed = value.AsSpan().TrimStart(' ');
        if (value[0] is '\t' or '\r' || (!trimmed.IsEmpty && trimmed[0] is '=' or '+' or '-' or '@'))
            value = "'" + value;

        if (value.IndexOfAny([',', '"', '\n', '\r']) >= 0)
            return $"\"{value.Replace("\"", "\"\"")}\"";

        return value;
    }
}
