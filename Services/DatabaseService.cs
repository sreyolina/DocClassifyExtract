using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DocClassifyExtract.Models;

namespace DocClassifyExtract.Services;

public interface IDatabaseService
{
    Task<bool> InsertFeatureDataAsync(IEnumerable<ExtractedFieldResult> featureData);
    Task<bool> InsertJobDetailsAsync(JobDetails jobDetails);
    Task<bool> InsertDocumentProcessingDataAsync(IEnumerable<ExtractedFieldResult> featureData, JobDetails jobDetails);
}

public class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _connectionString = configuration.GetConnectionString("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString not found in configuration");
        _logger = logger;
    }

    public async Task<bool> InsertFeatureDataAsync(IEnumerable<ExtractedFieldResult> featureData)
    {
        if (!featureData.Any())
        {
            _logger.LogWarning("No feature data to insert");
            return true;
        }

        const string sql = @"
            MERGE INTO dbo.FeatureData AS target
            USING (VALUES (@DocumentId, @FeatureId, @FieldName, @Value, @Confidence, @FieldMethod, @ConfidenceReason, @ReviewRequired,
                           @CitationPageNumber, @CitationPdfLink, @CitationSpanOffset, @CitationSpanLength,
                           @CitationHighlightText, @CitationTextMatch,
                           @CitationBoundingBoxX, @CitationBoundingBoxY, @CitationBoundingBoxWidth, @CitationBoundingBoxHeight,
                           @CreatedDate, @CreateUser, @ModifiedDate, @ModifyUser)) 
                AS source (DocumentId, FeatureId, FieldName, Value, Confidence, FieldMethod, ConfidenceReason, ReviewRequired,
                           CitationPageNumber, CitationPdfLink, CitationSpanOffset, CitationSpanLength,
                           CitationHighlightText, CitationTextMatch,
                           CitationBoundingBoxX, CitationBoundingBoxY, CitationBoundingBoxWidth, CitationBoundingBoxHeight,
                           CreatedDate, CreateUser, ModifiedDate, ModifyUser)
            ON target.DocumentId = source.DocumentId AND target.FeatureId = source.FeatureId
            WHEN MATCHED THEN
                UPDATE SET 
                    FieldName = source.FieldName,
                    Value = source.Value,
                    Confidence = source.Confidence,
                    FieldMethod = source.FieldMethod,
                    ConfidenceReason = source.ConfidenceReason,
                    ReviewRequired = source.ReviewRequired,
                    CitationPageNumber = source.CitationPageNumber,
                    CitationPdfLink = source.CitationPdfLink,
                    CitationSpanOffset = source.CitationSpanOffset,
                    CitationSpanLength = source.CitationSpanLength,
                    CitationHighlightText = source.CitationHighlightText,
                    CitationTextMatch = source.CitationTextMatch,
                    CitationBoundingBoxX = source.CitationBoundingBoxX,
                    CitationBoundingBoxY = source.CitationBoundingBoxY,
                    CitationBoundingBoxWidth = source.CitationBoundingBoxWidth,
                    CitationBoundingBoxHeight = source.CitationBoundingBoxHeight,
                    ModifiedDate = source.ModifiedDate,
                    ModifyUser = source.ModifyUser
            WHEN NOT MATCHED THEN
                INSERT (DocumentId, FeatureId, FieldName, Value, Confidence, FieldMethod, ConfidenceReason, ReviewRequired,
                        CitationPageNumber, CitationPdfLink, CitationSpanOffset, CitationSpanLength,
                        CitationHighlightText, CitationTextMatch,
                        CitationBoundingBoxX, CitationBoundingBoxY, CitationBoundingBoxWidth, CitationBoundingBoxHeight,
                        CreatedDate, CreateUser, ModifiedDate, ModifyUser)
                VALUES (source.DocumentId, source.FeatureId, source.FieldName, source.Value, source.Confidence, source.FieldMethod, source.ConfidenceReason, source.ReviewRequired,
                        source.CitationPageNumber, source.CitationPdfLink, source.CitationSpanOffset, source.CitationSpanLength,
                        source.CitationHighlightText, source.CitationTextMatch,
                        source.CitationBoundingBoxX, source.CitationBoundingBoxY, source.CitationBoundingBoxWidth, source.CitationBoundingBoxHeight,
                        source.CreatedDate, source.CreateUser, source.ModifiedDate, source.ModifyUser);";

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var insertedCount = 0;
            foreach (var feature in featureData)
            {
                using var command = new SqlCommand(sql, connection);
                AddFeatureParameters(command, feature);
                await command.ExecuteNonQueryAsync();
                insertedCount++;
            }

            _logger.LogInformation("Successfully inserted/updated {Count} feature data records", insertedCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inserting feature data. Records count: {Count}", featureData.Count());
            return false;
        }
    }

    public async Task<bool> InsertJobDetailsAsync(JobDetails jobDetails)
    {
        const string sql = @"
            MERGE INTO dbo.JobDetails AS target
            USING (VALUES (@JobId, @DocumentId, @Status)) AS source (JobId, DocumentId, Status)
            ON target.JobId = source.JobId
            WHEN MATCHED THEN
                UPDATE SET 
                    DocumentId = source.DocumentId,
                    Status = source.Status
            WHEN NOT MATCHED THEN
                INSERT (JobId, DocumentId, Status)
                VALUES (source.JobId, source.DocumentId, source.Status);";

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@JobId", jobDetails.JobId);
            command.Parameters.AddWithValue("@DocumentId", jobDetails.DocumentId ?? (object)DBNull.Value);
            var status = jobDetails.Status?.Length > 100 ? jobDetails.Status[..100] : jobDetails.Status;
            command.Parameters.AddWithValue("@Status", status ?? (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();

            _logger.LogInformation("Successfully inserted/updated job details for JobId: {JobId}", jobDetails.JobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inserting job details for JobId: {JobId}", jobDetails.JobId);
            return false;
        }
    }

    public async Task<bool> InsertDocumentProcessingDataAsync(IEnumerable<ExtractedFieldResult> featureData, JobDetails jobDetails)
    {
        bool featureDataSuccess = true;
        bool jobDetailsSuccess = true;
        var featureInsertCount = 0;
        var featureErrorCount = 0;

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            if (featureData.Any())
            {
                const string featureSql = @"
                    MERGE INTO dbo.FeatureData AS target
                    USING (VALUES (@DocumentId, @FeatureId, @FieldName, @Value, @Confidence, @FieldMethod, @ConfidenceReason, @ReviewRequired,
                                   @CitationPageNumber, @CitationPdfLink, @CitationSpanOffset, @CitationSpanLength,
                                   @CitationHighlightText, @CitationTextMatch,
                                   @CitationBoundingBoxX, @CitationBoundingBoxY, @CitationBoundingBoxWidth, @CitationBoundingBoxHeight,
                                   @CreatedDate, @CreateUser, @ModifiedDate, @ModifyUser)) 
                        AS source (DocumentId, FeatureId, FieldName, Value, Confidence, FieldMethod, ConfidenceReason, ReviewRequired,
                                   CitationPageNumber, CitationPdfLink, CitationSpanOffset, CitationSpanLength,
                                   CitationHighlightText, CitationTextMatch,
                                   CitationBoundingBoxX, CitationBoundingBoxY, CitationBoundingBoxWidth, CitationBoundingBoxHeight,
                                   CreatedDate, CreateUser, ModifiedDate, ModifyUser)
                    ON target.DocumentId = source.DocumentId AND target.FeatureId = source.FeatureId
                    WHEN MATCHED THEN
                        UPDATE SET 
                            FieldName = source.FieldName,
                            Value = source.Value,
                            Confidence = source.Confidence,
                            FieldMethod = source.FieldMethod,
                            ConfidenceReason = source.ConfidenceReason,
                            ReviewRequired = source.ReviewRequired,
                            CitationPageNumber = source.CitationPageNumber,
                            CitationPdfLink = source.CitationPdfLink,
                            CitationSpanOffset = source.CitationSpanOffset,
                            CitationSpanLength = source.CitationSpanLength,
                            CitationHighlightText = source.CitationHighlightText,
                            CitationTextMatch = source.CitationTextMatch,
                            CitationBoundingBoxX = source.CitationBoundingBoxX,
                            CitationBoundingBoxY = source.CitationBoundingBoxY,
                            CitationBoundingBoxWidth = source.CitationBoundingBoxWidth,
                            CitationBoundingBoxHeight = source.CitationBoundingBoxHeight,
                            ModifiedDate = source.ModifiedDate,
                            ModifyUser = source.ModifyUser
                    WHEN NOT MATCHED THEN
                        INSERT (DocumentId, FeatureId, FieldName, Value, Confidence, FieldMethod, ConfidenceReason, ReviewRequired,
                                CitationPageNumber, CitationPdfLink, CitationSpanOffset, CitationSpanLength,
                                CitationHighlightText, CitationTextMatch,
                                CitationBoundingBoxX, CitationBoundingBoxY, CitationBoundingBoxWidth, CitationBoundingBoxHeight,
                                CreatedDate, CreateUser, ModifiedDate, ModifyUser)
                        VALUES (source.DocumentId, source.FeatureId, source.FieldName, source.Value, source.Confidence, source.FieldMethod, source.ConfidenceReason, source.ReviewRequired,
                                source.CitationPageNumber, source.CitationPdfLink, source.CitationSpanOffset, source.CitationSpanLength,
                                source.CitationHighlightText, source.CitationTextMatch,
                                source.CitationBoundingBoxX, source.CitationBoundingBoxY, source.CitationBoundingBoxWidth, source.CitationBoundingBoxHeight,
                                source.CreatedDate, source.CreateUser, source.ModifiedDate, source.ModifyUser);";

                foreach (var feature in featureData)
                {
                    try
                    {
                        using var command = new SqlCommand(featureSql, connection);
                        AddFeatureParameters(command, feature);
                        await command.ExecuteNonQueryAsync();
                        featureInsertCount++;
                    }
                    catch (Exception featureEx)
                    {
                        featureErrorCount++;
                        _logger.LogError(featureEx,
                            "Failed to insert feature data for DocumentId: {DocumentId}, FeatureId: {FeatureId}",
                            feature.DocumentId, feature.FeatureId);
                        featureDataSuccess = false;
                    }
                }

                if (featureInsertCount > 0)
                    _logger.LogInformation("Successfully inserted {SuccessCount} feature data records", featureInsertCount);

                if (featureErrorCount > 0)
                    _logger.LogWarning("Failed to insert {ErrorCount} feature data records out of {TotalCount}",
                        featureErrorCount, featureData.Count());
            }

            try
            {
                const string jobSql = @"
                    MERGE INTO dbo.JobDetails AS target
                    USING (VALUES (@JobId, @DocumentId, @Status)) AS source (JobId, DocumentId, Status)
                    ON target.JobId = source.JobId
                    WHEN MATCHED THEN
                        UPDATE SET DocumentId = source.DocumentId, Status = source.Status
                    WHEN NOT MATCHED THEN
                        INSERT (JobId, DocumentId, Status) VALUES (source.JobId, source.DocumentId, source.Status);";

                using var jobCommand = new SqlCommand(jobSql, connection);
                jobCommand.Parameters.AddWithValue("@JobId", jobDetails.JobId);
                jobCommand.Parameters.AddWithValue("@DocumentId", jobDetails.DocumentId ?? (object)DBNull.Value);
                jobCommand.Parameters.AddWithValue("@Status", jobDetails.Status ?? (object)DBNull.Value);

                await jobCommand.ExecuteNonQueryAsync();
                _logger.LogInformation("Successfully inserted/updated job details for JobId: {JobId}", jobDetails.JobId);
            }
            catch (Exception jobEx)
            {
                jobDetailsSuccess = false;
                _logger.LogError(jobEx, "Failed to insert job details for JobId: {JobId}", jobDetails.JobId);
            }

            return jobDetailsSuccess || featureInsertCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error in document processing data operation. JobId: {JobId}", jobDetails.JobId);
            return false;
        }
    }

    private static void AddFeatureParameters(SqlCommand command, ExtractedFieldResult feature)
    {
        command.Parameters.AddWithValue("@DocumentId", feature.DocumentId);
        command.Parameters.AddWithValue("@FeatureId", feature.FeatureId);
        command.Parameters.AddWithValue("@FieldName", feature.FieldName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Value", feature.Value ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Confidence", feature.Confidence);
        command.Parameters.AddWithValue("@FieldMethod", feature.FieldMethod ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ConfidenceReason", feature.ConfidenceReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ReviewRequired", feature.ReviewRequired);
        command.Parameters.AddWithValue("@CitationPageNumber", feature.Citation?.PageNumber ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationPdfLink", feature.Citation?.PdfLink ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationSpanOffset", feature.Citation?.SpanOffset ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationSpanLength", feature.Citation?.SpanLength ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationHighlightText", feature.Citation?.HighlightText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationTextMatch", feature.Citation?.TextMatch ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationBoundingBoxX", feature.Citation?.BoundingBox?.X ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationBoundingBoxY", feature.Citation?.BoundingBox?.Y ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationBoundingBoxWidth", feature.Citation?.BoundingBox?.Width ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CitationBoundingBoxHeight", feature.Citation?.BoundingBox?.Height ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@CreatedDate", feature.CreatedDate);
        command.Parameters.AddWithValue("@CreateUser", feature.CreateUser);
        command.Parameters.AddWithValue("@ModifiedDate", feature.ModifiedDate);
        command.Parameters.AddWithValue("@ModifyUser", feature.ModifyUser);
    }
}
