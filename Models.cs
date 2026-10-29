using System;
using System.Collections.Generic;

namespace Nimbo;

public sealed class AppSettings
{
    public bool RunAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool ThresholdEnabled { get; set; }
    public int ThresholdPercent { get; set; } = 85;
    public bool IntervalEnabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public bool ConfirmBeforeManualClean { get; set; } = true;
    public bool CleanWorkingSets { get; set; } = true;
    public bool CleanSystemFileCache { get; set; }
    public bool CleanUserTempFiles { get; set; }
}

public sealed record MemorySnapshot(
    DateTimeOffset CapturedAt,
    ulong PhysicalTotal,
    ulong PhysicalAvailable,
    ulong PagefileTotal,
    ulong PagefileAvailable,
    ulong SystemCache)
{
    public double PhysicalUsagePercent => PhysicalTotal == 0 ? 0 : 100d * (PhysicalTotal - PhysicalAvailable) / PhysicalTotal;
    public double PagefileUsagePercent => PagefileTotal == 0 ? 0 : 100d * (PagefileTotal - PagefileAvailable) / PagefileTotal;
    public ulong PhysicalUsed => PhysicalTotal > PhysicalAvailable ? PhysicalTotal - PhysicalAvailable : 0;
}

public sealed class CleanupRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string Status { get; set; } = "completed";
    public int ProcessesTrimmed { get; set; }
    public int ProcessesSkipped { get; set; }
    public bool SystemFileCacheTrimmed { get; set; }
    public int TempFilesDeleted { get; set; }
    public int TempFilesSkipped { get; set; }
    public ulong TempBytesDeleted { get; set; }
    public ulong AvailableBefore { get; set; }
    public ulong AvailableAfter { get; set; }
    public List<string> SelectedAreas { get; set; } = [];
    public string Note { get; set; } = "";
}
