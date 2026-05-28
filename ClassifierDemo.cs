// ============================================================================
// ClassifierDemo.cs — Standalone Classifier + GPT Confidence Test
// 
// Usage:  dotnet run -- classify <blob-name>
// Example: dotnet run -- classify "Sample CNI Credit Agreement - JP Morgan.pdf"
//
// This runs ONLY the classification pipeline (no extraction, no DB, no routing)
// and writes a formatted output file to: classifier_output_{timestamp}.txt
// ============================================================================

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace DocClassifyExtract;

public static class ClassifierDemo
{
    // ─── Configuration (reads from local.settings.json) ───────────────────────
    private const string ClassifierApiVersion = "2025-11-01";
    private const string OpenAiApiVersion = "2024-12-01-preview";
    private const string ClassifierId = "doc_classifier_cre_cni_valuation_confidence_score_other";
    private const string GptDeployment = "gpt-4.1";
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

    // ─── Entry Point ──────────────────────────────────────────────────────────

    public static async Task RunAsync(string blobName)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║       DOCUMENT CLASSIFIER DEMO — Classification Only        ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Load settings from local.settings.json
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
        if (!File.Exists(settingsPath))
            settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");

        if (!File.Exists(settingsPath))
        {
            Console.WriteLine("ERROR: local.settings.json not found.");
            return;
        }

        var settings = JsonNode.Parse(File.ReadAllText(settingsPath));
        var endpoint = settings?["Values"]?["AZURE_CONTENT_UNDERSTANDING_ENDPOINT"]?.GetValue<string>()?.TrimEnd('/')
            ?? throw new Exception("AZURE_CONTENT_UNDERSTANDING_ENDPOINT not found");
        var apiKey = settings?["Values"]?["AZURE_CONTENT_UNDERSTANDING_API_KEY"]?.GetValue<string>()
            ?? throw new Exception("AZURE_CONTENT_UNDERSTANDING_API_KEY not found");
        var storageConnStr = settings?["Values"]?["AzureWebJobsStorage"]?.GetValue<string>()
            ?? throw new Exception("AzureWebJobsStorage not found");

        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
        var blobServiceClient = new BlobServiceClient(storageConnStr);

        var containerName = "genpact";
        var blobPath = $"incoming-documents/{blobName}";

        Console.WriteLine($"  Document:  {blobName}");
        Console.WriteLine($"  Blob Path: {containerName}/{blobPath}");
        Console.WriteLine($"  Endpoint:  {endpoint}");
        Console.WriteLine($"  Classifier: {ClassifierId}");
        Console.WriteLine();

        var output = new StringBuilder();
        output.AppendLine("═══════════════════════════════════════════════════════════════");
        output.AppendLine("  DOCUMENT CLASSIFIER DEMO — OUTPUT REPORT");
        output.AppendLine("═══════════════════════════════════════════════════════════════");
        output.AppendLine($"  Date:       {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        output.AppendLine($"  Document:   {blobName}");
        output.AppendLine($"  Classifier: {ClassifierId}");
        output.AppendLine("═══════════════════════════════════════════════════════════════");
        output.AppendLine();

        // Step 1: Generate SAS URL
        Console.WriteLine("► Step 1: Generating SAS URL for blob...");
        var sasUrl = GenerateSasUrl(blobServiceClient, containerName, blobPath);
        Console.WriteLine("  ✓ SAS URL generated");
        output.AppendLine("[Step 1] SAS URL generated for blob access");
        output.AppendLine();

        // Step 2: Submit to classifier
        Console.WriteLine("► Step 2: Submitting document to Azure Content Understanding classifier...");
        var analyzeUrl = $"{endpoint}/contentunderstanding/analyzers/{ClassifierId}:analyze?api-version={ClassifierApiVersion}";
        var body = JsonSerializer.Serialize(new { inputs = new[] { new { url = sasUrl } } });

        var request = new HttpRequestMessage(HttpMethod.Post, analyzeUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"  ✗ Classification request failed ({(int)response.StatusCode}): {err}");
            return;
        }

        string? operationLocation = null;
        if (response.Headers.TryGetValues("Operation-Location", out var vals))
            operationLocation = vals.FirstOrDefault();

        if (operationLocation is null)
        {
            Console.WriteLine("  ✗ No Operation-Location header received");
            return;
        }

        Console.WriteLine("  ✓ Submitted — polling for results...");
        output.AppendLine("[Step 2] Document submitted to classifier");
        output.AppendLine($"  API: POST {analyzeUrl}");
        output.AppendLine();

        // Step 3: Poll for completion
        Console.WriteLine("► Step 3: Polling for classification result...");
        var rawJson = await PollForCompletionAsync(httpClient, apiKey, operationLocation);
        Console.WriteLine("  ✓ Classification completed");
        output.AppendLine("[Step 3] Classification completed successfully");
        output.AppendLine();

        // Step 4: Parse segments
        Console.WriteLine("► Step 4: Parsing classification results...");
        output.AppendLine("───────────────────────────────────────────────────────────────");
        output.AppendLine("  CLASSIFICATION RESULTS");
        output.AppendLine("───────────────────────────────────────────────────────────────");

        var contentsArray = rawJson["result"]?["contents"];
        var topLevelContent = rawJson["result"]?["contents"]?[0];
        var segments = new List<(string Category, int StartPage, int EndPage, double NativeConfidence)>();

        if (contentsArray is JsonArray contents)
        {
            foreach (var content in contents)
            {
                if (content is null) continue;
                var category = content["category"]?.GetValue<string>();
                if (string.IsNullOrEmpty(category)) continue;

                var startPage = content["startPageNumber"]?.GetValue<int>() ?? 0;
                var endPage = content["endPageNumber"]?.GetValue<int>() ?? 0;
                var confidence = content["confidence"]?.GetValue<double>() ?? 0d;

                segments.Add((category, startPage, endPage, confidence));
            }
        }

        // Fallback: check segments array inside contents[0]
        if (segments.Count == 0)
        {
            var segmentsNode = rawJson["result"]?["contents"]?[0]?["segments"]?.AsArray();
            if (segmentsNode is not null)
            {
                foreach (var s in segmentsNode)
                {
                    if (s is null) continue;
                    var category = s["category"]?.GetValue<string>() ?? string.Empty;
                    var startPage = s["startPageNumber"]?.GetValue<int>() ?? 0;
                    var endPage = s["endPageNumber"]?.GetValue<int>() ?? 0;
                    var confidence = s["confidence"]?.GetValue<double>() ?? 0d;

                    segments.Add((category, startPage, endPage, confidence));
                }
            }
        }

        Console.WriteLine($"  ✓ Found {segments.Count} segment(s)");
        output.AppendLine();

        foreach (var seg in segments)
        {
            Console.WriteLine($"    • Pages {seg.StartPage}-{seg.EndPage}: {seg.Category}");
            output.AppendLine($"  Segment: Pages {seg.StartPage}-{seg.EndPage}");
            output.AppendLine($"    Category:           {seg.Category}");
        }
        output.AppendLine();

        // Step 5: GPT-4.1 Confidence Scoring
        Console.WriteLine();
        Console.WriteLine("► Step 5: Running GPT-4.1 confidence validation...");
        output.AppendLine("───────────────────────────────────────────────────────────────");
        output.AppendLine("  GPT-4.1 CONFIDENCE VALIDATION");
        output.AppendLine("───────────────────────────────────────────────────────────────");
        output.AppendLine();

        // Re-parse contents to get markdown for GPT scoring
        if (contentsArray is JsonArray contentsForGpt)
        {
            int segIndex = 0;
            foreach (var content in contentsForGpt)
            {
                if (content is null) continue;
                var category = content["category"]?.GetValue<string>();
                if (string.IsNullOrEmpty(category)) continue;

                Console.WriteLine($"  → Scoring segment: {category}...");

                if (string.Equals(category, "Other", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"    ⚠ Category 'Other' — skipping GPT (human intervention required)");
                    output.AppendLine($"  ── Segment {++segIndex}: {category} ──");
                    output.AppendLine($"    GPT Score:      N/A (category is 'Other')");
                    output.AppendLine($"    Decision:       ⚠ HUMAN INTERVENTION REQUIRED — unknown category");
                    output.AppendLine();
                    continue;
                }

                // Build excerpt
                var excerpt = BuildExcerpt(content, topLevelContent);
                if (string.IsNullOrWhiteSpace(excerpt))
                {
                    Console.WriteLine($"    ⚠ No excerpt available for scoring");
                    output.AppendLine($"  ── Segment {++segIndex}: {category} ──");
                    output.AppendLine($"    GPT Score:      N/A (no excerpt available)");
                    output.AppendLine();
                    continue;
                }

                // Call GPT
                var gptResult = await CallGptForScoringAsync(httpClient, endpoint, apiKey, category, excerpt);

                var decision = gptResult.ConfidencePercent >= ConfidenceThresholdPercent
                    ? "✓ PASS — proceed to extraction"
                    : "⚠ FAIL — requires human intervention";

                Console.WriteLine($"    Score: {gptResult.ConfidencePercent}% — {decision}");
                Console.WriteLine($"    Reasoning: {gptResult.Reasoning}");

                output.AppendLine($"  ── Segment {++segIndex}: {category} ──");
                output.AppendLine($"    GPT Confidence Score:       {gptResult.ConfidencePercent}%");
                output.AppendLine($"    Threshold:                  {ConfidenceThresholdPercent}%");
                output.AppendLine($"    Decision:                   {decision}");
                output.AppendLine();
                output.AppendLine($"    Reasoning:");
                output.AppendLine($"      {gptResult.Reasoning}");
                output.AppendLine();
                output.AppendLine($"    Matched Indicators:");
                foreach (var ind in gptResult.MatchedIndicators)
                    output.AppendLine($"      ✓ {ind}");
                output.AppendLine();
                output.AppendLine($"    Missing Indicators:");
                foreach (var ind in gptResult.MissingIndicators)
                    output.AppendLine($"      ✗ {ind}");
                output.AppendLine();
                if (!string.IsNullOrEmpty(gptResult.CompetingCategory) && gptResult.CompetingCategory != "null")
                {
                    output.AppendLine($"    Competing Category:         {gptResult.CompetingCategory} ({gptResult.CompetingConfidencePercent}%)");
                }
                output.AppendLine();
            }
        }

        // Step 6: Summary
        output.AppendLine("───────────────────────────────────────────────────────────────");
        output.AppendLine("  SUMMARY");
        output.AppendLine("───────────────────────────────────────────────────────────────");
        output.AppendLine($"  Total Segments:     {segments.Count}");
        output.AppendLine($"  Classifier Used:    Azure AI Content Understanding");
        output.AppendLine($"  Classifier ID:      {ClassifierId}");
        output.AppendLine($"  Confidence Scorer:  GPT-4.1 (Azure OpenAI)");
        output.AppendLine($"  Threshold:          {ConfidenceThresholdPercent}% — below this requires human review");
        output.AppendLine();
        output.AppendLine("  Pipeline:");
        output.AppendLine("    1. Document uploaded to Azure Blob Storage");
        output.AppendLine("    2. Azure Content Understanding classifies into CRE/CNI/Valuation/Other");
        output.AppendLine("    3. GPT-4.1 validates classification with reasoning and indicator matching");
        output.AppendLine("    4. If confidence ≥ 70%: proceed to field extraction");
        output.AppendLine("    5. If confidence < 70%: flag for human intervention");
        output.AppendLine("═══════════════════════════════════════════════════════════════");

        // Write output file to test_files_fine_tuning folder
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outputFolder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "test_files_fine_tuning");
        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);

        var outputFileName = $"classifier_output_{timestamp}.txt";
        var outputPath = Path.Combine(outputFolder, outputFileName);
        await File.WriteAllTextAsync(outputPath, output.ToString());

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  ✓ Output written to: test_files_fine_tuning/{outputFileName}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // Also write raw JSON for reference
        var rawOutputPath = Path.Combine(outputFolder, $"classifier_raw_{timestamp}.json");
        await File.WriteAllTextAsync(rawOutputPath, rawJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  ✓ Raw JSON saved to: test_files_fine_tuning/classifier_raw_{timestamp}.json");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GenerateSasUrl(BlobServiceClient blobServiceClient, string containerName, string blobPath)
    {
        var blobClient = blobServiceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath);
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);
        return blobClient.GenerateSasUri(sasBuilder).ToString();
    }

    private static async Task<JsonNode> PollForCompletionAsync(HttpClient httpClient, string apiKey, string operationUrl)
    {
        var delay = TimeSpan.FromSeconds(PollingIntervalSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(900);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 30));

            var req = new HttpRequestMessage(HttpMethod.Get, operationUrl);
            req.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
            var response = await httpClient.SendAsync(req);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Polling failed ({(int)response.StatusCode}): {err}");
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
                ?? throw new Exception("Empty polling response");
            var status = json["status"]?.GetValue<string>();

            Console.Write($"    Status: {status}\r");

            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                return json;
            }

            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Operation failed: {json["error"]?.ToJsonString() ?? "(no detail)"}");
        }

        throw new TimeoutException("Classification did not complete within 900 seconds.");
    }

    private static string BuildExcerpt(JsonNode segmentContent, JsonNode? topLevelContent)
    {
        var segmentMarkdown = segmentContent["markdown"]?.GetValue<string>() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(segmentMarkdown))
            return ExtractPageSampledExcerpt(segmentContent, segmentMarkdown);

        var topLevelMarkdown = topLevelContent?["markdown"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(topLevelMarkdown))
            return string.Empty;

        var startPage = segmentContent["startPageNumber"]?.GetValue<int>();
        var endPage = segmentContent["endPageNumber"]?.GetValue<int>();
        return ExtractPageSampledExcerpt(topLevelContent, topLevelMarkdown, startPage, endPage);
    }

    private static string ExtractPageSampledExcerpt(JsonNode? content, string markdown, int? startPage = null, int? endPage = null)
    {
        const int FallbackChars = 4000;

        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var pages = content?["pages"]?.AsArray();
        if (pages is null || pages.Count == 0)
            return markdown.Length > FallbackChars ? markdown[..FallbackChars] : markdown;

        var eligiblePages = pages
            .Where(p => p is not null)
            .Where(p =>
            {
                var pn = p!["pageNumber"]?.GetValue<int>() ?? 0;
                return (!startPage.HasValue || pn >= startPage.Value) && (!endPage.HasValue || pn <= endPage.Value);
            })
            .ToList();

        if (eligiblePages.Count == 0)
            return markdown.Length > FallbackChars ? markdown[..FallbackChars] : markdown;

        var pageLimit = (int)Math.Max(1, Math.Ceiling(eligiblePages.Count * PageSampleRatio));
        var sb = new StringBuilder();

        foreach (var page in eligiblePages.Take(pageLimit))
        {
            var spans = page!["spans"]?.AsArray();
            if (spans is null) continue;
            foreach (var span in spans)
            {
                var offset = span?["offset"]?.GetValue<int>() ?? -1;
                var length = span?["length"]?.GetValue<int>() ?? 0;
                if (offset >= 0 && length > 0 && offset + length <= markdown.Length)
                    sb.Append(markdown, offset, length);
            }
        }

        var excerpt = sb.ToString().Trim();
        return excerpt.Length > 0 ? excerpt : (markdown.Length > FallbackChars ? markdown[..FallbackChars] : markdown);
    }

    private static async Task<GptScoringOutput> CallGptForScoringAsync(
        HttpClient httpClient, string endpoint, string apiKey, string category, string excerpt)
    {
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

        var chatUrl = $"{endpoint}/openai/deployments/{GptDeployment}/chat/completions?api-version={OpenAiApiVersion}";
        var request = new HttpRequestMessage(HttpMethod.Post, chatUrl);
        request.Headers.Add("api-key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"    ⚠ GPT call failed ({(int)response.StatusCode}): {error}");
            return new GptScoringOutput();
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        var content = JsonNode.Parse(responseBody)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;

        try
        {
            var parsed = JsonNode.Parse(content);
            return new GptScoringOutput
            {
                ConfidencePercent = parsed?["confidence_percent"]?.GetValue<int>() ?? 0,
                Reasoning = parsed?["reasoning"]?.GetValue<string>() ?? string.Empty,
                MatchedIndicators = parsed?["matched_indicators"]?.AsArray()
                    .Select(n => n?.GetValue<string>() ?? string.Empty).ToList() ?? [],
                MissingIndicators = parsed?["missing_indicators"]?.AsArray()
                    .Select(n => n?.GetValue<string>() ?? string.Empty).ToList() ?? [],
                CompetingCategory = parsed?["competing_category"]?.GetValue<string>(),
                CompetingConfidencePercent = parsed?["competing_confidence_percent"]?.GetValue<int>() ?? 0
            };
        }
        catch (JsonException)
        {
            Console.WriteLine($"    ⚠ GPT returned non-JSON: {content}");
            return new GptScoringOutput();
        }
    }

    private sealed class GptScoringOutput
    {
        public int ConfidencePercent { get; init; }
        public string Reasoning { get; init; } = string.Empty;
        public List<string> MatchedIndicators { get; init; } = [];
        public List<string> MissingIndicators { get; init; } = [];
        public string? CompetingCategory { get; init; }
        public int CompetingConfidencePercent { get; init; }
    }
}
