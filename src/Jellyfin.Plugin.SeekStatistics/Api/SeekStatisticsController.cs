using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SeekStatistics.Data;
using Jellyfin.Plugin.SeekStatistics.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SeekStatistics.Api;

[ApiController]
[Authorize]
[Route("SeekStatistics")]
public sealed class SeekStatisticsController : ControllerBase
{
    private readonly SeekRepository _repository;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;

    public SeekStatisticsController(
        SeekRepository repository,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDtoService dtoService)
    {
        _repository = repository;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
    }

    [HttpGet("Summary")]
    public ActionResult<IReadOnlyList<SeekSummary>> GetSummary([FromQuery] int days = 0, [FromQuery] int limit = 10000)
    {
        return Ok(_repository.GetSummary(Math.Max(0, days), limit));
    }

    [HttpGet("Items")]
    public ActionResult<QueryResult<BaseItemDto>> GetItems(
        [FromQuery] Guid? parentId,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 100,
        [FromQuery] bool recursive = false,
        [FromQuery] string? includeItemTypes = null,
        [FromQuery] string? filters = null,
        [FromQuery] bool? isFavorite = null,
        [FromQuery] bool? isPlayed = null,
        [FromQuery] bool? is4K = null,
        [FromQuery] bool? isHD = null,
        [FromQuery] bool? is3D = null,
        [FromQuery] bool? hasSubtitles = null,
        [FromQuery] bool? hasTrailer = null,
        [FromQuery] bool? hasSpecialFeature = null,
        [FromQuery] bool? hasThemeSong = null,
        [FromQuery] bool? hasThemeVideo = null,
        [FromQuery] string? nameStartsWith = null,
        [FromQuery] string? nameLessThan = null,
        [FromQuery] string? videoTypes = null,
        [FromQuery] string? genreIds = null,
        [FromQuery] string sortOrder = "Descending")
    {
        var userClaim = User.Claims.FirstOrDefault(claim =>
            claim.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(userClaim, out var userId))
        {
            return Unauthorized();
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var parent = _libraryManager.GetParentItem(parentId, userId);
        var folder = parent as Folder ?? _libraryManager.GetUserRootFolder();
        if (parent is not UserRootFolder && !parent.IsVisible(user))
        {
            return Unauthorized();
        }

        startIndex = Math.Max(0, startIndex);
        limit = Math.Clamp(limit, 1, 500);
        var descending = !sortOrder.Equals("Ascending", StringComparison.OrdinalIgnoreCase);
        var dtoOptions = new DtoOptions(false)
        {
            Fields = new[]
            {
                ItemFields.PrimaryImageAspectRatio,
                ItemFields.SortName,
                ItemFields.Path,
                ItemFields.ChildCount,
                ItemFields.MediaSourceCount
            },
            ImageTypeLimit = 1,
            ImageTypes = new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Thumb },
            EnableImages = true,
            EnableUserData = true
        };

        var requestedItemTypes = ParseEnumList<BaseItemKind>(includeItemTypes);

        InternalItemsQuery CreateQuery() => new(user)
        {
            ParentId = parentId ?? Guid.Empty,
            Recursive = recursive,
            IncludeItemTypes = requestedItemTypes,
            IsFavorite = isFavorite,
            IsPlayed = isPlayed,
            Is4K = is4K,
            IsHD = isHD,
            Is3D = is3D,
            HasSubtitles = hasSubtitles,
            HasTrailer = hasTrailer,
            HasSpecialFeature = hasSpecialFeature,
            HasThemeSong = hasThemeSong,
            HasThemeVideo = hasThemeVideo,
            NameStartsWith = nameStartsWith,
            NameLessThan = nameLessThan,
            VideoTypes = ParseEnumList<VideoType>(videoTypes),
            GenreIds = ParseGuidList(genreIds),
            DtoOptions = dtoOptions,
            EnableTotalRecordCount = true,
            GroupByPresentationUniqueKey = false
        };

        var filterValues = ParseEnumList<ItemFilter>(filters);
        var summaries = _repository.GetSummary(0, 100000);
        var counts = summaries
            .Select(summary => (Summary: summary, Parsed: Guid.TryParse(summary.ItemId, out var id), Id: id))
            .Where(entry => entry.Parsed)
            .ToDictionary(entry => entry.Id, entry => entry.Summary.SeekCount);
        var soughtIds = counts.Keys.ToArray();

        var soughtItems = soughtIds
            .Select(_libraryManager.GetItemById)
            .Where(item => item is not null
                && item.IsVisible(user)
                && (!parentId.HasValue || item.GetAncestorIds().Contains(parent.Id))
                && (requestedItemTypes.Length == 0 || requestedItemTypes.Contains(item.GetBaseItemKind())))
            .Select(item => item!)
            .OrderBy(item => item, Comparer<BaseItem>.Create((left, right) =>
                {
                    var countComparison = counts.GetValueOrDefault(left.Id).CompareTo(counts.GetValueOrDefault(right.Id));
                    if (countComparison != 0)
                    {
                        return descending ? -countComparison : countComparison;
                    }

                    return string.Compare(left.SortName, right.SortName, StringComparison.CurrentCultureIgnoreCase);
                }))
            .ToArray();

        var zeroQuery = CreateQuery();
        zeroQuery.ExcludeItemIds = soughtIds;
        zeroQuery.OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) };
        ApplyFilters(zeroQuery, filterValues);

        var page = new List<BaseItem>(limit);
        QueryResult<BaseItem> zeroResult;

        if (descending)
        {
            var soughtStart = Math.Min(startIndex, soughtItems.Length);
            page.AddRange(soughtItems.Skip(soughtStart).Take(limit));
            var zeroStart = Math.Max(0, startIndex - soughtItems.Length);
            zeroQuery.StartIndex = zeroStart;
            zeroQuery.Limit = Math.Max(1, limit - page.Count);
            zeroResult = folder.GetItems(zeroQuery);
            if (page.Count < limit)
            {
                page.AddRange(zeroResult.Items.Take(limit - page.Count));
            }
        }
        else
        {
            zeroQuery.StartIndex = startIndex;
            zeroQuery.Limit = limit;
            zeroResult = folder.GetItems(zeroQuery);
            page.AddRange(zeroResult.Items);
            if (page.Count < limit)
            {
                var soughtStart = Math.Max(0, startIndex - zeroResult.TotalRecordCount);
                page.AddRange(soughtItems.Skip(soughtStart).Take(limit - page.Count));
            }
        }

        return new QueryResult<BaseItemDto>(
            startIndex,
            zeroResult.TotalRecordCount + soughtItems.Length,
            _dtoService.GetBaseItemDtos(page, dtoOptions, user));
    }

    private static T[] ParseEnumList<T>(string? value)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<T>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => Enum.TryParse<T>(entry, true, out var parsed) ? parsed : (T?)null)
                .Where(entry => entry.HasValue)
                .Select(entry => entry!.Value)
                .ToArray();

    private static Guid[] ParseGuidList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<Guid>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => Guid.TryParse(entry, out var parsed) ? parsed : Guid.Empty)
                .Where(entry => entry != Guid.Empty)
                .ToArray();

    private static void ApplyFilters(InternalItemsQuery query, IEnumerable<ItemFilter> filters)
    {
        foreach (var filter in filters)
        {
            switch (filter)
            {
                case ItemFilter.IsFavorite:
                    query.IsFavorite = true;
                    break;
                case ItemFilter.IsPlayed:
                    query.IsPlayed = true;
                    break;
                case ItemFilter.IsUnplayed:
                    query.IsPlayed = false;
                    break;
                case ItemFilter.IsResumable:
                    query.IsResumable = true;
                    break;
            }
        }
    }
}
