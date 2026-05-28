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

    /// <summary>
    /// Reassigns a document to a specific SME. Marks the current assignment as 'Reassigned'
    /// and creates a new assignment for the target SME.
    /// Returns true if reassignment succeeded, false if the target SME is at capacity or invalid.
    /// </summary>
    Task<bool> ReassignDocumentAsync(string documentId, int newSmeId);

    /// <summary>
    /// Gets all active SMEs for a given doc type (used to populate reassignment dropdown).
    /// </summary>
    Task<List<Sme>> GetAvailableSmesForDocTypeAsync(string docType);
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
        using var transaction = connection.BeginTransaction();

        try
        {
            // Acquire exclusive app lock to serialize assignment per doc type.
            // Lock timeout: 60 seconds — more than sufficient since the critical section
            // only runs a few lightweight SQL queries (~100-200ms typically).
            using (var lockCmd = new SqlCommand(
                "DECLARE @result INT; EXEC @result = sp_getapplock @Resource=@LockName, @LockMode='Exclusive', @LockTimeout=60000; SELECT @result;",
                connection, transaction))
            {
                lockCmd.Parameters.AddWithValue("@LockName", $"SMEAssign_{docType}");
                var lockResult = (int)(await lockCmd.ExecuteScalarAsync())!;
                if (lockResult < 0)
                {
                    _logger.LogWarning("Could not acquire assignment lock for DocType '{DocType}' (result={Result}). Document '{DocumentId}' unassigned.",
                        docType, lockResult, documentId);
                    transaction.Rollback();
                    return null;
                }
            }

            // Step 1: Get active SMEs for this doc type, ordered by SmeId
            var activeSmes = new List<int>();
            using (var cmd = new SqlCommand(
                "SELECT SmeId FROM dbo.SME WHERE DocType = @DocType AND IsActive = 1 ORDER BY SmeId", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@DocType", docType);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    activeSmes.Add(reader.GetInt32(0));
            }

            if (activeSmes.Count == 0)
            {
                _logger.LogWarning("No active SMEs found for DocType '{DocType}'", docType);
                transaction.Rollback();
                return null;
            }

            // Step 2: Get the last assigned SME pointer for this doc type
            int? lastSmeId = null;
            using (var cmd = new SqlCommand(
                "SELECT LastAssignedSmeId FROM dbo.RoundRobinPointer WHERE DocType = @DocType", connection, transaction))
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
                    "SELECT COUNT(*) FROM dbo.DocumentAssignment WHERE SmeId = @SmeId AND Status = 'Assigned'", connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@SmeId", candidateId);
                    currentCount = (int)(await cmd.ExecuteScalarAsync())!;
                }

                // Check max concurrent docs for this SME
                int maxDocs = 0;
                using (var cmd = new SqlCommand(
                    "SELECT MaxConcurrentDocs FROM dbo.SME WHERE SmeId = @SmeId", connection, transaction))
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
                      VALUES (@DocumentId, @SmeId, @DocType, 'Assigned', GETUTCDATE())", connection, transaction))
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
                      WHEN NOT MATCHED THEN INSERT (DocType, LastAssignedSmeId) VALUES (source.DocType, source.LastAssignedSmeId);", connection, transaction))
                {
                    cmd.Parameters.AddWithValue("@DocType", docType);
                    cmd.Parameters.AddWithValue("@SmeId", candidateId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Commit releases the app lock — next waiting caller proceeds
                transaction.Commit();

                _logger.LogInformation("Document '{DocumentId}' assigned to SME {SmeId} for DocType '{DocType}'",
                    documentId, candidateId, docType);

                return candidateId;
            }

            // All SMEs at capacity — release lock
            transaction.Commit();

            _logger.LogWarning("All SMEs at capacity for DocType '{DocType}', document '{DocumentId}' unassigned",
                docType, documentId);
            return null;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Error during SME assignment for document '{DocumentId}', DocType '{DocType}'", documentId, docType);
            throw;
        }
    }

    public async Task<List<Sme>> GetAvailableSmesForDocTypeAsync(string docType)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var smes = new List<Sme>();
        using var cmd = new SqlCommand(
            "SELECT SmeId, Name, DocType, MaxConcurrentDocs, IsActive FROM dbo.SME WHERE DocType = @DocType AND IsActive = 1 ORDER BY Name", connection);
        cmd.Parameters.AddWithValue("@DocType", docType);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            smes.Add(new Sme
            {
                SmeId = reader.GetInt32(0),
                Name = reader.GetString(1),
                DocType = reader.GetString(2),
                MaxConcurrentDocs = reader.GetInt32(3),
                IsActive = reader.GetBoolean(4)
            });
        }

        return smes;
    }

    public async Task<bool> ReassignDocumentAsync(string documentId, int newSmeId)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // Step 1: Get the current active assignment for this document
            string? docType = null;
            int? currentSmeId = null;
            using (var cmd = new SqlCommand(
                "SELECT SmeId, DocType FROM dbo.DocumentAssignment WHERE DocumentId = @DocumentId AND Status = 'Assigned'", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@DocumentId", documentId);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    currentSmeId = reader.GetInt32(0);
                    docType = reader.GetString(1);
                }
            }

            if (docType == null)
            {
                _logger.LogWarning("No active assignment found for document '{DocumentId}'", documentId);
                return false;
            }

            // Step 2: Validate the new SME is active and handles this doc type
            int maxDocs = 0;
            bool smeValid = false;
            using (var cmd = new SqlCommand(
                "SELECT MaxConcurrentDocs FROM dbo.SME WHERE SmeId = @SmeId AND DocType = @DocType AND IsActive = 1", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@SmeId", newSmeId);
                cmd.Parameters.AddWithValue("@DocType", docType);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    maxDocs = (int)result;
                    smeValid = true;
                }
            }

            if (!smeValid)
            {
                _logger.LogWarning("SME {SmeId} is not active or does not handle DocType '{DocType}'", newSmeId, docType);
                return false;
            }

            // Step 3: Check the new SME is not at capacity
            int currentCount = 0;
            using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.DocumentAssignment WHERE SmeId = @SmeId AND Status = 'Assigned'", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@SmeId", newSmeId);
                currentCount = (int)(await cmd.ExecuteScalarAsync())!;
            }

            if (currentCount >= maxDocs)
            {
                _logger.LogWarning("SME {SmeId} is at capacity ({Current}/{Max}), cannot reassign", newSmeId, currentCount, maxDocs);
                return false;
            }

            // Step 4: Mark the current assignment as 'Reassigned'
            using (var cmd = new SqlCommand(
                "UPDATE dbo.DocumentAssignment SET Status = 'Reassigned', CompletedDate = GETUTCDATE() WHERE DocumentId = @DocumentId AND Status = 'Assigned'", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@DocumentId", documentId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Step 5: Create new assignment for the target SME
            using (var cmd = new SqlCommand(
                @"INSERT INTO dbo.DocumentAssignment (DocumentId, SmeId, DocType, Status, AssignedDate)
                  VALUES (@DocumentId, @SmeId, @DocType, 'Assigned', GETUTCDATE())", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@DocumentId", documentId);
                cmd.Parameters.AddWithValue("@SmeId", newSmeId);
                cmd.Parameters.AddWithValue("@DocType", docType);
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();

            _logger.LogInformation("Document '{DocumentId}' reassigned from SME {OldSmeId} to SME {NewSmeId}",
                documentId, currentSmeId, newSmeId);
            return true;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Failed to reassign document '{DocumentId}' to SME {SmeId}", documentId, newSmeId);
            throw;
        }
    }
}
