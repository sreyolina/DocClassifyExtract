using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Storage.Blobs;
using DocClassifyExtract.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService();

// Register Azure Blob Storage client
builder.Services.AddSingleton(serviceProvider =>
{
    var connectionString = builder.Configuration["AzureWebJobsStorage"]
        ?? throw new InvalidOperationException("AzureWebJobsStorage connection string not found");
    return new BlobServiceClient(connectionString);
});

// Register HTTP client for Content Understanding service
builder.Services.AddHttpClient<IContentUnderstandingService, ContentUnderstandingService>();

// Register extraction, routing & database services
builder.Services.AddScoped<ICitationService, CitationService>();
builder.Services.AddScoped<IDocumentFieldExtractor, DocumentFieldExtractor>();
builder.Services.AddScoped<IFeatureRefService, FeatureRefService>();
builder.Services.AddScoped<IDatabaseService, DatabaseService>();
builder.Services.AddScoped<IBlobRoutingService, BlobRoutingService>();

builder.Build().Run();
