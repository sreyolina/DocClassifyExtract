using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using DocClassifyExtract.Configuration;
using DocClassifyExtract.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DocClassifyExtract.Services;

public interface IContentUnderstandingService
{
    /// <summary>
    /// Classifies a document using the classifier,
    /// then returns parsed segments with their category and DocumentType.
    /// </summary>
    Task<(ClassificationResult classification, JsonElement? rawResponse)> ClassifyAndAnalyzeAsync(
        string containerName, string blobPath);

    /// <summary>
    /// Calls a specific extraction analyzer on a blob and returns the raw JSON result.
    /// Used for two-step classify→extract workflow.
    /// </summary>
    Task<JsonElement?> AnalyzeWithExtractorAsync(string containerName, string blobPath, string analyzerId);

    /// <summary>
    /// Returns a map of fieldName -> method ("extract" or "generate") from the analyzer schema.
    /// </summary>
    Dictionary<string, string> GetFieldMethodMap(string analyzerId);
}

/// <summary>
/// Manages the enhanced Content Understanding classifier that classifies documents
/// AND routes each segment to the appropriate extraction analyzer automatically.
/// </summary>
public class ContentUnderstandingService : IContentUnderstandingService
{
    private const string ApiVersion = "2025-05-01-preview";
    private const string ClassifierApiVersion = "2025-11-01";
    private const string OpenAiApiVersion = "2024-12-01-preview";
    private const string ClassifierId = "doc_classifier_cre_cni_valuation_confidence_score_other";
    private const string GptDeployment = "gpt-4.1";
    private const int DefaultTimeoutSeconds = 600;
    private const int PollingIntervalSeconds = 2;
    private const int ConfidenceThresholdPercent = 70;
    private const double PageSampleRatio = 0.20;

    private const string ValuationDescription =
        "Appraisal or valuation report focused on estimating property value. " +
        "Strong indicators: effective age, remaining economic life, USPAP compliance, " +
        "comparable sales analysis, scope of work, neighborhood analysis, topography analysis, " +
        "site coverage ratio, floor area ratio, yield capitalization, band of investment, paired sales analysis.";

    private const string CreDescription =
        "Commercial real estate loan agreement or legal credit package secured by real property. " +
        "Strong indicators: Assignment of Leases and Rents, tenant estoppel certificates, " +
        "non-recourse carveouts, DSCR triggers, property cash flow waterfall, " +
        "mortgage deed, deed of trust, environmental indemnity agreement.";

    private const string CniDescription =
        "Commercial and industrial loan agreements, corporate credit agreements, borrowing base documents. " +
        "Indicators: revolving and term loan mechanics, corporate financial covenants, " +
        "collateral structures not centered on real estate.";

    private const string OtherDescription =
        "All other documents that do not clearly fall into CRE, CNI, or Valuation. " +
        "Use this category for unrelated files, mixed-content files, or documents with insufficient evidence for the known categories.";

    private const string UnknownCategoryHumanInterventionMessage = "human intervention required category is unknown";

    private static readonly string GptSystemPrompt =
        "You are a document classification validator. Given a document excerpt and the category assigned by a classifier, score your confidence that the classification is correct.\n\n" +
        "CATEGORIES:\n" +
        $"- Valuation: {ValuationDescription}\n" +
        $"- CRE: {CreDescription}\n" +
        $"- CNI: {CniDescription}\n\n" +
        "SCORING RULES (return confidence as a PERCENTAGE integer between 0 and 99, NEVER 100):\n" +
        "- 91-99: Document overwhelmingly matches assigned category, many strong indicators present, no competing signals.\n" +
        "- 70-89: Document clearly matches with several strong indicators but not all are present.\n" +
        "- 50-69: Ambiguous — some indicators match but competing signals exist.\n" +
        "- 30-49: Weak match — another category may be more appropriate.\n" +
        "- 0-29: Likely misclassified.\n\n" +
        "Be CRITICAL and DISCRIMINATING. Deduct points for each expected indicator NOT found in the excerpt.\n" +
        "Count how many strong indicators from the assigned category actually appear. Also check if indicators from OTHER categories appear.\n\n" +
        "Respond ONLY with this JSON (no markdown fences, no extra text):\n" +
        "{\"confidence_percent\": 95, \"reasoning\": \"2-3 sentence explanation\", \"matched_indicators\": [\"indicator1\"], \"missing_indicators\": [\"indicator not found\"], \"competing_category\": \"category name or null\", \"competing_confidence_percent\": 10}";

    private readonly HttpClient _httpClient;
    private readonly ILogger<ContentUnderstandingService> _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _endpoint;
    private readonly string _apiKey;

    public ContentUnderstandingService(
        HttpClient httpClient,
        ILogger<ContentUnderstandingService> logger,
        IConfiguration configuration,
        BlobServiceClient blobServiceClient)
    {
        _httpClient = httpClient;
        _logger = logger;
        _blobServiceClient = blobServiceClient;

        _endpoint = (configuration["AZURE_CONTENT_UNDERSTANDING_ENDPOINT"]
            ?? throw new InvalidOperationException("AZURE_CONTENT_UNDERSTANDING_ENDPOINT is not set"))
            .TrimEnd('/');

        _apiKey = configuration["AZURE_CONTENT_UNDERSTANDING_API_KEY"]
            ?? throw new InvalidOperationException("AZURE_CONTENT_UNDERSTANDING_API_KEY is not set");

        _httpClient.Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ────────────────────────────────────────────────────────────────────────

    public async Task<(ClassificationResult classification, JsonElement? rawResponse)> ClassifyAndAnalyzeAsync(
        string containerName, string blobPath)
    {
        _logger.LogInformation("ClassifyAndAnalyze starting for {BlobPath}", blobPath);

        // Step 1: Ensure the enhanced classifier exists (with analyzer routing)
        await EnsureClassifierExistsAsync();

        // Step 2: Generate SAS URL for the blob
        var sasUrl = GenerateBlobSasUrl(containerName, blobPath);

        // Step 3: Submit to :analyze
        var analyzeUrl = $"{_endpoint}/contentunderstanding/analyzers/{ClassifierId}:analyze?api-version={ClassifierApiVersion}";
        var body = new { inputs = new[] { new { url = sasUrl } } };

        var response = await PostJsonAsync(analyzeUrl, body);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Analyze request failed ({(int)response.StatusCode}): {err}");
        }

        var operationLocation = GetHeader(response, "Operation-Location")
            ?? throw new InvalidOperationException("No Operation-Location header in analyze response");

        // Step 4: Poll until succeeded
        var rawJson = await PollForCompletionAsync(operationLocation);
        var topLevelContent = rawJson["result"]?["contents"]?[0];

        _logger.LogInformation("Classification + extraction completed for {BlobPath}", blobPath);

        // Step 5: Parse classification segments
        var segments = new List<ClassifiedSegment>();
        var contentsArray = rawJson["result"]?["contents"];

        if (contentsArray is JsonArray contents)
        {
            // The first element (index 0) is the top-level document content.
            // Segments (classified + routed) start from index 1 onward in the contents array.
            // Each segment has: category, startPageNumber, endPageNumber, confidence, and fields from the routed analyzer.
            foreach (var content in contents)
            {
                if (content is null) continue;

                var category = content["category"]?.GetValue<string>();
                if (string.IsNullOrEmpty(category)) continue; // skip the top-level content (no category)

                var docType = DocumentTypeConfiguration.GetDocumentTypeFromCategory(category);
                if (docType is null)
                {
                    _logger.LogWarning("Unknown category '{Category}' in classifier response, skipping", category);
                    continue;
                }

                var scoring = IsOtherCategory(category)
                    ? CreateUnknownCategoryScoringResult()
                    : await ScoreClassificationConfidenceAsync(blobPath, category, content, topLevelContent);
                var requiresHumanIntervention = scoring.ConfidencePercent < ConfidenceThresholdPercent;

                segments.Add(new ClassifiedSegment
                {
                    Category = category,
                    StartPageNumber = content["startPageNumber"]?.GetValue<int>() ?? 0,
                    EndPageNumber = content["endPageNumber"]?.GetValue<int>() ?? 0,
                    Confidence = content["confidence"]?.GetValue<double>() ?? 0d,
                    DocumentType = docType.Value,
                    ConfidencePercent = scoring.ConfidencePercent,
                    Reasoning = scoring.Reasoning,
                    MatchedIndicators = scoring.MatchedIndicators,
                    MissingIndicators = scoring.MissingIndicators,
                    CompetingCategory = scoring.CompetingCategory,
                    CompetingConfidencePercent = scoring.CompetingConfidencePercent,
                    RequiresHumanIntervention = requiresHumanIntervention
                });

                LogSegmentConfidence(category, blobPath, scoring, requiresHumanIntervention);
            }
        }

        // Also try the "segments" array inside contents[0] (alternative response format)
        if (segments.Count == 0)
        {
            var segmentsNode = rawJson["result"]?["contents"]?[0]?["segments"]?.AsArray();
            if (segmentsNode is not null)
            {
                foreach (var s in segmentsNode)
                {
                    if (s is null) continue;
                    var category = s["category"]?.GetValue<string>() ?? string.Empty;
                    var docType = DocumentTypeConfiguration.GetDocumentTypeFromCategory(category);
                    if (docType is null) continue;

                    var scoring = IsOtherCategory(category)
                        ? CreateUnknownCategoryScoringResult()
                        : await ScoreClassificationConfidenceAsync(blobPath, category, s, topLevelContent);
                    var requiresHumanIntervention = scoring.ConfidencePercent < ConfidenceThresholdPercent;

                    segments.Add(new ClassifiedSegment
                    {
                        Category = category,
                        StartPageNumber = s["startPageNumber"]?.GetValue<int>() ?? 0,
                        EndPageNumber = s["endPageNumber"]?.GetValue<int>() ?? 0,
                        Confidence = s["confidence"]?.GetValue<double>() ?? 0d,
                        DocumentType = docType.Value,
                        ConfidencePercent = scoring.ConfidencePercent,
                        Reasoning = scoring.Reasoning,
                        MatchedIndicators = scoring.MatchedIndicators,
                        MissingIndicators = scoring.MissingIndicators,
                        CompetingCategory = scoring.CompetingCategory,
                        CompetingConfidencePercent = scoring.CompetingConfidencePercent,
                        RequiresHumanIntervention = requiresHumanIntervention
                    });

                    LogSegmentConfidence(category, blobPath, scoring, requiresHumanIntervention);
                }
            }
        }

        var classification = new ClassificationResult
        {
            BlobName = blobPath,
            Segments = segments
        };

        // Convert JsonNode to JsonElement for downstream consumption
        var jsonString = rawJson.ToJsonString();
        var jsonDoc = JsonDocument.Parse(jsonString);
        JsonElement rawElement = jsonDoc.RootElement;

        return (classification, rawElement);
    }

    private async Task<GptScoringResult> ScoreClassificationConfidenceAsync(
        string blobPath,
        string category,
        JsonNode segmentContent,
        JsonNode? topLevelContent)
    {
        var excerpt = BuildClassificationExcerpt(segmentContent, topLevelContent);
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            _logger.LogWarning("No markdown excerpt available for confidence scoring of {BlobPath} category {Category}", blobPath, category);
            return new GptScoringResult
            {
                Reasoning = "No markdown excerpt available for confidence scoring."
            };
        }

        return await ScoreWithGptAsync(blobPath, category, excerpt);
    }

    private static bool IsOtherCategory(string category)
    {
        return string.Equals(category, "Other", StringComparison.OrdinalIgnoreCase);
    }

    private static GptScoringResult CreateUnknownCategoryScoringResult()
    {
        return new GptScoringResult
        {
            Reasoning = UnknownCategoryHumanInterventionMessage
        };
    }

    private static string BuildClassificationExcerpt(JsonNode segmentContent, JsonNode? topLevelContent)
    {
        var segmentMarkdown = segmentContent["markdown"]?.GetValue<string>() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(segmentMarkdown))
        {
            return ExtractPageSampledExcerpt(segmentContent, segmentMarkdown);
        }

        var topLevelMarkdown = topLevelContent?["markdown"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(topLevelMarkdown))
        {
            return string.Empty;
        }

        var startPageNumber = segmentContent["startPageNumber"]?.GetValue<int>();
        var endPageNumber = segmentContent["endPageNumber"]?.GetValue<int>();

        return ExtractPageSampledExcerpt(topLevelContent, topLevelMarkdown, startPageNumber, endPageNumber);
    }

    private static string ExtractPageSampledExcerpt(
        JsonNode? content,
        string markdown,
        int? startPageNumber = null,
        int? endPageNumber = null)
    {
        const int FallbackChars = 4000;

        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var pages = content?["pages"]?.AsArray();
        if (pages is null || pages.Count == 0)
        {
            return markdown.Length > FallbackChars ? markdown[..FallbackChars] : markdown;
        }

        var eligiblePages = pages
            .Where(page => page is not null)
            .Where(page =>
            {
                var pageNumber = page!["pageNumber"]?.GetValue<int>() ?? 0;
                return (!startPageNumber.HasValue || pageNumber >= startPageNumber.Value)
                    && (!endPageNumber.HasValue || pageNumber <= endPageNumber.Value);
            })
            .ToList();

        if (eligiblePages.Count == 0)
        {
            return markdown.Length > FallbackChars ? markdown[..FallbackChars] : markdown;
        }

        var pageLimit = (int)Math.Max(1, Math.Ceiling(eligiblePages.Count * PageSampleRatio));
        var excerptBuilder = new StringBuilder();

        foreach (var page in eligiblePages.Take(pageLimit))
        {
            var spans = page!["spans"]?.AsArray();
            if (spans is null)
            {
                continue;
            }

            foreach (var span in spans)
            {
                var offset = span?["offset"]?.GetValue<int>() ?? -1;
                var length = span?["length"]?.GetValue<int>() ?? 0;

                if (offset >= 0 && length > 0 && offset + length <= markdown.Length)
                {
                    excerptBuilder.Append(markdown, offset, length);
                }
            }
        }

        var excerpt = excerptBuilder.ToString().Trim();
        return excerpt.Length > 0
            ? excerpt
            : (markdown.Length > FallbackChars ? markdown[..FallbackChars] : markdown);
    }

    private async Task<GptScoringResult> ScoreWithGptAsync(string blobPath, string category, string excerpt)
    {
        _logger.LogInformation(
            "Calling GPT confidence scoring for {BlobPath} category {Category} with excerpt length {Length}",
            blobPath,
            category,
            excerpt.Length);

        var userMessage = $"ASSIGNED CATEGORY: {category}\n\nDOCUMENT EXCERPT:\n{excerpt}";
        var requestBody = new
        {
            messages = new object[]
            {
                new { role = "system", content = GptSystemPrompt },
                new { role = "user", content = userMessage }
            },
            temperature = 0.1,
            max_tokens = 500
        };

        var chatUrl = $"{_endpoint}/openai/deployments/{GptDeployment}/chat/completions?api-version={OpenAiApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, chatUrl);
        request.Headers.Add("api-key", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "GPT confidence scoring failed ({StatusCode}) for {BlobPath} category {Category}: {Error}",
                (int)response.StatusCode,
                blobPath,
                category,
                error);
            return new GptScoringResult();
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        var content = JsonNode.Parse(responseBody)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;

        try
        {
            var parsed = JsonNode.Parse(content);
            return new GptScoringResult
            {
                ConfidencePercent = parsed?["confidence_percent"]?.GetValue<int>() ?? 0,
                Reasoning = parsed?["reasoning"]?.GetValue<string>() ?? string.Empty,
                MatchedIndicators = parsed?["matched_indicators"]?.AsArray()
                    .Select(node => node?.GetValue<string>() ?? string.Empty)
                    .ToList() ?? [],
                MissingIndicators = parsed?["missing_indicators"]?.AsArray()
                    .Select(node => node?.GetValue<string>() ?? string.Empty)
                    .ToList() ?? [],
                CompetingCategory = parsed?["competing_category"]?.GetValue<string>(),
                CompetingConfidencePercent = parsed?["competing_confidence_percent"]?.GetValue<int>() ?? 0
            };
        }
        catch (JsonException)
        {
            _logger.LogWarning("GPT confidence scoring returned non-JSON for {BlobPath}: {Content}", blobPath, content);
            return new GptScoringResult();
        }
    }

    private void LogSegmentConfidence(string category, string blobPath, GptScoringResult scoring, bool requiresHumanIntervention)
    {
        if (IsOtherCategory(category))
        {
            _logger.LogWarning(
                "{Message} for {BlobPath} category {Category}",
                UnknownCategoryHumanInterventionMessage,
                blobPath,
                category);
            return;
        }

        if (requiresHumanIntervention)
        {
            _logger.LogWarning(
                "Confidence score less than 70% require human intervention for {BlobPath} category {Category}. Score={Score}. Reason={Reason}",
                blobPath,
                category,
                scoring.ConfidencePercent,
                scoring.Reasoning);
            return;
        }

        _logger.LogInformation(
            "Confidence score accepted for {BlobPath} category {Category}. Score={Score}",
            blobPath,
            category,
            scoring.ConfidencePercent);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Classifier provisioning (with analyzer routing)
    // ────────────────────────────────────────────────────────────────────────

    private async Task EnsureClassifierExistsAsync()
    {
        if (await AnalyzerIsReadyAsync(ClassifierId))
            return;

        _logger.LogInformation("Provisioning enhanced classifier: {Id}", ClassifierId);

        // Ensure the default completion model deployment is set before creating the classifier
        await EnsureDefaultsSetAsync();

        // Ensure extraction analyzers exist before creating the classifier (it references them)
        await EnsureExtractionAnalyzersExistAsync();

        static JsonObject MakeCategory(string description, string? analyzerId = null)
        {
            var category = new JsonObject { ["description"] = description };
            if (!string.IsNullOrWhiteSpace(analyzerId))
            {
                category["analyzerId"] = analyzerId;
            }

            return category;
        }

        var classifierDef = new JsonObject
        {
            ["baseAnalyzerId"] = "prebuilt-document",
            ["description"] = "Classifier for CRE, C&I, Valuation, and other documents",
            ["config"] = new JsonObject
            {
                ["returnDetails"] = true,
                ["enableSegment"] = false,
                ["contentCategories"] = new JsonObject
                {
                    ["Valuation"] = MakeCategory(
                        "Classify as Valuation when the document is an appraisal or valuation report focused on estimating property value and collateral value using valuation methodology and appraisal standards. " +
                        "Strong indicators include effective age, remaining economic life, indirect costs, entrepreneurial profit, reversionary value, stabilized net operating income, yield capitalization, band of investment, paired sales analysis, comparable land sales analysis, residual land value, feasibility rent, going concern value, business enterprise value, lease-up analysis, absorption rate, vacancy and collection loss, retrospective valuation, prospective valuation, scope of work, RICS Red Book valuation, USPAP compliance, inspection date, neighborhood analysis, site visit summary, topography analysis, site coverage ratio, and floor area ratio. " +
                        "If the document primarily reads like an appraisal narrative and valuation report, classify as Valuation.",
                        "appraisal_report_analyzer"),

                    ["CRE"] = MakeCategory(
                        "Classify as CRE when the document is a commercial real estate loan agreement or related legal credit package secured by real property, with covenant, collateral control, tenant controls, and cash-flow control language. " +
                        "Strong indicators include Assignment of Leases and Rents, tenant estoppel certificates, property operating income, operating expense reserve or lease-up reserve, property condition report requirement, environmental indemnity agreement, non-recourse carveouts or bad boy guaranty, completion guaranty, carry guaranty, separateness covenants, major tenant clause, anchor tenant, tenant rollover schedule, occupancy covenant, minimum occupancy requirement, permitted lease amendments, leasing guidelines, tenant improvement allowance, leasing commissions, property cash flow waterfall, net cash flow sweep, excess cash flow lock-up, DSCR triggers, and stabilization conditions. " +
                        "Legal-title indicators include mortgage deed or deed of trust, mechanics lien holdback, zoning compliance covenant, ALTA or NSPS survey references, encroachment easement right-of-way language, and title endorsements. If the document is primarily a legal real estate lending agreement, classify as CRE.",
                        "cre_loan_analyzer"),

                    ["CNI"] = MakeCategory(
                        "Classify as CNI for commercial and industrial loan agreements, corporate credit agreements, borrowing base documents, and general business lending packages that are not primarily CRE valuation reports and not primarily CRE real estate loan agreements. " +
                        "Typical indicators include borrower and guarantor corporate credit terms, revolving and term loan mechanics, corporate financial covenants, and collateral structures not centered on specific real estate operations. " +
                        "Decision rule: if the document does not clearly match Valuation or CRE, classify as CNI.",
                        "cni_agreement_analyzer"),

                    ["Other"] = MakeCategory(
                        "Classify as Other when the document does not fall into CRE, CNI, or Valuation. " +
                        "Use this for unrelated documents, unsupported document types, or documents with insufficient evidence for the known categories.")
                }
            },
            ["models"] = new JsonObject { ["completion"] = "gpt-4.1" }
        };

        var url = $"{_endpoint}/contentunderstanding/analyzers/{ClassifierId}?api-version={ClassifierApiVersion}";
        var response = await PutJsonAsync(url, classifierDef);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();

            if ((int)response.StatusCode == 409 &&
                err.Contains("ModelExists", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Classifier already exists: {Id}", ClassifierId);
                return;
            }

            throw new InvalidOperationException($"Failed to create classifier ({(int)response.StatusCode}): {err}");
        }

        var operationLocation = GetHeader(response, "Operation-Location");
        if (operationLocation is not null)
            await PollForCompletionAsync(operationLocation);

        _logger.LogInformation("Enhanced classifier provisioned: {Id}", ClassifierId);
    }


    

    // ────────────────────────────────────────────────────────────────────────
    // Field method map from analyzer schemas
    // ────────────────────────────────────────────────────────────────────────

    public Dictionary<string, string> GetFieldMethodMap(string analyzerId)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var schemaFile = ExtractionAnalyzerSchemas
            .FirstOrDefault(s => s.Contains(analyzerId, StringComparison.OrdinalIgnoreCase));
        if (schemaFile is null) return map;

        var schemaPath = Path.Combine(AppContext.BaseDirectory, schemaFile);
        if (!File.Exists(schemaPath)) return map;

        var schemaJson = JsonNode.Parse(File.ReadAllText(schemaPath));
        var fields = schemaJson?["fieldSchema"]?["fields"]?.AsObject();
        if (fields is null) return map;

        foreach (var kvp in fields)
        {
            var method = kvp.Value?["method"]?.GetValue<string>() ?? "extract";
            map[kvp.Key] = method;
        }
        return map;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Extraction analyzer provisioning (from schema JSON files)
    // ────────────────────────────────────────────────────────────────────────

    private static readonly string[] ExtractionAnalyzerSchemas =
    [
        "analyzer-schemas/appraisal_report_analyzer.json",
        "analyzer-schemas/cni_agreement_analyzer.json",
        "analyzer-schemas/cre_loan_analyzer.json"
    ];

    private async Task EnsureExtractionAnalyzersExistAsync()
    {
        foreach (var schemaFile in ExtractionAnalyzerSchemas)
        {
            var schemaPath = Path.Combine(AppContext.BaseDirectory, schemaFile);
            if (!File.Exists(schemaPath))
            {
                _logger.LogWarning("Schema file not found: {Path}", schemaPath);
                continue;
            }

            var schemaJson = JsonNode.Parse(await File.ReadAllTextAsync(schemaPath));
            if (schemaJson is null) continue;

            var analyzerId = schemaJson["analyzerId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(analyzerId)) continue;

            if (await AnalyzerIsReadyAsync(analyzerId))
            {
                _logger.LogInformation("Extraction analyzer already ready: {Id}", analyzerId);
                continue;
            }

            _logger.LogInformation("Creating extraction analyzer: {Id}", analyzerId);

            // Remove analyzerId from the body (it goes in the URL)
            schemaJson.AsObject().Remove("analyzerId");

            var url = $"{_endpoint}/contentunderstanding/analyzers/{analyzerId}?api-version={ClassifierApiVersion}";
            var response = await PutJsonAsync(url, schemaJson);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode == 409)
                {
                    _logger.LogInformation("Analyzer already exists: {Id}", analyzerId);
                    continue;
                }
                throw new InvalidOperationException($"Failed to create analyzer {analyzerId} ({(int)response.StatusCode}): {err}");
            }

            var operationLocation = GetHeader(response, "Operation-Location");
            if (operationLocation is not null)
                await PollForCompletionAsync(operationLocation);

            _logger.LogInformation("Extraction analyzer provisioned: {Id}", analyzerId);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Defaults provisioning (set default completion model deployment)
    // ────────────────────────────────────────────────────────────────────────

    private async Task EnsureDefaultsSetAsync()
    {
        _logger.LogInformation("Setting Content Understanding defaults for model gpt-4.1");

        var defaultsUrl = $"{_endpoint}/contentunderstanding/defaults?api-version={ClassifierApiVersion}";
        var body = new JsonObject
        {
            ["modelDeployments"] = new JsonObject { ["gpt-4.1"] = "gpt-4.1" }
        };

        var response = await PatchJsonAsync(defaultsUrl, body);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to set defaults ({StatusCode}): {Error}", (int)response.StatusCode, err);
            // Don't throw — the classifier PUT may still succeed if defaults were already set
        }
        else
        {
            _logger.LogInformation("Content Understanding defaults set successfully");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Polling
    // ────────────────────────────────────────────────────────────────────────

    private async Task<JsonNode> PollForCompletionAsync(string operationUrl, int maxWaitSeconds = 900)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        var delay = TimeSpan.FromSeconds(PollingIntervalSeconds);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 30));

            ApplyHeaders();
            var response = await _httpClient.GetAsync(operationUrl);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Polling failed ({(int)response.StatusCode}): {err}");
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
                ?? throw new InvalidOperationException("Empty response while polling");
            var status = json["status"]?.GetValue<string>();

            _logger.LogInformation("Operation status: {Status}", status);

            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return json;

            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Operation failed: {json["error"]?.ToJsonString() ?? "(no detail)"}");
        }

        throw new TimeoutException($"Content Understanding operation did not complete within {maxWaitSeconds} seconds.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Analyzer existence check
    // ────────────────────────────────────────────────────────────────────────

    private async Task<bool> AnalyzerIsReadyAsync(string analyzerId)
    {
        var json = await GetAnalyzerAsync(analyzerId);
        var status = json?["status"]?.GetValue<string>();
        if (string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase))
            return true;

        _logger.LogWarning("Analyzer '{Id}' status is '{Status}' — will re-provision.", analyzerId, status);
        return false;
    }

    private async Task<JsonNode?> GetAnalyzerAsync(string analyzerId)
    {
        ApplyHeaders();
        var response = await _httpClient.GetAsync(
            $"{_endpoint}/contentunderstanding/analyzers/{analyzerId}?api-version={ClassifierApiVersion}");

        if (!response.IsSuccessStatusCode)
            return null;

        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    // ────────────────────────────────────────────────────────────────────────
    // SAS URL generation
    // ────────────────────────────────────────────────────────────────────────

    private string GenerateBlobSasUrl(string containerName, string blobName)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(containerName).GetBlobClient(blobName);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return blobClient.GenerateSasUri(sasBuilder).ToString();
    }

    // ────────────────────────────────────────────────────────────────────────
    // HTTP helpers
    // ────────────────────────────────────────────────────────────────────────

    private void ApplyHeaders()
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _apiKey);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object body)
    {
        ApplyHeaders();
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _httpClient.PostAsync(url, content);
    }

    private async Task<HttpResponseMessage> PutJsonAsync(string url, JsonNode body)
    {
        ApplyHeaders();
        var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await _httpClient.PutAsync(url, content);
    }

    private async Task<HttpResponseMessage> PatchJsonAsync(string url, JsonNode body)
    {
        ApplyHeaders();
        var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
        return await _httpClient.SendAsync(request);
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(name, out values))
            return values.FirstOrDefault();
        return null;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Two-step extraction: call per-segment analyzer via 2025-05-01-preview
    // ────────────────────────────────────────────────────────────────────────

    public async Task<JsonElement?> AnalyzeWithExtractorAsync(string containerName, string blobPath, string analyzerId)
    {
        _logger.LogInformation("Calling extraction analyzer {AnalyzerId} for {BlobPath}", analyzerId, blobPath);

        var sasUrl = GenerateBlobSasUrl(containerName, blobPath);
        var analyzeUrl = $"{_endpoint}/contentunderstanding/analyzers/{analyzerId}:analyze?api-version={ClassifierApiVersion}";
        var body = new { inputs = new[] { new { url = sasUrl } } };

        var response = await PostJsonAsync(analyzeUrl, body);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("Extraction analyze failed for {AnalyzerId}: {Error}", analyzerId, err);
            return null;
        }

        var operationLocation = GetHeader(response, "Operation-Location");
        if (operationLocation is null)
        {
            _logger.LogError("No Operation-Location header from {AnalyzerId}", analyzerId);
            return null;
        }

        var rawJson = await PollForCompletionAsync(operationLocation);

        // Return contents[0] (includes fields + markdown + pages) for citation/confidence enrichment
        var contentsNode = rawJson["result"]?["contents"];
        if (contentsNode is JsonArray arr && arr.Count > 0)
        {
            var jsonString = arr[0]!.ToJsonString();
            var jsonDoc = JsonDocument.Parse(jsonString);
            return jsonDoc.RootElement;
        }

        _logger.LogError("No contents found in extraction response for {AnalyzerId}", analyzerId);
        return null;
    }

    private sealed record GptScoringResult
    {
        public int ConfidencePercent { get; init; }
        public string Reasoning { get; init; } = string.Empty;
        public List<string> MatchedIndicators { get; init; } = [];
        public List<string> MissingIndicators { get; init; } = [];
        public string? CompetingCategory { get; init; }
        public int CompetingConfidencePercent { get; init; }
    }
}
