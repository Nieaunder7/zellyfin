using Jellyfin.Plugin.SeekStatistics.Models;
using MediaBrowser.Controller.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeekStatistics.Data;

public sealed class SeekRepository
{
    private readonly string _databasePath;
    private readonly ILogger<SeekRepository> _logger;
    private readonly object _gate = new();

    public SeekRepository(IServerConfigurationManager configurationManager, ILogger<SeekRepository> logger)
    {
        _databasePath = Path.Combine(configurationManager.ApplicationPaths.DataPath, "seek-statistics.db");
        _logger = logger;
    }

    public string DatabasePath => _databasePath;

    public void Initialize()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS SeekEvents (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OccurredUtc TEXT NOT NULL,
                    UserId TEXT NOT NULL,
                    UserName TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    ItemName TEXT NOT NULL,
                    ItemType TEXT NOT NULL,
                    PlaySessionId TEXT NOT NULL,
                    DeviceId TEXT NOT NULL,
                    DeviceName TEXT NOT NULL,
                    ClientName TEXT NOT NULL,
                    FromTicks INTEGER NOT NULL,
                    ToTicks INTEGER NOT NULL,
                    DeltaTicks INTEGER NOT NULL,
                    Direction TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_SeekEvents_ItemId ON SeekEvents(ItemId);
                CREATE INDEX IF NOT EXISTS IX_SeekEvents_OccurredUtc ON SeekEvents(OccurredUtc);
                CREATE INDEX IF NOT EXISTS IX_SeekEvents_UserId ON SeekEvents(UserId);
                """;
            command.ExecuteNonQuery();
            _logger.LogInformation("Seek Statistics database initialized at {DatabasePath}", _databasePath);
        }
    }

    public void Insert(SeekEvent seekEvent)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SeekEvents (
                    OccurredUtc, UserId, UserName, ItemId, ItemName, ItemType,
                    PlaySessionId, DeviceId, DeviceName, ClientName,
                    FromTicks, ToTicks, DeltaTicks, Direction)
                VALUES (
                    $occurredUtc, $userId, $userName, $itemId, $itemName, $itemType,
                    $playSessionId, $deviceId, $deviceName, $clientName,
                    $fromTicks, $toTicks, $deltaTicks, $direction);
                """;
            command.Parameters.AddWithValue("$occurredUtc", seekEvent.OccurredUtc.ToString("O"));
            command.Parameters.AddWithValue("$userId", seekEvent.UserId);
            command.Parameters.AddWithValue("$userName", seekEvent.UserName);
            command.Parameters.AddWithValue("$itemId", seekEvent.ItemId);
            command.Parameters.AddWithValue("$itemName", seekEvent.ItemName);
            command.Parameters.AddWithValue("$itemType", seekEvent.ItemType);
            command.Parameters.AddWithValue("$playSessionId", seekEvent.PlaySessionId);
            command.Parameters.AddWithValue("$deviceId", seekEvent.DeviceId);
            command.Parameters.AddWithValue("$deviceName", seekEvent.DeviceName);
            command.Parameters.AddWithValue("$clientName", seekEvent.ClientName);
            command.Parameters.AddWithValue("$fromTicks", seekEvent.FromTicks);
            command.Parameters.AddWithValue("$toTicks", seekEvent.ToTicks);
            command.Parameters.AddWithValue("$deltaTicks", seekEvent.DeltaTicks);
            command.Parameters.AddWithValue("$direction", seekEvent.Direction);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SeekSummary> GetSummary(int days, int limit)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ItemId, MAX(ItemName), MAX(ItemType), COUNT(*),
                       SUM(CASE WHEN Direction = 'Forward' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Direction = 'Backward' THEN 1 ELSE 0 END),
                       SUM(ABS(DeltaTicks)), MAX(OccurredUtc)
                FROM SeekEvents
                WHERE ($fromUtc = '' OR OccurredUtc >= $fromUtc)
                GROUP BY ItemId
                ORDER BY COUNT(*) DESC, MAX(OccurredUtc) DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$fromUtc", days > 0 ? DateTime.UtcNow.AddDays(-days).ToString("O") : string.Empty);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));

            var results = new List<SeekSummary>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SeekSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    TimeSpan.FromTicks(reader.GetInt64(6)).TotalSeconds,
                    DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            }

            return results;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared");
        connection.Open();
        return connection;
    }
}
