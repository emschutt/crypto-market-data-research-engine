using System.Text.Json;
using CryptoMarketDataResearchEngine;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Models;
using CryptoMarketDataResearchEngine.Storage;

var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
    ? args[0].ToLowerInvariant()
    : "collect";
var commandArgs = command == "collect" || command == "inspect" || command == "smoke"
    ? args.Skip(1).ToArray()
    : args;

try
{
    switch (command)
    {
        case "collect":
        {
            var options = CaptureOptions.FromArgs(commandArgs);
            var result = await PipelineRunner.RunAsync(options);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            break;
        }
        case "smoke":
            await PipelineRunner.SmokeAsync();
            break;
        case "inspect":
        {
            var options = CaptureOptions.FromArgs(commandArgs);
            var inspections = new List<DatasetReadback>();
            foreach (var dataset in Datasets.All)
                inspections.Add(await ParquetDatasetInspector.InspectAsync(options.OutputPath, dataset));
            Console.WriteLine(JsonSerializer.Serialize(inspections, new JsonSerializerOptions { WriteIndented = true }));
            break;
        }
        default:
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  dotnet run --project src/CryptoMarketDataResearchEngine -- collect --mode mock --duration 10 --output sample_data/smoke");
            Console.Error.WriteLine("  dotnet run --project src/CryptoMarketDataResearchEngine -- collect --mode live --duration 30 --output data/binance");
            Console.Error.WriteLine("  dotnet run --project src/CryptoMarketDataResearchEngine -- smoke");
            Console.Error.WriteLine("  dotnet run --project src/CryptoMarketDataResearchEngine -- inspect --output sample_data/smoke");
            return 2;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
