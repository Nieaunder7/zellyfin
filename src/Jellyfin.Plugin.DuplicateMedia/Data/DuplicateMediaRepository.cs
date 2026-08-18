using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.DuplicateMedia.Models;
using Jellyfin.Plugin.DuplicateMedia.Services;
using MediaBrowser.Controller.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DuplicateMedia.Data;

public sealed class DuplicateMediaRepository
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "New",
        "Reviewed",
        "Ignored"
    };

    private readonly string _databasePath;
    private readonly ILogger<DuplicateMediaRepository> _logger;
    private readonly object _gate = new();

    public DuplicateMediaRepository(IServerConfigurationManager configurationManager, ILogger<DuplicateMediaRepository> logger)
    {
        _databasePath = Path.Combine(configurationManager.ApplicationPaths.DataPath, "duplicate-media.db");
        _logger = logger;
        Initialize();
    }

    public string DatabasePath => _databasePath;

    public long StartRun(long totalItems)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ScanRuns (StartedUtc, Status, TotalItems, ScannedItems, MatchedItems, Error)
                VALUES ($startedUtc, 'Running', $totalItems, 0, 0, '');
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$totalItems", totalItems);
            return (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    public void AddBatch(long runId, IReadOnlyList<ScannedMediaItem> items, long scannedItems, long matchedItems)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO MediaItems (
                    RunId, ItemId, ProductCode, Name, Path, SizeBytes, RuntimeTicks,
                    Width, Height, Container, DateCreatedUtc, ChapterImagesJson, DateModifiedUtc)
                VALUES (
                    $runId, $itemId, $productCode, $name, $path, $sizeBytes,
                    $runtimeTicks, $width, $height, $container, $dateCreatedUtc, $chapterImagesJson, $dateModifiedUtc);
                """;

            var runIdParameter = command.Parameters.Add("$runId", SqliteType.Integer);
            var itemIdParameter = command.Parameters.Add("$itemId", SqliteType.Text);
            var productCodeParameter = command.Parameters.Add("$productCode", SqliteType.Text);
            var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
            var pathParameter = command.Parameters.Add("$path", SqliteType.Text);
            var sizeParameter = command.Parameters.Add("$sizeBytes", SqliteType.Integer);
            var runtimeParameter = command.Parameters.Add("$runtimeTicks", SqliteType.Integer);
            var widthParameter = command.Parameters.Add("$width", SqliteType.Integer);
            var heightParameter = command.Parameters.Add("$height", SqliteType.Integer);
            var containerParameter = command.Parameters.Add("$container", SqliteType.Text);
            var createdParameter = command.Parameters.Add("$dateCreatedUtc", SqliteType.Text);
            var chapterImagesParameter = command.Parameters.Add("$chapterImagesJson", SqliteType.Text);
            var modifiedParameter = command.Parameters.Add("$dateModifiedUtc", SqliteType.Text);

            foreach (var item in items)
            {
                runIdParameter.Value = runId;
                itemIdParameter.Value = item.ItemId;
                productCodeParameter.Value = item.ProductCode;
                nameParameter.Value = item.Name;
                pathParameter.Value = item.Path;
                sizeParameter.Value = item.SizeBytes.HasValue ? item.SizeBytes.Value : DBNull.Value;
                runtimeParameter.Value = item.RuntimeTicks.HasValue ? item.RuntimeTicks.Value : DBNull.Value;
                widthParameter.Value = item.Width;
                heightParameter.Value = item.Height;
                containerParameter.Value = item.Container;
                createdParameter.Value = item.DateCreatedUtc.HasValue
                    ? item.DateCreatedUtc.Value.ToString("O", CultureInfo.InvariantCulture)
                    : DBNull.Value;
                chapterImagesParameter.Value = JsonSerializer.Serialize(item.ChapterImages);
                modifiedParameter.Value = item.DateModifiedUtc.ToString("O", CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }

            using var progressCommand = connection.CreateCommand();
            progressCommand.Transaction = transaction;
            progressCommand.CommandText = """
                UPDATE ScanRuns
                SET ScannedItems = $scannedItems, MatchedItems = $matchedItems
                WHERE Id = $runId;
                """;
            progressCommand.Parameters.AddWithValue("$scannedItems", scannedItems);
            progressCommand.Parameters.AddWithValue("$matchedItems", matchedItems);
            progressCommand.Parameters.AddWithValue("$runId", runId);
            progressCommand.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void CompleteRun(long runId, long scannedItems, long matchedItems)
    {
        FinishRun(runId, "Completed", scannedItems, matchedItems, string.Empty);

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var deleteItems = connection.CreateCommand();
            deleteItems.Transaction = transaction;
            deleteItems.CommandText = "DELETE FROM MediaItems WHERE RunId <> $runId;";
            deleteItems.Parameters.AddWithValue("$runId", runId);
            deleteItems.ExecuteNonQuery();

            using var deleteRuns = connection.CreateCommand();
            deleteRuns.Transaction = transaction;
            deleteRuns.CommandText = "DELETE FROM ScanRuns WHERE Id <> $runId;";
            deleteRuns.Parameters.AddWithValue("$runId", runId);
            deleteRuns.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void CancelRun(long runId, long scannedItems, long matchedItems)
    {
        FinishRun(runId, "Cancelled", scannedItems, matchedItems, string.Empty);
    }

    public void FailRun(long runId, long scannedItems, long matchedItems, string error)
    {
        FinishRun(runId, "Failed", scannedItems, matchedItems, error);
    }

    public DuplicateMediaSummary GetSummary()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var latestRun = ReadLatestRun(connection);
            var resultRunId = GetResultRunId(connection);
            if (resultRunId == 0)
            {
                return new DuplicateMediaSummary(latestRun, 0, 0, 0, 0, _databasePath);
            }

            var groups = ReadGroupHeaders(connection, resultRunId);
            return new DuplicateMediaSummary(
                latestRun,
                resultRunId,
                groups.Count,
                groups.Sum(group => group.FileCount),
                groups.Sum(group => group.PotentialSavingsBytes),
                _databasePath);
        }
    }

    public DuplicateGroupPage GetGroups(int startIndex, int limit, string? search, string? status, string? classification)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var runId = GetResultRunId(connection);
            if (runId == 0)
            {
                return new DuplicateGroupPage(startIndex, 0, Array.Empty<DuplicateGroup>());
            }

            IEnumerable<DuplicateGroup> query = ReadGroupHeaders(connection, runId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(group => group.ProductCode.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(group => group.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(classification) && !classification.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(group => group.Classification.Equals(classification, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = query.ToList();
            var page = filtered.Skip(startIndex).Take(limit)
                .Select(group => group with { Items = ReadItems(connection, runId, group.ProductCode) })
                .ToArray();
            return new DuplicateGroupPage(startIndex, filtered.Count, page);
        }
    }

    public IReadOnlyList<DuplicateGroup> GetAllGroups()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var runId = GetResultRunId(connection);
            if (runId == 0)
            {
                return Array.Empty<DuplicateGroup>();
            }

            return ReadGroupHeaders(connection, runId)
                .Select(group => group with { Items = ReadItems(connection, runId, group.ProductCode) })
                .ToArray();
        }
    }

    public DuplicateGroup? GetGroup(string productCode)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var runId = GetResultRunId(connection);
            if (runId == 0)
            {
                return null;
            }

            var normalizedCode = productCode.Trim().ToUpperInvariant();
            var items = ReadItems(connection, runId, normalizedCode);
            if (items.Count < 2)
            {
                return null;
            }

            var classification = DuplicateGroupClassifier.Classify(items);
            var totalSize = items.Sum(item => item.SizeBytes.GetValueOrDefault());
            var potentialSavings = classification == "Multipart"
                ? 0
                : totalSize - items.Max(item => item.SizeBytes.GetValueOrDefault());
            return new DuplicateGroup(
                normalizedCode,
                items.Count,
                classification,
                ReadGroupStatus(connection, normalizedCode),
                totalSize,
                potentialSavings,
                items);
        }
    }

    public void RemoveScannedItem(string itemId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM MediaItems WHERE ItemId = $itemId;";
            command.Parameters.AddWithValue("$itemId", itemId);
            command.ExecuteNonQuery();
        }
    }

    public void RecordDeletion(string productCode, PermanentDeleteItemResult result)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO DeletionAudit (
                    DeletedUtc, ProductCode, ItemId, Path, SizeBytes,
                    FileDeleted, IndexRemoved, Error)
                VALUES (
                    $deletedUtc, $productCode, $itemId, $path, $sizeBytes,
                    $fileDeleted, $indexRemoved, $error);
                """;
            command.Parameters.AddWithValue("$deletedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$productCode", productCode.ToUpperInvariant());
            command.Parameters.AddWithValue("$itemId", result.ItemId);
            command.Parameters.AddWithValue("$path", result.Path);
            command.Parameters.AddWithValue("$sizeBytes", result.SizeBytes);
            command.Parameters.AddWithValue("$fileDeleted", result.FileDeleted ? 1 : 0);
            command.Parameters.AddWithValue("$indexRemoved", result.IndexRemoved ? 1 : 0);
            command.Parameters.AddWithValue("$error", result.Error);
            command.ExecuteNonQuery();
        }
    }

    public void SetGroupStatus(string productCode, string status)
    {
        if (!AllowedStatuses.Contains(status))
        {
            throw new ArgumentException("Status must be New, Reviewed, or Ignored.", nameof(status));
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO GroupDecisions (ProductCode, Status, UpdatedUtc)
                VALUES ($productCode, $status, $updatedUtc)
                ON CONFLICT(ProductCode) DO UPDATE SET
                    Status = excluded.Status,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$productCode", productCode.ToUpperInvariant());
            command.Parameters.AddWithValue("$status", AllowedStatuses.Single(candidate => candidate.Equals(status, StringComparison.OrdinalIgnoreCase)));
            command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    private void Initialize()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS ScanRuns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartedUtc TEXT NOT NULL,
                    CompletedUtc TEXT NULL,
                    Status TEXT NOT NULL,
                    TotalItems INTEGER NOT NULL,
                    ScannedItems INTEGER NOT NULL,
                    MatchedItems INTEGER NOT NULL,
                    Error TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS MediaItems (
                    RunId INTEGER NOT NULL,
                    ItemId TEXT NOT NULL,
                    ProductCode TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Path TEXT NOT NULL,
                    SizeBytes INTEGER NULL,
                    RuntimeTicks INTEGER NULL,
                    Width INTEGER NOT NULL,
                    Height INTEGER NOT NULL,
                    Container TEXT NOT NULL,
                    DateCreatedUtc TEXT NULL,
                    ChapterImagesJson TEXT NOT NULL DEFAULT '[]',
                    DateModifiedUtc TEXT NOT NULL,
                    PRIMARY KEY (RunId, ItemId, ProductCode)
                );
                CREATE INDEX IF NOT EXISTS IX_MediaItems_Run_ProductCode ON MediaItems(RunId, ProductCode);
                CREATE TABLE IF NOT EXISTS GroupDecisions (
                    ProductCode TEXT PRIMARY KEY,
                    Status TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS DeletionAudit (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeletedUtc TEXT NOT NULL,
                    ProductCode TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    Path TEXT NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    FileDeleted INTEGER NOT NULL,
                    IndexRemoved INTEGER NOT NULL,
                    Error TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_DeletionAudit_ProductCode ON DeletionAudit(ProductCode, DeletedUtc);
                UPDATE ScanRuns
                SET Status = 'Aborted', CompletedUtc = $completedUtc, Error = 'Server stopped before the scan completed.'
                WHERE Status = 'Running';
                """;
            command.Parameters.AddWithValue("$completedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            EnsureColumn(connection, "MediaItems", "DateCreatedUtc", "TEXT NULL");
            EnsureColumn(connection, "MediaItems", "ChapterImagesJson", "TEXT NOT NULL DEFAULT '[]'");
            _logger.LogInformation("Duplicate Media database initialized at {DatabasePath}", _databasePath);
        }
    }

    private void FinishRun(long runId, string status, long scannedItems, long matchedItems, string error)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ScanRuns
                SET CompletedUtc = $completedUtc, Status = $status,
                    ScannedItems = $scannedItems, MatchedItems = $matchedItems, Error = $error
                WHERE Id = $runId;
                """;
            command.Parameters.AddWithValue("$completedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$scannedItems", scannedItems);
            command.Parameters.AddWithValue("$matchedItems", matchedItems);
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$runId", runId);
            command.ExecuteNonQuery();
        }
    }

    private static ScanRunInfo? ReadLatestRun(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Status, StartedUtc, CompletedUtc, TotalItems, ScannedItems, MatchedItems, Error
            FROM ScanRuns ORDER BY Id DESC LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ScanRunInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetString(7));
    }

    private static long GetResultRunId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM ScanRuns WHERE Status = 'Completed' ORDER BY Id DESC LIMIT 1;";
        return (long?)(command.ExecuteScalar()) ?? 0L;
    }

    private static List<DuplicateGroup> ReadGroupHeaders(SqliteConnection connection, long runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ProductCode
            FROM MediaItems
            WHERE RunId = $runId
            GROUP BY ProductCode
            HAVING COUNT(DISTINCT ItemId) > 1
            ORDER BY COUNT(DISTINCT ItemId) DESC, ProductCode COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        var codes = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                codes.Add(reader.GetString(0));
            }
        }

        var groups = new List<DuplicateGroup>(codes.Count);
        foreach (var code in codes)
        {
            var items = ReadItems(connection, runId, code);
            var classification = DuplicateGroupClassifier.Classify(items);
            var totalSize = items.Sum(item => item.SizeBytes.GetValueOrDefault());
            var potentialSavings = classification == "Multipart" || items.Count == 0
                ? 0
                : totalSize - items.Max(item => item.SizeBytes.GetValueOrDefault());
            groups.Add(new DuplicateGroup(
                code,
                items.Count,
                classification,
                ReadGroupStatus(connection, code),
                totalSize,
                potentialSavings,
                Array.Empty<DuplicateMediaItem>()));
        }

        return groups;
    }

    private static IReadOnlyList<DuplicateMediaItem> ReadItems(SqliteConnection connection, long runId, string productCode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ItemId, ProductCode, Name, Path, SizeBytes, RuntimeTicks,
                   Width, Height, Container, DateCreatedUtc, ChapterImagesJson, DateModifiedUtc
            FROM MediaItems
            WHERE RunId = $runId AND ProductCode = $productCode
            ORDER BY Path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$productCode", productCode);

        var items = new List<DuplicateMediaItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new DuplicateMediaItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                JsonSerializer.Deserialize<ChapterImageReference[]>(reader.GetString(10)) ?? [],
                DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return items;
    }

    private static string ReadGroupStatus(SqliteConnection connection, string productCode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM GroupDecisions WHERE ProductCode = $productCode;";
        command.Parameters.AddWithValue("$productCode", productCode);
        return command.ExecuteScalar() as string ?? "New";
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared;Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }
}
