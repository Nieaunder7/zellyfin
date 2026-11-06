using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StoryPreview.Api;

[ApiController]
[Authorize]
[Route("StoryPreview")]
public sealed class StoryPreviewController : ControllerBase
{
    private static readonly SemaphoreSlim ExtractionSlots = new(2, 2);
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IApplicationPaths _applicationPaths;

    public StoryPreviewController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _applicationPaths = applicationPaths;
    }

    [HttpGet("Items/{itemId:guid}/{percent:int}.jpg")]
    public async Task<IActionResult> GetFrame(Guid itemId, int percent, CancellationToken cancellationToken)
    {
        if (percent is not (25 or 50 or 75))
        {
            return BadRequest();
        }

        var claim = User.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user)
        {
            return Unauthorized();
        }

        if (_libraryManager.GetItemById(itemId) is not Video video || !video.IsVisible(user)
            || string.IsNullOrWhiteSpace(video.Path) || video.RunTimeTicks is not > 0)
        {
            return NotFound();
        }

        FileInfo media;
        try
        {
            media = new FileInfo(video.Path);
            if (!media.Exists || media.Length == 0)
            {
                return NotFound();
            }
        }
        catch (IOException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }

        var cacheDirectory = Path.Combine(_applicationPaths.DataPath, "story-preview-cache");
        var cacheFile = Path.Combine(cacheDirectory,
            $"{itemId:N}-{media.Length}-{media.LastWriteTimeUtc.Ticks}-{percent}.jpg");
        if (System.IO.File.Exists(cacheFile))
        {
            Response.Headers.CacheControl = "private, max-age=86400";
            return PhysicalFile(cacheFile, "image/jpeg");
        }

        await ExtractionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!System.IO.File.Exists(cacheFile))
            {
                Directory.CreateDirectory(cacheDirectory);
                var ffmpeg = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty,
                    OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (!System.IO.File.Exists(ffmpeg))
                {
                    return StatusCode(503, "FFmpeg is unavailable.");
                }

                var tempFile = Path.Combine(cacheDirectory, $"{Guid.NewGuid():N}.jpg");
                using var process = new Process();
                try
                {
                    process.StartInfo = new ProcessStartInfo(ffmpeg)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true
                    };
                    var seconds = video.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond * percent / 100;
                    foreach (var argument in new[]
                    {
                        "-hide_banner", "-loglevel", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", media.FullName, "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=320:-2",
                        "-q:v", "6", "-y", tempFile
                    })
                    {
                        process.StartInfo.ArgumentList.Add(argument);
                    }

                    process.Start();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    if (process.ExitCode != 0 || !System.IO.File.Exists(tempFile)
                        || new FileInfo(tempFile).Length == 0)
                    {
                        return StatusCode(422, "The preview frame could not be extracted.");
                    }

                    System.IO.File.Move(tempFile, cacheFile, true);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!process.HasExited) process.Kill(true);
                    return StatusCode(504, "Preview extraction timed out.");
                }
                finally
                {
                    if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
                }
            }
        }
        finally
        {
            ExtractionSlots.Release();
        }

        Response.Headers.CacheControl = "private, max-age=86400";
        return PhysicalFile(cacheFile, "image/jpeg");
    }
}
