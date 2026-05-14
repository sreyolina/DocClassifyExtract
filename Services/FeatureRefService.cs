using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DocClassifyExtract.Models;

namespace DocClassifyExtract.Services;

public interface IFeatureRefService
{
    Task<Dictionary<string, int>> GetAllFeaturesSimpleAsync();
    Task<int> EnsureFeatureExistsAsync(string featureName, string? documentType = null, string? fieldMethod = null);
}

public class FeatureRefService : IFeatureRefService
{
    private readonly ILogger<FeatureRefService> _logger;
    private readonly string _connectionString;

    public FeatureRefService(ILogger<FeatureRefService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString not configured");
    }

    public async Task<Dictionary<string, int>> GetAllFeaturesSimpleAsync()
    {
        var simpleFeatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            const string query = @"
                SELECT FeatureId, FeatureName
                FROM dbo.FeatureRef
                ORDER BY FeatureName";

            using var command = new SqlCommand(query, connection);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var featureName = reader.GetString(reader.GetOrdinal("FeatureName"));
                var featureId = reader.GetInt32(reader.GetOrdinal("FeatureId"));
                simpleFeatures[featureName] = featureId;
            }

            _logger.LogInformation("Cached {TotalFeatures} features (simple mapping)", simpleFeatures.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving simple feature mapping");
        }

        return simpleFeatures;
    }

    public async Task<int> EnsureFeatureExistsAsync(string featureName, string? documentType = null, string? fieldMethod = null)
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            const string selectSql = "SELECT FeatureId FROM dbo.FeatureRef WHERE FeatureName = @FeatureName";
            using var selectCmd = new SqlCommand(selectSql, connection);
            selectCmd.Parameters.AddWithValue("@FeatureName", featureName);
            var result = await selectCmd.ExecuteScalarAsync();

            if (result != null)
                return (int)result;

            const string insertSql = @"
                INSERT INTO dbo.FeatureRef (FeatureName, DocumentType, FieldMethod)
                OUTPUT INSERTED.FeatureId
                VALUES (@FeatureName, @DocumentType, @FieldMethod)";
            using var insertCmd = new SqlCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue("@FeatureName", featureName);
            insertCmd.Parameters.AddWithValue("@DocumentType", documentType ?? (object)DBNull.Value);
            insertCmd.Parameters.AddWithValue("@FieldMethod", fieldMethod ?? (object)DBNull.Value);

            var newId = (int)(await insertCmd.ExecuteScalarAsync())!;
            _logger.LogInformation("Auto-registered feature: {FeatureName} -> FeatureId: {FeatureId}", featureName, newId);
            return newId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ensuring feature exists: {FeatureName}", featureName);
            return 0;
        }
    }
}
