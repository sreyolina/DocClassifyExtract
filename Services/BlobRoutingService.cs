using Azure.Storage.Blobs;
using DocClassifyExtract.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace DocClassifyExtract.Services;

public interface IBlobRoutingService
{
    /// <summary>
    /// Copies the source blob to the appropriate classified folder with the
    /// correct document-name convention, then deletes the original.
    /// Returns the destination blob path, or null if routing was skipped.
    /// </summary>
    Task<string?> RouteClassifiedBlobAsync(
        string sourceContainer,
        string sourceBlobName,
        DocumentType documentType,
        IReadOnlyList<ExtractedFieldResult> extractedFields);
}

public class BlobRoutingService : IBlobRoutingService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger<BlobRoutingService> _logger;

    // Destination folder per document type (all inside the "genpact" container)
    private static readonly Dictionary<DocumentType, string> DestinationFolders = new()
    {
        [DocumentType.Loan]      = "cre_loan",
        [DocumentType.Appraisal] = "cre_valuation",
        [DocumentType.CNI]       = "cni"
    };

    public BlobRoutingService(BlobServiceClient blobServiceClient, ILogger<BlobRoutingService> logger)
    {
        _blobServiceClient = blobServiceClient;
        _logger = logger;
    }

    public async Task<string?> RouteClassifiedBlobAsync(
        string sourceContainer,
        string sourceBlobName,
        DocumentType documentType,
        IReadOnlyList<ExtractedFieldResult> extractedFields)
    {
        if (!DestinationFolders.TryGetValue(documentType, out var folder))
        {
            _logger.LogWarning("No routing folder configured for document type {DocType}", documentType);
            return null;
        }

        var extension = Path.GetExtension(sourceBlobName); // preserves .pdf / .docx etc.
        var baseName = BuildDestinationBaseName(documentType, extractedFields);
        var destBlobName = $"{folder}/{baseName}{extension}";

        var container = _blobServiceClient.GetBlobContainerClient(sourceContainer);
        await EnsureFolderExistsAsync(container, folder);

        var sourceBlob = container.GetBlobClient(sourceBlobName);
        var destBlob = container.GetBlobClient(destBlobName);

        _logger.LogInformation("Routing blob {Source} → {Destination}", sourceBlobName, destBlobName);

        // Copy source → destination (server-side copy)
        var copyOperation = await destBlob.StartCopyFromUriAsync(sourceBlob.Uri);
        await copyOperation.WaitForCompletionAsync();

        // Delete the original from the root trigger folder
        await sourceBlob.DeleteIfExistsAsync();

        _logger.LogInformation("Blob routed and original deleted: {Destination}", destBlobName);
        return destBlobName;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private string BuildDestinationBaseName(DocumentType documentType, IReadOnlyList<ExtractedFieldResult> fields)
    {
        return documentType switch
        {
            DocumentType.Loan =>
                $"CRE_{ResolveFieldValue(fields, "Relationship_Name")}_CreditAgreement_{ResolveDateValue(fields, "Original_Loan_Date")}",

            DocumentType.Appraisal =>
                $"CRE_{ResolveFieldValue(fields, "Client_Name")}_ValuationReport_{ResolveDateValue(fields, "Date_Of_Valuation")}",

            DocumentType.CNI =>
                $"C&I_{ResolveFieldValue(fields, "Borrower_Name")}_CreditAgreement_{ResolveDateValue(fields, "Agreement_Date")}",

            _ => "Unknown"
        };
    }

    private string ResolveFieldValue(IReadOnlyList<ExtractedFieldResult> fields, string fieldName)
    {
        var match = fields.FirstOrDefault(f =>
            string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));

        if (match is not null && !string.IsNullOrWhiteSpace(match.Value) && match.Value != "H-I-T-L")
            return Sanitize(match.Value);

        _logger.LogWarning("Label field '{Field}' not found or empty for {DocType}; using 'Unknown'",
            fieldName, "FileNaming");
        return "Unknown";
    }

    private string ResolveDateValue(IReadOnlyList<ExtractedFieldResult> fields, string fieldName)
    {
        var raw = ResolveFieldValue(fields, fieldName);
        if (string.Equals(raw, "Unknown", StringComparison.OrdinalIgnoreCase))
            return "UnknownDate";

        if (TryParseDate(raw, out var parsed))
            return parsed.ToString("MMddyyyy", CultureInfo.InvariantCulture);

        _logger.LogWarning("Date field '{Field}' value '{Value}' could not be parsed; using UnknownDate", fieldName, raw);
        return "UnknownDate";
    }

    private static bool TryParseDate(string input, out DateTime parsed)
    {
        var formats = new[]
        {
            "yyyy-MM-dd",
            "MM/dd/yyyy",
            "M/d/yyyy",
            "MM-dd-yyyy",
            "M-d-yyyy",
            "yyyyMMdd",
            "dd-MMM-yyyy",
            "MMM d, yyyy",
            "MMMM d, yyyy"
        };

        return DateTime.TryParseExact(input, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            || DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }

    private static string Sanitize(string value)
    {
        // Replace characters invalid in blob names / file names with a hyphen
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { '/', '\\', '#', '?', ' ' })
            .ToHashSet();

        return new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('-');
    }

    private async Task EnsureFolderExistsAsync(BlobContainerClient container, string folder)
    {
        // Azure Blob Storage is flat; this zero-byte marker makes the virtual folder explicit.
        var markerBlob = container.GetBlobClient($"{folder}/.folder");
        if (!await markerBlob.ExistsAsync())
        {
            await markerBlob.UploadAsync(BinaryData.FromString(string.Empty));
            _logger.LogInformation("Created blob folder marker: {Folder}", folder);
        }
    }
}
