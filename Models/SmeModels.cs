namespace DocClassifyExtract.Models;

/// <summary>
/// Represents a Subject Matter Expert who reviews documents with low-confidence fields.
/// </summary>
public class Sme
{
    public int SmeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DocType { get; set; } = string.Empty;
    public int MaxConcurrentDocs { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Tracks which document is assigned to which SME for HITL review.
/// </summary>
public class DocumentAssignment
{
    public int AssignmentId { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public int SmeId { get; set; }
    public string DocType { get; set; } = string.Empty;
    public string Status { get; set; } = "Assigned"; // Assigned / Completed / Reassigned
    public DateTime AssignedDate { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedDate { get; set; }
}
