using Jellyfin.Plugin.DuplicateMedia.Data;
using Jellyfin.Plugin.DuplicateMedia.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DuplicateMedia.Services;

public sealed class PermanentMediaDeletionService
{
    private static readonly TimeSpan ModifiedTolerance = TimeSpan.FromSeconds(2);
    private readonly DuplicateMediaRepository _repository;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PermanentMediaDeletionService> _logger;
    private readonly object _deletionGate = new();

    public PermanentMediaDeletionService(
        DuplicateMediaRepository repository,
        ILibraryManager libraryManager,
        ILogger<PermanentMediaDeletionService> logger)
    {
        _repository = repository;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public PermanentDeleteResult Delete(string productCode, PermanentDeleteRequest request)
    {
        lock (_deletionGate)
        {
            var normalizedCode = productCode.Trim().ToUpperInvariant();
            var group = _repository.GetGroup(normalizedCode)
                ?? throw new ArgumentException("The duplicate group no longer exists. Run a new scan.");
            var selectedIds = DeletionSafetyValidator.ValidateSelection(
                normalizedCode,
                request.Confirmation,
                request.ItemIds,
                group.Items.Select(item => item.ItemId));

            var selected = group.Items.Where(item => selectedIds.Contains(item.ItemId)).ToArray();

            var roots = _libraryManager.GetVirtualFolders()
                .SelectMany(folder => folder.Locations ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var prepared = selected.Select(item => Prepare(normalizedCode, item, roots)).ToArray();
            var results = ExecutePrepared(prepared, roots);

            return new PermanentDeleteResult(
                normalizedCode,
                prepared.Length,
                results.Count(result => result.FileDeleted),
                results.Where(result => result.FileDeleted).Sum(result => result.SizeBytes),
                results);
        }
    }

    public BatchPermanentDeleteResult DeleteBatch(BatchPermanentDeleteRequest request)
    {
        lock (_deletionGate)
        {
            var selections = request.Groups?.Where(group => group is not null).ToArray() ?? [];
            if (selections.Length > 200)
            {
                throw new ArgumentException("No more than 200 groups can be deleted at once.");
            }

            DeletionSafetyValidator.ValidateBatchConfirmation(selections.Length, request.Confirmation);
            if (selections.Any(selection =>
                    string.IsNullOrWhiteSpace(selection.ProductCode)
                    || selection.KeepItemIds is null
                    || selection.KeepItemIds.Count == 0
                    || selection.KeepItemIds.Any(string.IsNullOrWhiteSpace)))
            {
                throw new ArgumentException("Every group must include a unique code and at least one kept item id.");
            }

            var normalizedCodes = selections.Select(selection => selection.ProductCode.Trim().ToUpperInvariant()).ToArray();
            if (normalizedCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedCodes.Length)
            {
                throw new ArgumentException("Each duplicate group may appear only once.");
            }

            var roots = _libraryManager.GetVirtualFolders()
                .SelectMany(folder => folder.Locations ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var keepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<(string ProductCode, DuplicateMediaItem Item)>();

            for (var index = 0; index < selections.Length; index++)
            {
                var selection = selections[index];
                var productCode = normalizedCodes[index];
                var group = _repository.GetGroup(productCode)
                    ?? throw new ArgumentException($"The duplicate group no longer exists: {productCode}");
                var groupKeepIds = selection.KeepItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (groupKeepIds.Count >= group.Items.Count)
                {
                    throw new ArgumentException($"Group {productCode} must leave at least one file as a deletion target.");
                }

                if (!groupKeepIds.IsSubsetOf(group.Items.Select(item => item.ItemId)))
                {
                    throw new ArgumentException($"A kept file does not belong to group {productCode}.");
                }

                keepIds.UnionWith(groupKeepIds);
                pending.AddRange(group.Items
                    .Where(item => !groupKeepIds.Contains(item.ItemId))
                    .Select(item => (productCode, item)));
            }

            var duplicateDeleteIds = pending
                .GroupBy(entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateDeleteIds.Length > 0 || pending.Any(entry => keepIds.Contains(entry.Item.ItemId)))
            {
                throw new ArgumentException("Some files occur in multiple selected groups with conflicting decisions. Review those groups manually.");
            }

            // Prepare every file before deleting any file so stale paths or metadata fail the whole batch.
            var prepared = pending.Select(entry => Prepare(entry.ProductCode, entry.Item, roots)).ToArray();
            var results = ExecutePrepared(prepared, roots);
            return new BatchPermanentDeleteResult(
                selections.Length,
                prepared.Length,
                results.Count(result => result.FileDeleted),
                results.Where(result => result.FileDeleted).Sum(result => result.SizeBytes),
                results);
        }
    }

    private IReadOnlyList<PermanentDeleteItemResult> ExecutePrepared(
        IReadOnlyList<PreparedDeletion> prepared,
        IReadOnlyList<string> roots)
    {
        var results = new List<PermanentDeleteItemResult>(prepared.Count);
        foreach (var entry in prepared)
        {
            var fileDeleted = false;
            var indexRemoved = false;
            var error = string.Empty;
            try
            {
                // Revalidate immediately before the irreversible operation.
                Validate(entry.ScannedItem, entry.Item.Path, entry.File, roots);
                File.Delete(entry.File.FullName);
                fileDeleted = true;

                try
                {
                    // The source file is already gone. This removes only Jellyfin's index/internal metadata,
                    // avoiding Video.GetDeletePaths(), which can delete an entire containing folder.
                    _libraryManager.DeleteItem(
                        entry.Item,
                        new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false },
                        true);
                    indexRemoved = true;
                }
                catch (Exception ex)
                {
                    error = "Media file deleted, but Jellyfin index cleanup failed: " + ex.Message;
                    _logger.LogError(ex, "Index cleanup failed after deleting {Path}", entry.File.FullName);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogError(ex, "Permanent deletion failed for {Path}", entry.File.FullName);
            }

            var result = new PermanentDeleteItemResult(
                entry.ScannedItem.ItemId,
                entry.File.FullName,
                entry.ScannedItem.SizeBytes.GetValueOrDefault(),
                fileDeleted,
                indexRemoved,
                error);
            _repository.RecordDeletion(entry.ProductCode, result);
            if (fileDeleted)
            {
                _repository.RemoveScannedItem(entry.ScannedItem.ItemId);
            }

            results.Add(result);
        }

        return results;
    }

    private PreparedDeletion Prepare(string productCode, DuplicateMediaItem scannedItem, IReadOnlyList<string> roots)
    {
        if (!Guid.TryParse(scannedItem.ItemId, out var itemId))
        {
            throw new ArgumentException($"Invalid Jellyfin item id: {scannedItem.ItemId}");
        }

        var item = _libraryManager.GetItemById(itemId) as Video
            ?? throw new ArgumentException($"The Jellyfin video no longer exists: {scannedItem.Name}");
        var file = new FileInfo(DeletionSafetyValidator.NormalizePath(item.Path));
        Validate(scannedItem, item.Path, file, roots);
        return new PreparedDeletion(productCode, scannedItem, item, file);
    }

    private static void Validate(DuplicateMediaItem scannedItem, string indexedPath, FileInfo file, IReadOnlyList<string> roots)
    {
        DeletionSafetyValidator.ValidateSnapshot(
            scannedItem.Path,
            indexedPath,
            scannedItem.SizeBytes,
            scannedItem.DateModifiedUtc,
            file,
            roots,
            ModifiedTolerance);
    }

    private sealed record PreparedDeletion(string ProductCode, DuplicateMediaItem ScannedItem, Video Item, FileInfo File);
}
