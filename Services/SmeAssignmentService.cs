using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DocClassifyExtract.Models;

namespace DocClassifyExtract.Services;

public interface ISmeAssignmentService
{
    /// <summary>
    /// Assigns a document to the next available SME via round-robin for the given doc type.
    /// Returns the assigned SmeId, or null if all SMEs are at capacity.
    /// </summary>
    Task<int?> AssignDocumentAsync(string documentId, string docType);
}

public class SmeAssignmentService : ISmeAssignmentService
{
    private readonly string _connectionString;
    private readonly ILogger<SmeAssignmentService> _logger;

    public SmeAssignmentService(IConfiguration configuration, ILogger<SmeAssignmentService> logger)
    {
        _connectionString = configuration.GetConnectionString("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString not found in configuration");
        _logger = logger;
    }

    public async Task<int?> AssignDocumentAsync(string documentId, string docType)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Step 1: Get active SMEs for this doc type, ordered by SmeId
        var activeSmes = new List<int>();
        using (var cmd = new SqlCommand(
            "SELECT SmeId FROM dbo.SME WHERE DocType = @DocType AND IsActive = 1 ORDER BY SmeId", connection))
        {
            cmd.Parameters.AddWithValue("@DocType", docType);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                activeSmes.Add(reader.GetInt32(0));
        }

        if (activeSmes.Count == 0)
        {
            _logger.LogWarning("No active SMEs found for DocType '{DocType}'", docType);
            return null;
        }

        // Step 2: Get the last assigned SME pointer for this doc type
        int? lastSmeId = null;
        using (var cmd = new SqlCommand(
            "SELECT LastAssignedSmeId FROM dbo.RoundRobinPointer WHERE DocType = @DocType", connection))
        {
            cmd.Parameters.AddWithValue("@DocType", docType);
            var result = await cmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
                lastSmeId = (int)result;
        }

        // Step 3: Find the next eligible SME (active + under concurrent limit)
        int startIndex = lastSmeId.HasValue && activeSmes.Contains(lastSmeId.Value)
            ? (activeSmes.IndexOf(lastSmeId.Value) + 1) % activeSmes.Count
            : 0;

        for (int i = 0; i < activeSmes.Count; i++)
        {
            int candidateId = activeSmes[(startIndex + i) % activeSmes.Count];

            // Check current assignment count
            int currentCount = 0;
            using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.DocumentAssignment WHERE SmeId = @SmeId AND Status = 'Assigned'", connection))
            {
                cmd.Parameters.AddWithValue("@SmeId", candidateId);
                currentCount = (int)(await cmd.ExecuteScalarAsync())!;
            }

            // Check max concurrent docs for this SME
            int maxDocs = 0;
            using (var cmd = new SqlCommand(
                "SELECT MaxConcurrentDocs FROM dbo.SME WHERE SmeId = @SmeId", connection))
            {
                cmd.Parameters.AddWithValue("@SmeId", candidateId);
                maxDocs = (int)(await cmd.ExecuteScalarAsync())!;
            }

            if (currentCount >= maxDocs)
            {
                _logger.LogDebug("SME {SmeId} is at capacity ({Current}/{Max}), skipping", candidateId, currentCount, maxDocs);
                continue; // at capacity, try next
            }

            // Step 4: Assign the document to this SME
            using (var cmd = new SqlCommand(
                @"INSERT INTO dbo.DocumentAssignment (DocumentId, SmeId, DocType, Status, AssignedDate)
                  VALUES (@DocumentId, @SmeId, @DocType, 'Assigned', GETUTCDATE())", connection))
            {
                cmd.Parameters.AddWithValue("@DocumentId", documentId);
                cmd.Parameters.AddWithValue("@SmeId", candidateId);
                cmd.Parameters.AddWithValue("@DocType", docType);
                await cmd.ExecuteNonQueryAsync();
            }

            // Step 5: Update the round-robin pointer
            using (var cmd = new SqlCommand(
                @"MERGE dbo.RoundRobinPointer AS target
                  USING (VALUES (@DocType, @SmeId)) AS source (DocType, LastAssignedSmeId)
                  ON target.DocType = source.DocType
                  WHEN MATCHED THEN UPDATE SET LastAssignedSmeId = source.LastAssignedSmeId
                  WHEN NOT MATCHED THEN INSERT (DocType, LastAssignedSmeId) VALUES (source.DocType, source.LastAssignedSmeId);", connection))
            {
                cmd.Parameters.AddWithValue("@DocType", docType);
                cmd.Parameters.AddWithValue("@SmeId", candidateId);
                await cmd.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("Document '{DocumentId}' assigned to SME {SmeId} for DocType '{DocType}'",
                documentId, candidateId, docType);

            return candidateId;
        }

        _logger.LogWarning("All SMEs at capacity for DocType '{DocType}', document '{DocumentId}' unassigned",
            docType, documentId);
        return null;
    }
}
