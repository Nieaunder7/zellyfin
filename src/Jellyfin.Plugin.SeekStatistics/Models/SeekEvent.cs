namespace Jellyfin.Plugin.SeekStatistics.Models;

public sealed record SeekEvent(
    DateTime OccurredUtc,
    string UserId,
    string UserName,
    string ItemId,
    string ItemName,
    string ItemType,
    string PlaySessionId,
    string DeviceId,
    string DeviceName,
    string ClientName,
    long FromTicks,
    long ToTicks,
    long DeltaTicks,
    string Direction);

public sealed record SeekSummary(
    string ItemId,
    string ItemName,
    string ItemType,
    long SeekCount,
    long ForwardCount,
    long BackwardCount,
    double TotalMovedSeconds,
    DateTime LastSeekUtc);
