using Jellyfin.Plugin.SeekStatistics.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SeekStatistics.Api;

[ApiController]
[Authorize]
[Route("SceneAnalysis")]
public sealed class SceneAnalysisController : ControllerBase
{
    private readonly SceneAnalysisIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly PerformerFilmographyIndex _filmography;
    private readonly SynopsisIndex _synopsis;

    public SceneAnalysisController(
        SceneAnalysisIndex index,
        ILibraryManager libraryManager,
        IUserManager userManager,
        PerformerFilmographyIndex filmography,
        SynopsisIndex synopsis)
    {
        _index = index;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _filmography = filmography;
        _synopsis = synopsis;
    }

    [HttpGet("Items/{itemId:guid}")]
    public IActionResult GetItem(Guid itemId)
    {
        if (!TryGetVisibleVideo(itemId, out var video)) return NotFound();
        var record = _index.Get(itemId);
        if (record is null) return Ok(new { status = "unprocessed", completedSamples = 0, expectedSamples = 0, scenes = Array.Empty<object>() });
        var status = SceneAnalysisIndex.GetEffectiveStatus(video!, record);
        return Ok(new
        {
            status,
            completedSamples = record.CompletedSamples,
            expectedSamples = record.ExpectedSamples,
            error = record.Error,
            scenes = (status == "stale" ? Enumerable.Empty<SceneAnalysisSample>() : record.Scenes)
                .OrderBy(scene => scene.TimeTicks).Select(scene => new
            {
                timeTicks = scene.TimeTicks,
                description = string.IsNullOrWhiteSpace(scene.DescriptionKo) ? scene.Description : scene.DescriptionKo,
                descriptionEn = scene.Description,
                tags = scene.Tags,
                imageUrl = $"SceneAnalysis/Items/{itemId:D}/Frames/{scene.FrameIndex}.jpg"
            })
        });
    }

    [HttpGet("Items/{itemId:guid}/Synopsis")]
    public IActionResult GetSynopsis(Guid itemId)
    {
        if (!TryGetVisibleVideo(itemId, out _)) return NotFound();
        var record = _synopsis.Get(itemId);
        if (record is null) return NotFound();
        return Ok(new
        {
            code = record.Code,
            title = record.Title,
            synopsis = record.DisplayText,
            originalJapanese = record.Japanese,
            sourceUrl = record.SourceUrl,
            reviewStatus = record.ReviewStatus,
            keywords = record.Keywords
        });
    }

    [HttpGet("Keywords")]
    public IActionResult GetKeywords([FromQuery] Guid? parentId, [FromQuery] int limit = 40)
    {
        var claim = User.Claims.FirstOrDefault(value => value.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user) return Unauthorized();
        var parent = _libraryManager.GetParentItem(parentId, userId);
        if (parent is not UserRootFolder && !parent.IsVisible(user)) return Unauthorized();
        var visibleIds = (parent as Folder ?? _libraryManager.GetUserRootFolder())
            .GetItems(new InternalItemsQuery(user)
            {
                ParentId = parentId ?? Guid.Empty,
                Recursive = true,
                GroupByPresentationUniqueKey = false
            }).Items.OfType<Video>().Where(video => video.IsVisible(user)).Select(video => video.Id).ToHashSet();
        var words = _synopsis.GetRecords()
            .Where(entry => visibleIds.Contains(entry.Key))
            .Select(entry => entry.Value)
            .DistinctBy(record => record.Code)
            .SelectMany(record => record.Keywords)
            .GroupBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { keyword = group.Key, count = group.Count() })
            .OrderByDescending(entry => entry.count).ThenBy(entry => entry.keyword, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 100)).ToArray();
        return Ok(words);
    }

    [HttpGet("Items/{itemId:guid}/Frames/{frameIndex:int}.jpg")]
    public IActionResult GetFrame(Guid itemId, int frameIndex)
    {
        if (frameIndex < 0 || !TryGetVisibleVideo(itemId, out var video)) return NotFound();
        var record = _index.Get(itemId);
        if (record is null || SceneAnalysisIndex.GetEffectiveStatus(video!, record) == "stale"
            || !record.Scenes.Any(scene => scene.FrameIndex == frameIndex)) return NotFound();
        var path = Path.Combine(_index.Root, "frames", itemId.ToString("N"), $"{frameIndex}.jpg");
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, "image/jpeg");
    }

    [HttpGet("Search")]
    public IActionResult Search([FromQuery] string query, [FromQuery] int limit = 50)
    {
        var claim = User.Claims.FirstOrDefault(value => value.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user) return Unauthorized();
        var terms = System.Text.RegularExpressions.Regex.Matches(query ?? "", @"[\p{L}\p{N}]+")
            .Select(match => match.Value switch
            {
                "여성둘" or "여자둘" or "여성두명" or "여자두명" => "여성2명",
                "자고" or "자는" or "잠자는" => "잠든",
                "있음" or "있는" => null,
                _ => match.Value
            })
            .OfType<string>()
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (terms.Length == 0) return Ok(Array.Empty<object>());
        var database = Path.Combine(_index.Root, "scene-search.db");
        if (!System.IO.File.Exists(database)) return Ok(Array.Empty<object>());
        var results = new List<object>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemId, TimeTicks, Description, Tags, FrameIndex FROM SceneSearch WHERE SceneSearch MATCH $query ORDER BY rank LIMIT $limit";
        command.Parameters.AddWithValue("$query", string.Join(" AND ", terms.Select(term => $"\"{term}\"")));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200) * 5);
        using var reader = command.ExecuteReader();
        while (reader.Read() && results.Count < Math.Clamp(limit, 1, 200))
        {
            if (!Guid.TryParse(reader.GetString(0), out var itemId)) continue;
            if (_libraryManager.GetItemById(itemId) is not Video video || !video.IsVisible(user)) continue;
            var record = _index.Get(itemId);
            if (record is null || SceneAnalysisIndex.GetEffectiveStatus(video, record) != "complete") continue;
            var frameIndex = reader.GetInt32(4);
            var scene = record.Scenes.FirstOrDefault(value => value.FrameIndex == frameIndex);
            results.Add(new
            {
                itemId,
                itemName = video.Name,
                timeTicks = reader.GetInt64(1),
                durationTicks = video.RunTimeTicks ?? 0,
                sampleNumber = frameIndex + 1,
                sampleCount = record.ExpectedSamples,
                description = string.IsNullOrWhiteSpace(scene?.DescriptionKo) ? scene?.Description ?? reader.GetString(2) : scene.DescriptionKo,
                tags = reader.GetString(3).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                matchedTerms = terms,
                imageUrl = $"SceneAnalysis/Items/{itemId:D}/Frames/{frameIndex}.jpg"
            });
        }
        return Ok(results);
    }

    [HttpGet("UnifiedSearch")]
    public IActionResult UnifiedSearch(
        [FromQuery] string query,
        [FromQuery] Guid? parentId,
        [FromQuery] string mode = "all",
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50)
    {
        var claim = User.Claims.FirstOrDefault(value => value.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user) return Unauthorized();
        var parent = _libraryManager.GetParentItem(parentId, userId);
        var folder = parent as Folder ?? _libraryManager.GetUserRootFolder();
        if (parent is not UserRootFolder && !parent.IsVisible(user)) return Unauthorized();

        mode = mode is "metadata" or "scene" or "performer" or "filename" ? mode : "all";
        var searchScenes = mode is "all" or "scene";
        var searchMetadata = mode is "all" or "metadata";
        var searchNames = mode is "all" or "filename";
        var searchPerformers = mode is "all" or "performer";
        var normalizedQuery = searchScenes || searchMetadata
            ? Regex.Replace(query ?? "", @"여(?:성|자)\s*(?:둘|두\s*명)", "여성2명")
            : query ?? "";
        var terms = Regex.Matches(normalizedQuery, @"[\p{L}\p{N}]+")
            .Select(match => searchScenes || searchMetadata ? match.Value switch
            {
                "자고" or "자는" or "잠자는" => "잠든",
                "있음" or "있는" => null,
                _ => match.Value
            } : match.Value)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (terms.Length == 0) return Ok(new { totalRecordCount = 0, items = Array.Empty<object>() });
        var ruruMask = 0;
        for (var index = 0; index < terms.Length; index++)
        {
            if (searchPerformers && PerformerFilmographyIndex.IsRuruTerm(terms[index])) ruruMask |= 1 << index;
        }
        if (mode == "performer" && ruruMask != (1 << terms.Length) - 1)
            return Ok(new { totalRecordCount = 0, items = Array.Empty<object>() });

        var sceneMatches = new Dictionary<Guid, Dictionary<int, (int Mask, long TimeTicks)>>();
        var database = Path.Combine(_index.Root, "scene-search.db");
        if (searchScenes && System.IO.File.Exists(database))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ItemId, FrameIndex, TimeTicks FROM SceneSearch WHERE SceneSearch MATCH $term";
            var parameter = command.Parameters.Add("$term", SqliteType.Text);
            for (var index = 0; index < terms.Length; index++)
            {
                // A performer name must match the saved catalog, not a guessed scene caption.
                if ((ruruMask & (1 << index)) != 0) continue;
                parameter.Value = $"\"{terms[index]}\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!Guid.TryParse(reader.GetString(0), out var itemId)) continue;
                    var frameIndex = reader.GetInt32(1);
                    if (!sceneMatches.TryGetValue(itemId, out var frames))
                    {
                        frames = [];
                        sceneMatches[itemId] = frames;
                    }
                    var previous = frames.GetValueOrDefault(frameIndex);
                    frames[frameIndex] = (previous.Mask | (1 << index), reader.GetInt64(2));
                }
            }
        }

        var allTerms = (1 << terms.Length) - 1;
        var candidates = folder.GetItems(new InternalItemsQuery(user)
        {
            ParentId = parentId ?? Guid.Empty,
            Recursive = true,
            GroupByPresentationUniqueKey = false
        }).Items.OfType<Video>().Where(video => video.IsVisible(user));
        var synopsisRecords = searchMetadata ? _synopsis.GetRecords() : null;
        var matches = new List<(Video Video, bool NameExact, bool NameMatch, bool MetadataMatch, bool SynopsisMatch,
            SynopsisRecord? Synopsis, PerformerCode? Performer, List<SceneAnalysisSample> Scenes)>();
        var ownedRuruVideos = 0;
        foreach (var video in candidates)
        {
            var name = video.Name ?? "";
            var fileName = Path.GetFileNameWithoutExtension(video.Path ?? "");
            var compactName = Regex.Replace(name + fileName, @"[^\p{L}\p{N}]", "");
            var fileMask = 0;
            if (searchNames) for (var index = 0; index < terms.Length; index++)
            {
                if ((ruruMask & (1 << index)) != 0) continue;
                if (name.Contains(terms[index], StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains(terms[index], StringComparison.OrdinalIgnoreCase)
                    || compactName.Contains(terms[index], StringComparison.OrdinalIgnoreCase))
                    fileMask |= 1 << index;
            }
            var metadataMask = 0;
            var synopsisMask = 0;
            SynopsisRecord? synopsis = null;
            if (synopsisRecords is not null) synopsisRecords.TryGetValue(video.Id, out synopsis);
            if (searchMetadata)
            {
                for (var index = 0; index < terms.Length; index++)
                {
                    if ((ruruMask & (1 << index)) != 0) continue;
                    if (synopsis is not null && (
                            synopsis.DisplayText.Contains(terms[index], StringComparison.OrdinalIgnoreCase)
                            || synopsis.Title.Contains(terms[index], StringComparison.OrdinalIgnoreCase)
                            || synopsis.Code.Contains(terms[index], StringComparison.OrdinalIgnoreCase)
                            || synopsis.Japanese.Contains(terms[index], StringComparison.OrdinalIgnoreCase)
                            || synopsis.Keywords.Any(keyword => keyword.Contains(terms[index], StringComparison.OrdinalIgnoreCase))))
                        synopsisMask |= 1 << index;
                    if ((!string.IsNullOrWhiteSpace(video.Overview) && video.Overview.Contains(terms[index], StringComparison.OrdinalIgnoreCase))
                        || (synopsisMask & (1 << index)) != 0)
                        metadataMask |= 1 << index;
                }
            }
            var performer = ruruMask == 0 ? null : _filmography.MatchRuru(name, video.Path);
            if (performer is not null) ownedRuruVideos++;
            var knownMask = fileMask | metadataMask | (performer is null ? 0 : ruruMask);

            var matchedScenes = new List<SceneAnalysisSample>();
            if (sceneMatches.TryGetValue(video.Id, out var frames))
            {
                var record = _index.Get(video.Id);
                if (record is not null && SceneAnalysisIndex.GetEffectiveStatus(video, record) == "complete")
                {
                    matchedScenes = record.Scenes
                        .Where(scene => frames.TryGetValue(scene.FrameIndex, out var frame)
                            && (knownMask | frame.Mask) == allTerms)
                        .OrderBy(scene => scene.TimeTicks)
                        .Take(3)
                        .ToList();
                }
            }
            if (knownMask != allTerms && matchedScenes.Count == 0) continue;
            var compactQuery = Regex.Replace(normalizedQuery, @"[^\p{L}\p{N}]", "");
            matches.Add((video,
                searchNames && compactQuery.Length > 0 && compactName.Contains(compactQuery, StringComparison.OrdinalIgnoreCase),
                fileMask != 0,
                metadataMask != 0,
                synopsisMask != 0,
                synopsis,
                performer,
                matchedScenes));
        }

        startIndex = Math.Max(0, startIndex);
        limit = Math.Clamp(limit, 1, 200);
        var page = matches
            .OrderByDescending(match => match.NameExact)
            .ThenByDescending(match => match.Synopsis is not null && match.MetadataMatch)
            .ThenByDescending(match => match.Scenes.Count > 0)
            .ThenByDescending(match => match.NameMatch)
            .ThenBy(match => match.Video.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Video.Id)
            .Skip(startIndex).Take(limit)
            .Select(match => new
            {
                itemId = match.Video.Id,
                itemName = match.Video.Name,
                durationTicks = match.Video.RunTimeTicks ?? 0,
                matchedInName = match.NameMatch,
                matchedInMetadata = match.MetadataMatch,
                synopsis = match.Synopsis is null || !match.SynopsisMatch ? null : Excerpt(match.Synopsis.DisplayText, terms),
                synopsisCode = match.Synopsis?.Code,
                synopsisKeywords = match.Synopsis?.Keywords ?? [],
                synopsisReviewStatus = match.Synopsis?.ReviewStatus,
                matchedPerformerCode = match.Performer?.Code,
                performerSource = match.Performer?.Source,
                scenes = match.Scenes.Select(scene => new
                {
                    timeTicks = scene.TimeTicks,
                    description = string.IsNullOrWhiteSpace(scene.DescriptionKo) ? scene.Description : scene.DescriptionKo,
                    tags = scene.Tags,
                    imageUrl = $"SceneAnalysis/Items/{match.Video.Id:D}/Frames/{scene.FrameIndex}.jpg"
                })
            }).ToArray();
        object? performerSummary = ruruMask == 0 ? null : new
        {
            name = "아이자와 루루",
            catalogCodeCount = _filmography.RuruCodeCount,
            ownedVideoCount = ownedRuruVideos
        };
        return Ok(new { totalRecordCount = matches.Count, items = page, performerSummary });
    }

    private static string Excerpt(string text, IReadOnlyList<string> terms)
    {
        if (text.Length <= 300) return text;
        var first = terms.Select(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, first - 80);
        var length = Math.Min(300, text.Length - start);
        return (start > 0 ? "…" : "") + text.Substring(start, length)
            + (start + length < text.Length ? "…" : "");
    }

    private bool TryGetVisibleVideo(Guid itemId, out Video? video)
    {
        video = null;
        var claim = User.Claims.FirstOrDefault(value => value.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user) return false;
        video = _libraryManager.GetItemById(itemId) as Video;
        return video is not null && video.IsVisible(user);
    }
}
