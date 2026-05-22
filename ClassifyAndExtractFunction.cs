using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using DocClassifyExtract.Configuration;
using DocClassifyExtract.Models;
using DocClassifyExtract.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace DocClassifyExtract;

public class ClassifyAndExtractFunction
{
    private readonly ILogger<ClassifyAndExtractFunction> _logger;
    private readonly IContentUnderstandingService _contentUnderstandingService;
    private readonly IDocumentFieldExtractor _fieldExtractor;
    private readonly IDatabaseService _databaseService;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IBlobRoutingService _blobRoutingService;
    private readonly ISmeAssignmentService _smeAssignmentService;

    public ClassifyAndExtractFunction(
        ILogger<ClassifyAndExtractFunction> logger,
        IContentUnderstandingService contentUnderstandingService,
        IDocumentFieldExtractor fieldExtractor,
        IDatabaseService databaseService,
        BlobServiceClient blobServiceClient,
        IBlobRoutingService blobRoutingService,
        ISmeAssignmentService smeAssignmentService)
    {
        _logger = logger;
        _contentUnderstandingService = contentUnderstandingService;
        _fieldExtractor = fieldExtractor;
        _databaseService = databaseService;
        _blobServiceClient = blobServiceClient;
        _blobRoutingService = blobRoutingService;
        _smeAssignmentService = smeAssignmentService;
    }

    /// <summary>
    /// Blob trigger: upload any document to genpact/{name}.
    /// The classifier determines the document type automatically and routes to the correct analyzer.
    /// Extracted fields are saved to SQL.
    /// </summary>
    [Function("ClassifyAndExtract")]
    public async Task Run(
        [BlobTrigger("genpact/incoming-documents/{name}", Connection = "AzureWebJobsStorage")] string triggerItem,
        FunctionContext context,
        string name)
    {
        var stopwatch = Stopwatch.StartNew();
        var operationId = Guid.NewGuid().ToString("N")[..8];

        _logger.LogInformation("[{OperationId}] Blob trigger fired for: {Name}", operationId, name);

        string documentId = string.Empty;
        var jobDetails = new JobDetails();

        try
        {
            // Step 1: Derive document ID from filename
            string documentName = Path.GetFileNameWithoutExtension(name);
            int underscoreIndex = documentName.IndexOf('_');
            documentId = underscoreIndex > 0 ? documentName[..underscoreIndex] : documentName;

            jobDetails.DocumentId = documentId;
            jobDetails.JobId = Guid.NewGuid().ToString();

            _logger.LogInformation("[{OperationId}] JobId={JobId}, DocumentId={DocumentId}",
                operationId, jobDetails.JobId, documentId);

            // Step 2: Classify + analyze (classifier auto-routes to correct extraction analyzer)
            var analysisStopwatch = Stopwatch.StartNew();

            var (classification, rawResponse) = await _contentUnderstandingService.ClassifyAndAnalyzeAsync(
                "genpact", $"incoming-documents/{name}");

            analysisStopwatch.Stop();

            _logger.LogInformation("[{OperationId}] Classification complete in {Ms}ms: {SegmentCount} segment(s) found",
                operationId, analysisStopwatch.ElapsedMilliseconds, classification.Segments.Count);

            foreach (var seg in classification.Segments)
            {
                _logger.LogInformation("[{OperationId}]   Pages {Start}-{End}: {Category} ({DocType}) confidence={Confidence:P0}",
                    operationId, seg.StartPageNumber, seg.EndPageNumber, seg.Category, seg.DocumentType, seg.Confidence);
            }

            if (rawResponse is null || classification.Segments.Count == 0)
            {
                _logger.LogWarning("[{OperationId}] No segments found for {Name}", operationId, name);
                jobDetails.Status = "Failed - No Segments";
                await _databaseService.InsertJobDetailsAsync(jobDetails);
                return;
            }

            // Step 3: Generate a SAS URL for PDF citation links
            string pdfBaseUrl = "";
            try
            {
                var blobClient = _blobServiceClient.GetBlobContainerClient("genpact").GetBlobClient($"incoming-documents/{name}");
                var sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = "genpact",
                    BlobName = $"incoming-documents/{name}",
                    Resource = "b",
                    ExpiresOn = DateTimeOffset.UtcNow.AddHours(24)
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read);
                pdfBaseUrl = blobClient.GenerateSasUri(sasBuilder).ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{OperationId}] Could not generate SAS URL for PDF link", operationId);
            }

            // Step 4: Extract fields from each classified segment using per-segment analyzers
            var allExtractedFields = new List<ExtractedFieldResult>();
            var extractionStopwatch = Stopwatch.StartNew();

            foreach (var seg in classification.Segments)
            {
                var analyzerId = DocumentTypeConfiguration.GetAnalyzerId(seg.DocumentType);
                _logger.LogInformation("[{OperationId}] Extracting segment {Category} (pages {Start}-{End}) with analyzer {AnalyzerId}",
                    operationId, seg.Category, seg.StartPageNumber, seg.EndPageNumber, analyzerId);

                var extractionResult = await _contentUnderstandingService.AnalyzeWithExtractorAsync("genpact", $"incoming-documents/{name}", analyzerId);
                if (extractionResult is null)
                {
                    _logger.LogWarning("[{OperationId}] Extraction returned null for {AnalyzerId}", operationId, analyzerId);
                    continue;
                }

                // Load schema-defined field methods (extract vs generate) for this analyzer
                var schemaFieldMethods = _contentUnderstandingService.GetFieldMethodMap(analyzerId);

                // Use the existing field extractor which handles fields, citations, and confidence
                try
                {
                    var (fields, status) = await _fieldExtractor.ExtractFieldsAsync(
                        extractionResult.Value, name, seg.DocumentType, documentId, pdfBaseUrl: pdfBaseUrl,
                        schemaFieldMethods: schemaFieldMethods);

                    _logger.LogInformation("[{OperationId}] Extracted {Count} fields from {AnalyzerId}: {Status}",
                        operationId, fields.Count, analyzerId, status);

                    allExtractedFields.AddRange(fields);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{OperationId}] Error extracting fields from {AnalyzerId}", operationId, analyzerId);
                }
            }

            extractionStopwatch.Stop();

            _logger.LogInformation("[{OperationId}] Field extraction completed in {Ms}ms: {Count} total fields across {Segments} segment(s)",
                operationId, extractionStopwatch.ElapsedMilliseconds, allExtractedFields.Count, classification.Segments.Count);

            // Step 5: Determine overall job status
            if (allExtractedFields.Count == 0)
                jobDetails.Status = "No Fields Extracted";
            else
            {
                var successCount = allExtractedFields.Count(f => f.Value != "H-I-T-L");
                jobDetails.Status = successCount == allExtractedFields.Count ? "Successful"
                    : successCount == 0 ? "Failed"
                    : "Partially Successful";
            }

            // Step 6: Save to database
            var dbStopwatch = Stopwatch.StartNew();
            var dbSuccess = await _databaseService.InsertDocumentProcessingDataAsync(allExtractedFields, jobDetails);
            dbStopwatch.Stop();

            _logger.LogInformation("[{OperationId}] DB save in {Ms}ms: {Result}",
                operationId, dbStopwatch.ElapsedMilliseconds, dbSuccess ? "Success" : "Failed");

            // Step 6.5: Assign to SME if any field requires HITL review
            if (allExtractedFields.Any(f => f.ReviewRequired))
            {
                var docTypeCategory = classification.Segments[0].Category;
                var assignedSmeId = await _smeAssignmentService.AssignDocumentAsync(documentId, docTypeCategory);

                if (assignedSmeId != null)
                    _logger.LogInformation("[{OperationId}] Document assigned to SME {SmeId} for HITL review",
                        operationId, assignedSmeId);
                else
                    _logger.LogWarning("[{OperationId}] No SME available for DocType '{DocType}', document unassigned",
                        operationId, docTypeCategory);
            }

            stopwatch.Stop();
            // Step 7: Route the blob to the classified folder
            var primarySegment = classification.Segments[0];
            try
            {
                var destPath = await _blobRoutingService.RouteClassifiedBlobAsync(
                    "genpact", $"incoming-documents/{name}", primarySegment.DocumentType, allExtractedFields);
                _logger.LogInformation("[{OperationId}] Blob routed to {DestPath}", operationId, destPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{OperationId}] Blob routing failed for {Name}", operationId, name);
            }

            _logger.LogInformation(
                "[{OperationId}] COMPLETE: {Name} | Segments: {SegCount} | Fields: {FieldCount} | Total: {TotalMs}ms",
                operationId, name, classification.Segments.Count, allExtractedFields.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "[{OperationId}] ERROR processing {Name} after {Ms}ms",
                operationId, name, stopwatch.ElapsedMilliseconds);

            try
            {
                jobDetails.DocumentId = documentId;
                jobDetails.Status = $"Failed - {ex.Message}";
                await _databaseService.InsertJobDetailsAsync(jobDetails);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "[{OperationId}] Failed to save error job details", operationId);
            }
        }
    }

    /// <summary>
    /// Process the fields from a single classified segment and add them to the results list.
    /// </summary>
    private async Task ProcessSegmentFieldsAsync(
        JsonElement segElement, int segmentIndex, string operationId,
        string documentId, string pdfBaseUrl, List<ExtractedFieldResult> allExtractedFields)
    {
        string category = "";
        if (segElement.TryGetProperty("category", out var catProp))
            category = catProp.GetString() ?? "";

        if (string.IsNullOrEmpty(category))
            return;

        var docType = DocumentTypeConfiguration.GetDocumentTypeFromCategory(category);
        if (docType is null)
        {
            _logger.LogWarning("[{OperationId}] Unknown category '{Category}' at segment {Index}, skipping",
                operationId, category, segmentIndex);
            return;
        }

        if (!segElement.TryGetProperty("fields", out _))
        {
            _logger.LogWarning("[{OperationId}] Segment {Index} ({Category}) has no fields", operationId, segmentIndex, category);
            return;
        }

        var confidenceThreshold = DocumentTypeConfiguration.GetConfidenceThreshold(docType.Value);

        _logger.LogInformation("[{OperationId}] Extracting fields from segment {Index}: {Category} → {DocType}",
            operationId, segmentIndex, category, docType.Value);

        var (fields, status) = await _fieldExtractor.ExtractFieldsAsync(
            segElement, $"segment_{segmentIndex}_{category}", docType.Value, documentId, confidenceThreshold, pdfBaseUrl);

        if (fields.Count > 0)
        {
            allExtractedFields.AddRange(fields);
            _logger.LogInformation("[{OperationId}] Segment {Index} ({Category}): {Count} fields, status={Status}",
                operationId, segmentIndex, category, fields.Count, status);
        }
    }
}
