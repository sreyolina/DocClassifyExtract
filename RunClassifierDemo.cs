// ============================================================================
// RunClassifierDemo.cs — HTTP-triggered classifier demo endpoint
//
// To test: POST or GET to http://localhost:7071/api/ClassifyDemo?blob=YourDoc.pdf
// ============================================================================

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace DocClassifyExtract;

public class RunClassifierDemoFunction
{
    private readonly ILogger<RunClassifierDemoFunction> _logger;

    public RunClassifierDemoFunction(ILogger<RunClassifierDemoFunction> logger)
    {
        _logger = logger;
    }

    [Function("ClassifyDemo")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        var blobName = System.Web.HttpUtility.ParseQueryString(req.Url.Query).Get("blob");
        if (string.IsNullOrWhiteSpace(blobName))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Please provide ?blob=<filename> query parameter");
            return badResponse;
        }

        _logger.LogInformation("ClassifyDemo triggered for: {BlobName}", blobName);

        await ClassifierDemo.RunAsync(blobName);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync($"Classification demo completed for: {blobName}\nCheck the output .txt file in the working directory.");
        return response;
    }
}
