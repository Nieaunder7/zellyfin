using MediaBrowser.Controller.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.SeekStatistics.Data;

/// <summary>Read-only snapshot of separately harvested and reviewed synopses.</summary>
public sealed class SynopsisIndex
{
    private readonly string _databasePath;
    private readonly object _gate = new();
    private DateTime _loadedUtc = DateTime.MinValue;
    private IReadOnlyDictionary<Guid, SynopsisRecord> _records =
        new Dictionary<Guid, SynopsisRecord>();

    public SynopsisIndex(IServerConfigurationManager configurationManager)
    {
        _databasePath = Path.Combine(configurationManager.ApplicationPaths.DataPath, "synopsis-index.db");
    }

    public SynopsisRecord? Get(Guid itemId) => GetRecords().GetValueOrDefault(itemId);

    public IReadOnlyDictionary<Guid, SynopsisRecord> GetRecords()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _loadedUtc < TimeSpan.FromSeconds(30)) return _records;
            try
            {
                _records = ReadRecords();
                _loadedUtc = DateTime.UtcNow;
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                _loadedUtc = DateTime.UtcNow;
            }
            return _records;
        }
    }

    private IReadOnlyDictionary<Guid, SynopsisRecord> ReadRecords()
    {
        var output = new Dictionary<Guid, SynopsisRecord>();
        if (!File.Exists(_databasePath)) return output;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var keywordMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Code,Keyword FROM SynopsisKeyword";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var code = reader.GetString(0);
                if (!keywordMap.TryGetValue(code, out var list))
                {
                    list = [];
                    keywordMap[code] = list;
                }
                list.Add(reader.GetString(1));
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.ItemId,w.Code,w.Title,w.Synopsis,w.SourceUrl,
                       t.SynopsisKo,r.RefinedKo,r.ReviewStatus
                FROM ItemWork i JOIN Work w ON w.Code=i.Code
                JOIN SynopsisTranslation t ON t.Code=w.Code AND t.Status='complete'
                LEFT JOIN SynopsisReview r ON r.Code=w.Code
                WHERE w.Status='success'
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var itemId)) continue;
                var code = reader.GetString(1);
                output[itemId] = new SynopsisRecord(
                    code,
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? "unreviewed" : reader.GetString(7),
                    keywordMap.TryGetValue(code, out var terms) ? terms.ToArray() : []);
            }
        }
        return output;
    }
}

public sealed record SynopsisRecord(
    string Code,
    string Title,
    string Japanese,
    string SourceUrl,
    string Translation,
    string Refined,
    string ReviewStatus,
    string[] Keywords)
{
    public string DisplayText => !string.IsNullOrWhiteSpace(Refined) ? Refined : Translation;
}
