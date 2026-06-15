using Parquet;

namespace CryptoMarketDataResearchEngine.Storage;

public static class ParquetDatasetInspector
{
    public static async Task<DatasetReadback> InspectAsync(string rootPath, string dataset, CancellationToken ct = default)
    {
        var files = Directory.Exists(Path.Combine(rootPath, dataset))
            ? Directory.EnumerateFiles(Path.Combine(rootPath, dataset), "*.parquet", SearchOption.AllDirectories).ToArray()
            : [];

        var columns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowGroups = 0;
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: ct);
            rowGroups += reader.RowGroupCount;
            foreach (var field in reader.Schema.Fields)
                columns.Add(field.Name);
        }

        return new DatasetReadback(dataset, files.Length, rowGroups, columns.ToArray());
    }
}

public sealed record DatasetReadback(
    string Dataset,
    int FileCount,
    int RowGroupCount,
    IReadOnlyList<string> Columns);
