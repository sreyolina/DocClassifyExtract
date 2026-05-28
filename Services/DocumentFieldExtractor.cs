using DocClassifyExtract.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DocClassifyExtract.Services;

public interface IDocumentFieldExtractor
{
    Task<(List<ExtractedFieldResult>, string)> ExtractFieldsAsync(JsonElement contentResponse,
        string documentName, DocumentType documentType, string documentID, double confidenceThreshold = 0.5, string pdfBaseUrl = "",
        Dictionary<string, string>? schemaFieldMethods = null);
}

public class DocumentFieldExtractor : IDocumentFieldExtractor
{
    private readonly ILogger<DocumentFieldExtractor> _logger;
    private readonly IFeatureRefService _featureRefService;
    private readonly ICitationService _citationService;

    public DocumentFieldExtractor(ILogger<DocumentFieldExtractor> logger, IFeatureRefService featureRefService, ICitationService citationService)
    {
        _logger = logger;
        _featureRefService = featureRefService;
        _citationService = citationService;
    }

    public async Task<(List<ExtractedFieldResult>, string)> ExtractFieldsAsync(JsonElement contentResponse,
        string documentName, DocumentType documentType, string documentID, double confidenceThreshold = 0.5, string pdfBaseUrl = "",
        Dictionary<string, string>? schemaFieldMethods = null)
    {
        var extractedFields = new List<ExtractedFieldResult>();
        string status = string.Empty;
        int totalFields = 0;

        try
        {
            if (contentResponse.ValueKind == JsonValueKind.Undefined || contentResponse.ValueKind == JsonValueKind.Null)
            {
                _logger.LogWarning("No content found in Content Understanding response for {DocumentName}", documentName);
                status = "failed";
                return (extractedFields, status);
            }

            JsonElement fieldsElement;
            string documentMarkdown = string.Empty;
            JsonElement pagesElement = default;

            if (contentResponse.TryGetProperty("fields", out fieldsElement))
            {
                if (contentResponse.TryGetProperty("markdown", out var markdownElement))
                    documentMarkdown = markdownElement.GetString() ?? string.Empty;
                if (contentResponse.TryGetProperty("pages", out pagesElement))
                    _logger.LogDebug("Loaded {PageCount} pages for citation mapping", pagesElement.GetArrayLength());
            }
            else
            {
                fieldsElement = contentResponse;
            }

            var documentTypeStr = documentType.ToString();
            var allFeatures = await _featureRefService.GetAllFeaturesSimpleAsync();

            _logger.LogInformation("=== Processing Fields for Document: {DocumentName} (markdown: {MarkdownLength} chars) ===",
                documentName, documentMarkdown.Length);

            foreach (var field in fieldsElement.EnumerateObject())
            {
                bool reviewRequired = false;
                var key = field.Name;
                var value = field.Value;

                totalFields++;

                // Handle simple scalar values (string, number, etc.) that aren't objects
                if (value.ValueKind != JsonValueKind.Object)
                {
                    var featureId = allFeatures
                        .Where(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        .Select(f => f.Value)
                        .FirstOrDefault();
                    if (featureId == 0)
                    {
                        featureId = await _featureRefService.EnsureFeatureExistsAsync(key, documentType.ToString());
                        if (featureId > 0) allFeatures[key] = featureId;
                    }
                    string simpleValue = value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? string.Empty
                        : value.ToString();
                    extractedFields.Add(new ExtractedFieldResult
                    {
                        DocumentId = documentID,
                        FieldName = key,
                        FeatureId = featureId,
                        Value = simpleValue,
                        Confidence = 0.0,
                        FieldMethod = "extract",
                        ConfidenceReason = "Simple value from API response",
                        ReviewRequired = false
                    });
                    continue;
                }

                var featureId2 = allFeatures
                    .Where(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Value)
                    .FirstOrDefault();

                if (featureId2 == 0)
                {
                    featureId2 = await _featureRefService.EnsureFeatureExistsAsync(key, documentType.ToString());
                    if (featureId2 > 0)
                        allFeatures[key] = featureId2;
                }

                double confidence = 0.0;
                string confidenceReason = string.Empty;
                if (value.TryGetProperty("confidence", out var confidenceElement))
                    confidence = confidenceElement.GetDouble();

                string fieldValue = string.Empty;
                string fieldType = "unknown";

                if (value.TryGetProperty("type", out var typeElement))
                    fieldType = typeElement.GetString() ?? "unknown";

                if (value.TryGetProperty("valueDate", out var valueDateElement))
                    fieldValue = valueDateElement.GetString() ?? string.Empty;
                else if (value.TryGetProperty("valueString", out var valueStringElement))
                    fieldValue = valueStringElement.GetString() ?? string.Empty;
                else if (value.TryGetProperty("valueNumber", out var valueNumberElement))
                    fieldValue = valueNumberElement.GetDouble().ToString("F2");
                else if (value.TryGetProperty("content", out var contentElement))
                    fieldValue = contentElement.GetString() ?? string.Empty;
                else if (value.TryGetProperty("valueGeneratedContent", out var genContentElement))
                    fieldValue = genContentElement.GetString() ?? string.Empty;
                else
                {
                    // Log the raw JSON to diagnose what property the API returns
                    _logger.LogWarning("Field {FieldName}: No recognized value property found. Raw JSON: {RawJson}",
                        key, value.GetRawText());
                    fieldValue = "H-I-T-L";
                    confidence = 0.0;
                    reviewRequired = true;
                    confidenceReason = "Value not found in analyzer response - human review required";
                }

                string fieldMethod = DetermineFieldMethod(key, value, schemaFieldMethods);
                FieldCitation? citation = null;

                if (fieldMethod == "extract")
                {
                    confidenceReason = confidence >= confidenceThreshold
                        ? "High confidence extraction"
                        : "Low confidence extraction - review recommended";

                    if (value.TryGetProperty("source", out var sourceElement))
                    {
                        var sourceStr = sourceElement.GetString() ?? string.Empty;
                        citation = _citationService.ParseExtractSource(sourceStr, value, pdfBaseUrl);
                        if (citation != null)
                            _logger.LogDebug("Field {FieldName}: Citation at page {Page}", key, citation.PageNumber);
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(documentMarkdown) && fieldValue != "H-I-T-L")
                    {
                        var (genConfidence, reason, genCitation) = _citationService.ComputeGenerateConfidence(
                            fieldValue, documentMarkdown, pagesElement, pdfBaseUrl);

                        confidence = genConfidence;
                        confidenceReason = reason;
                        citation = genCitation;

                        _logger.LogDebug("Field {FieldName} (generate): Confidence={Confidence:F2}, Reason={Reason}",
                            key, genConfidence, reason);
                    }
                    else
                    {
                        confidenceReason = "Generate field - no document text available for verification";
                    }
                }

                if (confidence < confidenceThreshold && confidence > 0)
                    reviewRequired = true;

                var extractedField = new ExtractedFieldResult
                {
                    DocumentId = documentID,
                    FieldName = key,
                    FeatureId = featureId2,
                    Value = fieldValue,
                    Confidence = confidence,
                    ReviewRequired = reviewRequired,
                    FieldMethod = fieldMethod,
                    ConfidenceReason = confidenceReason,
                    Citation = citation
                };

                extractedFields.Add(extractedField);

                _logger.LogDebug("Stored field: {FieldName} -> FeatureId: {FeatureId}, Value: {Value}, Confidence: {Confidence:F3}, Method: {Method}",
                    key, featureId2, fieldValue, confidence, fieldMethod);

                if (featureId2 == 0)
                {
                    _logger.LogWarning("No FeatureId found for field: {FieldName} in document type: {DocumentType} (field still processed)",
                        key, documentType);
                }
            }

            _logger.LogInformation("=== End Field Processing ===");

            var lowConfidenceCount = extractedFields.Count(f => f.Confidence < confidenceThreshold && f.Confidence > 0);
            var missingValueCount = extractedFields.Count(f => f.Value == "H-I-T-L");
            var extractCount = extractedFields.Count(f => f.FieldMethod == "extract");
            var generateCount = extractedFields.Count(f => f.FieldMethod == "generate");

            var successfulExtractions = extractedFields.Count(f => f.Value != "H-I-T-L");
            var highConfidenceExtractions = extractedFields.Count(f => f.Confidence >= confidenceThreshold);

            if (totalFields == 0)
                status = "No Fields Found";
            else if (successfulExtractions == totalFields && highConfidenceExtractions >= successfulExtractions * 0.8)
                status = "Successful";
            else if (successfulExtractions == 0)
                status = "Failed";
            else
                status = "Partially Successful";

            _logger.LogInformation("Extracted {FieldCount} fields from {DocumentName}. " +
                    "Extract: {ExtractCount}, Generate: {GenerateCount}, " +
                    "Low confidence: {LowConfidenceCount}, Missing values: {MissingValueCount}, Status: {Status}",
                    extractedFields.Count, documentName, extractCount, generateCount,
                    lowConfidenceCount, missingValueCount, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting fields from document {DocumentName}", documentName);
            status = "Failed";
        }

        return (extractedFields, status);
    }

    private static string DetermineFieldMethod(string fieldName, JsonElement fieldElement, Dictionary<string, string>? schemaFieldMethods)
    {
        // Use the analyzer schema definition if available
        if (schemaFieldMethods != null && schemaFieldMethods.TryGetValue(fieldName, out var schemaMethod))
            return schemaMethod;

        // Fallback: infer from response properties
        if (fieldElement.TryGetProperty("source", out var sourceEl) &&
            !string.IsNullOrEmpty(sourceEl.GetString()))
            return "extract";

        if (fieldElement.TryGetProperty("confidence", out var confEl) && confEl.GetDouble() > 0)
            return "extract";

        return "generate";
    }
}
