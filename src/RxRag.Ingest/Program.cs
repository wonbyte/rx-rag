using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RxRag.Core;
using RxRag.Core.Ingestion;

// Usage: dotnet run --project src/RxRag.Ingest [generic names...]
// With no names, we load a default set that covers the eval questions.
string[] defaults =
[
    "ibuprofen", "acetaminophen", "aspirin", "naproxen",
    "diphenhydramine", "loratadine", "cetirizine",
    "omeprazole", "famotidine",
    "warfarin", "metformin", "lisinopril", "atorvastatin",
];

var drugs = args.Length > 0 ? args : defaults;

// We do not pass args to the builder: drug names are not config keys.
// Config comes from appsettings and environment variables (your .env).
var builder = Host.CreateApplicationBuilder();

// HttpClient and Polly log every request and retry attempt at Information.
// Useful when debugging, noise otherwise. Warning still shows real
// problems (retries that fail, timeouts, 5xx responses).
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Polly", LogLevel.Warning);

builder.Services.AddRxRag(builder.Configuration);
using var host = builder.Build();

// Ctrl+C asks the run to stop. The token reaches every API call, so a
// call that is waiting (for example on a rate-limit retry) stops at once.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var ingest = host.Services.GetRequiredService<IngestService>();

try
{
    var count = await ingest.RunAsync(drugs, labelsPerDrug: 3, cts.Token);
    Console.WriteLine($"Indexed {count} chunks.");
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // A stop the user asked for is not an error. Chunks already uploaded
    // stay in the index, and re-running is safe (stable IDs overwrite).
    Console.WriteLine("Stopped. Run again to finish; it is safe to re-run.");
    Environment.ExitCode = 130; // Usual exit code for "stopped by Ctrl+C".
}