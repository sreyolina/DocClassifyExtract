using DocClassifyExtract.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocClassifyExtract.Services;

public interface ICitationService
{
    FieldCitation? ParseExtractSource(string source, JsonElement fieldElement, string pdfBaseUrl = "");

    (double confidence, string reason, FieldCitation? citation) ComputeGenerateConfidence(
        string generatedValue, string documentMarkdown, JsonElement pagesElement, string pdfBaseUrl = "");
}

public class CitationService : ICitationService
{
    private readonly ILogger<CitationService> _logger;

    public CitationService(ILogger<CitationService> logger)
    {
        _logger = logger;
    }

    public FieldCitation? ParseExtractSource(string source, JsonElement fieldElement, string pdfBaseUrl = "")
    {
        if (string.IsNullOrEmpty(source))
            return null;

        try
        {
            var firstSource = source.Split(';')[0].Trim();
            var match = Regex.Match(firstSource, @"D\((\d+),([\d.]+),([\d.]+),([\d.]+),([\d.]+),([\d.]+),([\d.]+),([\d.]+),([\d.]+)\)");

            if (!match.Success)
                return null;

            var pageNumber = int.Parse(match.Groups[1].Value);
            var x1 = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var y1 = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var x2 = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            var y3 = double.Parse(match.Groups[7].Value, CultureInfo.InvariantCulture);

            var width = Math.Abs(x2 - x1);
            var height = Math.Abs(y3 - y1);

            int spanOffset = 0, spanLength = 0;
            if (fieldElement.TryGetProperty("spans", out var spansElement) && spansElement.GetArrayLength() > 0)
            {
                var firstSpan = spansElement[0];
                spanOffset = firstSpan.GetProperty("offset").GetInt32();
                spanLength = firstSpan.GetProperty("length").GetInt32();
            }

            string highlightText = "";
            if (fieldElement.TryGetProperty("content", out var vc))
                highlightText = vc.GetString() ?? "";
            else if (fieldElement.TryGetProperty("valueString", out var vs))
                highlightText = vs.GetString() ?? "";
            else if (fieldElement.TryGetProperty("valueDate", out var vd))
                highlightText = vd.GetString() ?? "";
            else if (fieldElement.TryGetProperty("valueNumber", out var vn))
                highlightText = vn.GetDouble().ToString("F2");
            else if (fieldElement.TryGetProperty("valueCurrency", out var vcur)
                     && vcur.TryGetProperty("amount", out var amt))
                highlightText = amt.GetDouble().ToString("C0");

            return new FieldCitation
            {
                PageNumber = pageNumber,
                BoundingBox = new BoundingBoxInfo
                {
                    X = Math.Round(x1, 4),
                    Y = Math.Round(y1, 4),
                    Width = Math.Round(width, 4),
                    Height = Math.Round(height, 4)
                },
                PdfLink = string.IsNullOrEmpty(pdfBaseUrl) ? $"#page={pageNumber}" : $"{pdfBaseUrl}#page={pageNumber}",
                SpanOffset = spanOffset,
                SpanLength = spanLength,
                HighlightText = highlightText
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse source string: {Source}", source);
            return null;
        }
    }

    public (double confidence, string reason, FieldCitation? citation) ComputeGenerateConfidence(
        string generatedValue, string documentMarkdown, JsonElement pagesElement, string pdfBaseUrl = "")
    {
        if (string.IsNullOrEmpty(generatedValue) || string.IsNullOrEmpty(documentMarkdown))
            return (0.3, "No document text available for verification", null);

        var normalizedValue = NormalizeForSearch(generatedValue);
        var normalizedMarkdown = NormalizeForSearch(documentMarkdown);

        var exactIndex = normalizedMarkdown.IndexOf(normalizedValue, StringComparison.OrdinalIgnoreCase);
        if (exactIndex >= 0)
        {
            var citation = BuildCitationFromOffset(exactIndex, normalizedValue.Length, documentMarkdown, pagesElement, generatedValue, pdfBaseUrl);
            return (0.9, "Value found verbatim in document text", citation);
        }

        var numbers = ExtractNumbers(generatedValue);
        foreach (var number in numbers)
        {
            var formattedIndex = documentMarkdown.IndexOf(number, StringComparison.OrdinalIgnoreCase);
            if (formattedIndex >= 0)
            {
                var contextLength = Math.Min(80, documentMarkdown.Length - formattedIndex);
                var citation = BuildCitationFromOffset(formattedIndex, contextLength, documentMarkdown, pagesElement, pdfBaseUrl: pdfBaseUrl);
                return (0.6, $"Key value '{number}' found in document text (partial match)", citation);
            }

            var strippedNumber = Regex.Replace(number, @"[,$\s]", "");
            if (strippedNumber.Length >= 4)
            {
                var strippedMarkdown = Regex.Replace(documentMarkdown, @"[,$\s]", "");
                var strippedIndex = strippedMarkdown.IndexOf(strippedNumber, StringComparison.OrdinalIgnoreCase);
                if (strippedIndex >= 0)
                {
                    var approxOffset = FindApproximateOriginalOffset(documentMarkdown, strippedNumber);
                    var citation = BuildCitationFromOffset(approxOffset, number.Length + 20, documentMarkdown, pagesElement, pdfBaseUrl: pdfBaseUrl);
                    return (0.6, $"Numeric value '{strippedNumber}' found in document (partial match)", citation);
                }
            }
        }

        var keywords = ExtractKeywords(generatedValue);
        foreach (var keyword in keywords)
        {
            var keywordIndex = documentMarkdown.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (keywordIndex >= 0)
            {
                var contextLength = Math.Min(100, documentMarkdown.Length - keywordIndex);
                var citation = BuildCitationFromOffset(keywordIndex, contextLength, documentMarkdown, pagesElement, pdfBaseUrl: pdfBaseUrl);
                return (0.6, $"Keyword '{keyword}' found in document text (partial match)", citation);
            }
        }

        return (0.3, "Generated value not found in document text - low verification confidence", null);
    }

    private FieldCitation? BuildCitationFromOffset(int offset, int length, string documentMarkdown, JsonElement pagesElement, string? highlightText = null, string pdfBaseUrl = "")
    {
        if (offset < 0) return null;

        try
        {
            int pageNumber = MapOffsetToPage(offset, pagesElement);

            var safeLength = Math.Min(length, documentMarkdown.Length - offset);
            var matchedText = documentMarkdown.Substring(offset, safeLength).Replace("\n", " ").Trim();

            var contextStart = Math.Max(0, offset - 30);
            var contextEnd = Math.Min(documentMarkdown.Length, offset + length + 30);
            var contextText = documentMarkdown[contextStart..contextEnd].Replace("\n", " ").Trim();
            if (contextStart > 0) contextText = "..." + contextText;
            if (contextEnd < documentMarkdown.Length) contextText += "...";

            return new FieldCitation
            {
                PageNumber = pageNumber,
                PdfLink = string.IsNullOrEmpty(pdfBaseUrl) ? $"#page={pageNumber}" : $"{pdfBaseUrl}#page={pageNumber}",
                SpanOffset = offset,
                SpanLength = safeLength,
                TextMatch = contextText,
                HighlightText = highlightText ?? matchedText
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build citation from offset {Offset}", offset);
            return null;
        }
    }

    private int MapOffsetToPage(int offset, JsonElement pagesElement)
    {
        if (pagesElement.ValueKind != JsonValueKind.Array)
            return 1;

        foreach (var page in pagesElement.EnumerateArray())
        {
            if (!page.TryGetProperty("spans", out var spans))
                continue;

            foreach (var span in spans.EnumerateArray())
            {
                var spanOffset = span.GetProperty("offset").GetInt32();
                var spanLength = span.GetProperty("length").GetInt32();

                if (offset >= spanOffset && offset < spanOffset + spanLength)
                    return page.GetProperty("pageNumber").GetInt32();
            }
        }

        return 1;
    }

    private static string NormalizeForSearch(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");

    private static List<string> ExtractNumbers(string text)
    {
        var numbers = new List<string>();

        var dollarMatches = Regex.Matches(text, @"\$[\d,]+(?:\.\d+)?");
        foreach (Match m in dollarMatches)
            numbers.Add(m.Value);

        var numMatches = Regex.Matches(text, @"\b[\d,]{4,}(?:\.\d+)?\b");
        foreach (Match m in numMatches)
            if (!numbers.Contains("$" + m.Value))
                numbers.Add(m.Value);

        var pctMatches = Regex.Matches(text, @"\d+\.?\d*%");
        foreach (Match m in pctMatches)
            numbers.Add(m.Value);

        return numbers;
    }

    private static List<string> ExtractKeywords(string text)
    {
        var keywords = new List<string>();

        var phrases = text.Split(new[] { '.', ',', ';', ':', '\u2014', '-' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var phrase in phrases)
        {
            var trimmed = phrase.Trim();
            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3 && trimmed.Length >= 15)
                keywords.Add(trimmed);
        }

        return keywords.Take(5).ToList();
    }

    private static int FindApproximateOriginalOffset(string originalText, string strippedNumber)
    {
        var patterns = new[]
        {
            strippedNumber,
            FormatAsThousands(strippedNumber),
        };

        foreach (var pattern in patterns)
        {
            var idx = originalText.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return idx;
        }

        return 0;
    }

    private static string FormatAsThousands(string number)
    {
        if (long.TryParse(number, out var value))
            return value.ToString("N0");
        return number;
    }
}
