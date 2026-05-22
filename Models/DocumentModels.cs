using System.Text.Json.Serialization;

namespace DocClassifyExtract.Models;

/// <summary>
/// Enum for document types matching the classifier categories
/// </summary>
public enum DocumentType
{
    Loan,       // CRE documents
    Appraisal,  // Valuation documents
    CNI,        // C&I documents
    Other       // Unknown or unsupported documents
}

// ── Extraction result models ────────────────────────────────────────────

/// <summary>
/// Model for storing extraction results in SQL
/// </summary>
public class ExtractedFieldResult
{
    public string DocumentId { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public int FeatureId { get; set; }
    public string Value { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public bool ReviewRequired { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public int CreateUser { get; set; } = 999;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;
    public int ModifyUser { get; set; } = 999;

    // Enhanced citation/confidence properties
    public string FieldMethod { get; set; } = string.Empty; // "extract" or "generate"
    public string ConfidenceReason { get; set; } = string.Empty;
    public FieldCitation? Citation { get; set; }
}

/// <summary>
/// Citation information linking a field value back to its source in the document
/// </summary>
public class FieldCitation
{
    public int PageNumber { get; set; }
    public BoundingBoxInfo? BoundingBox { get; set; }
    public string PdfLink { get; set; } = string.Empty;
    public int SpanOffset { get; set; }
    public int SpanLength { get; set; }
    public string HighlightText { get; set; } = string.Empty;
    public string TextMatch { get; set; } = string.Empty;
}

/// <summary>
/// Parsed bounding box coordinates from the source field
/// </summary>
public class BoundingBoxInfo
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class JobDetails
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public string DocumentId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

// ── Classification result models ────────────────────────────────────────

/// <summary>
/// Result of the classifier: which categories/segments were found and their extracted fields
/// </summary>
public class ClassificationResult
{
    public string BlobName { get; init; } = string.Empty;
    public List<ClassifiedSegment> Segments { get; init; } = [];
}

/// <summary>
/// A single classified segment with category, page range, and extracted fields (from routed analyzer)
/// </summary>
public class ClassifiedSegment
{
    public string Category { get; init; } = string.Empty;
    public int StartPageNumber { get; init; }
    public int EndPageNumber { get; init; }
    public double Confidence { get; init; }
    public DocumentType DocumentType { get; init; }
    public int ConfidencePercent { get; init; }
    public string Reasoning { get; init; } = string.Empty;
    public List<string> MatchedIndicators { get; init; } = [];
    public List<string> MissingIndicators { get; init; } = [];
    public string? CompetingCategory { get; init; }
    public int CompetingConfidencePercent { get; init; }
    public bool RequiresHumanIntervention { get; init; }
}

// ── Content Understanding API response models ───────────────────────────

public class ContentUnderstandingAnalysisResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public ContentUnderstandingAnalysisResult? Result { get; set; }
}

public class ContentUnderstandingAnalysisResult
{
    [JsonPropertyName("analyzerId")]
    public string AnalyzerId { get; set; } = string.Empty;

    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("warnings")]
    public List<ContentUnderstandingWarning> Warnings { get; set; } = new();

    [JsonPropertyName("contents")]
    public List<ContentUnderstandingContent> Contents { get; set; } = new();
}

public class ContentUnderstandingWarning
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class ContentUnderstandingContent
{
    [JsonPropertyName("markdown")]
    public string Markdown { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public Dictionary<string, ContentUnderstandingField> Fields { get; set; } = new();

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("startPageNumber")]
    public int StartPageNumber { get; set; }

    [JsonPropertyName("endPageNumber")]
    public int EndPageNumber { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("pages")]
    public List<ContentUnderstandingPage> Pages { get; set; } = new();

    [JsonPropertyName("paragraphs")]
    public List<ContentUnderstandingParagraph> Paragraphs { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<ContentUnderstandingSection> Sections { get; set; } = new();

    [JsonPropertyName("tables")]
    public List<ContentUnderstandingTable> Tables { get; set; } = new();
}

public class ContentUnderstandingField
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("valueString")]
    public string? ValueString { get; set; }

    [JsonPropertyName("valueNumber")]
    public double? ValueNumber { get; set; }

    [JsonPropertyName("valueDate")]
    public DateTime? ValueDate { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("span")]
    public ContentUnderstandingSpan? Span { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;
}

public class ContentUnderstandingPage
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("angle")]
    public double Angle { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonPropertyName("spans")]
    public List<ContentUnderstandingSpan> Spans { get; set; } = new();

    [JsonPropertyName("words")]
    public List<ContentUnderstandingWord> Words { get; set; } = new();

    [JsonPropertyName("lines")]
    public List<ContentUnderstandingLine> Lines { get; set; } = new();
}

public class ContentUnderstandingWord
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("span")]
    public ContentUnderstandingSpan Span { get; set; } = new();

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;
}

public class ContentUnderstandingLine
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("span")]
    public ContentUnderstandingSpan Span { get; set; } = new();
}

public class ContentUnderstandingParagraph
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("span")]
    public ContentUnderstandingSpan Span { get; set; } = new();

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

public class ContentUnderstandingSection
{
    [JsonPropertyName("span")]
    public ContentUnderstandingSpan Span { get; set; } = new();

    [JsonPropertyName("elements")]
    public List<string> Elements { get; set; } = new();
}

public class ContentUnderstandingTable
{
    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("columnCount")]
    public int ColumnCount { get; set; }

    [JsonPropertyName("cells")]
    public List<ContentUnderstandingTableCell> Cells { get; set; } = new();

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("span")]
    public ContentUnderstandingSpan Span { get; set; } = new();
}

public class ContentUnderstandingTableCell
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("rowIndex")]
    public int RowIndex { get; set; }

    [JsonPropertyName("columnIndex")]
    public int ColumnIndex { get; set; }

    [JsonPropertyName("rowSpan")]
    public int RowSpan { get; set; }

    [JsonPropertyName("columnSpan")]
    public int ColumnSpan { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("span")]
    public ContentUnderstandingSpan Span { get; set; } = new();

    [JsonPropertyName("elements")]
    public List<string> Elements { get; set; } = new();
}

public class ContentUnderstandingSpan
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }
}
