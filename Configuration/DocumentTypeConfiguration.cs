using DocClassifyExtract.Models;

namespace DocClassifyExtract.Configuration;

/// <summary>
/// Document type configuration: maps classifier categories to analyzer IDs and confidence thresholds.
/// </summary>
public static class DocumentTypeConfiguration
{
    // Confidence thresholds per document type
    private static readonly Dictionary<DocumentType, double> _confidenceThresholds = new()
    {
        { DocumentType.Loan, 0.7 },
        { DocumentType.Appraisal, 0.7 },
        { DocumentType.CNI, 0.7 }
    };

    // Analyzer ID mappings for Content Understanding API
    private static readonly Dictionary<DocumentType, string> _analyzerIds = new()
    {
        { DocumentType.Loan, "cre_loan_analyzer" },
        { DocumentType.Appraisal, "appraisal_report_analyzer" },
        { DocumentType.CNI, "cni_agreement_analyzer" }
    };

    // Maps classifier category names → DocumentType
    private static readonly Dictionary<string, DocumentType> _categoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "CRE", DocumentType.Loan },
        { "Valuation", DocumentType.Appraisal },
        { "CNI", DocumentType.CNI }
    };

    public static double GetConfidenceThreshold(DocumentType documentType)
    {
        return _confidenceThresholds.TryGetValue(documentType, out var threshold) ? threshold : 0.5;
    }

    public static string GetAnalyzerId(DocumentType documentType)
    {
        return _analyzerIds.TryGetValue(documentType, out var analyzerId) ? analyzerId : "prebuilt-layout";
    }

    /// <summary>
    /// Maps a classifier category string (e.g. "CRE", "Valuation", "CNI") to a DocumentType enum.
    /// Returns null if the category is not recognized.
    /// </summary>
    public static DocumentType? GetDocumentTypeFromCategory(string category)
    {
        return _categoryMap.TryGetValue(category, out var docType) ? docType : null;
    }

    public static void ValidateConfiguration()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_CONTENT_UNDERSTANDING_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_CONTENT_UNDERSTANDING_API_KEY");

        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentException("AZURE_CONTENT_UNDERSTANDING_ENDPOINT must be provided");

        if (string.IsNullOrEmpty(apiKey) || apiKey == "AZURE_CONTENT_UNDERSTANDING_API_KEY")
            throw new ArgumentException("AZURE_CONTENT_UNDERSTANDING_API_KEY must be provided");
    }
}
