using System.Net.Mime;
using System.Text;
using Jellyfin.Plugin.DuplicateMedia.Data;
using Jellyfin.Plugin.DuplicateMedia.Models;
using Jellyfin.Plugin.DuplicateMedia.Tasks;
using Jellyfin.Plugin.DuplicateMedia.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DuplicateMedia.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("DuplicateMedia")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class DuplicateMediaController : ControllerBase
{
    private readonly DuplicateMediaRepository _repository;
    private readonly ITaskManager _taskManager;
    private readonly PermanentMediaDeletionService _deletionService;

    public DuplicateMediaController(
        DuplicateMediaRepository repository,
        ITaskManager taskManager,
        PermanentMediaDeletionService deletionService)
    {
        _repository = repository;
        _taskManager = taskManager;
        _deletionService = deletionService;
    }

    [HttpGet("Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DuplicateMediaSummary> GetSummary()
    {
        return Ok(_repository.GetSummary());
    }

    [HttpGet("Groups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<DuplicateGroupPage> GetGroups(
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? classification = null)
    {
        return Ok(_repository.GetGroups(
            Math.Max(0, startIndex),
            Math.Clamp(limit, 1, 200),
            search,
            status,
            classification));
    }

    [HttpPost("Scan")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult StartScan()
    {
        _taskManager.QueueIfNotRunning<DuplicateMediaScanTask>();
        return Accepted(new { queued = true });
    }

    [HttpPut("Groups/{productCode}/Status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SetGroupStatus([FromRoute] string productCode, [FromBody] UpdateGroupStatusRequest request)
    {
        try
        {
            _repository.SetGroupStatus(productCode, request.Status);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("Groups/{productCode}/Delete")]
    [ProducesResponseType(typeof(PermanentDeleteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PermanentDeleteResult> PermanentlyDelete(
        [FromRoute] string productCode,
        [FromBody] PermanentDeleteRequest request)
    {
        try
        {
            return Ok(_deletionService.Delete(productCode, request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("DeleteBatch")]
    [ProducesResponseType(typeof(BatchPermanentDeleteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<BatchPermanentDeleteResult> PermanentlyDeleteBatch(
        [FromBody] BatchPermanentDeleteRequest request)
    {
        try
        {
            return Ok(_deletionService.DeleteBatch(request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("Export")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ExportCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("ProductCode,Classification,Status,Name,Path,SizeBytes,RuntimeSeconds,Resolution,Container,CreatedUtc,ModifiedUtc,ChapterImageCount,ItemId");
        foreach (var group in _repository.GetAllGroups())
        {
            foreach (var item in group.Items)
            {
                builder.Append(Csv(group.ProductCode)).Append(',')
                    .Append(Csv(group.Classification)).Append(',')
                    .Append(Csv(group.Status)).Append(',')
                    .Append(Csv(item.Name)).Append(',')
                    .Append(Csv(item.Path)).Append(',')
                    .Append(item.SizeBytes.GetValueOrDefault()).Append(',')
                    .Append(item.RuntimeTicks.HasValue ? TimeSpan.FromTicks(item.RuntimeTicks.Value).TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : string.Empty).Append(',')
                    .Append(Csv(item.Width > 0 && item.Height > 0 ? $"{item.Width}x{item.Height}" : string.Empty)).Append(',')
                    .Append(Csv(item.Container)).Append(',')
                    .Append(Csv(item.DateCreatedUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)).Append(',')
                    .Append(Csv(item.DateModifiedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture))).Append(',')
                    .Append(item.ChapterImages.Count).Append(',')
                    .Append(Csv(item.ItemId)).AppendLine();
            }
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var content = Encoding.UTF8.GetBytes(builder.ToString());
        var bytes = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, bytes, preamble.Length, content.Length);
        return File(bytes, "text/csv; charset=utf-8", $"duplicate-media-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static string Csv(string value)
    {
        var safeValue = value;
        if (safeValue.Length > 0 && "=+-@".Contains(safeValue[0], StringComparison.Ordinal))
        {
            safeValue = "'" + safeValue;
        }

        return '"' + safeValue.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
