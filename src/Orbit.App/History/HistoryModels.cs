using System;
using System.Collections.Generic;
using Orbit.Core;
using Orbit.Core.Downloads;

namespace Orbit.App.History;

public enum HistoryStatus { Queued, Running, Succeeded, Failed, Cancelled, Interrupted }

/// <summary>Passive metadata only. Loading this DTO never schedules an operation or opens its paths.</summary>
public sealed record HistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; init; }
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
    public ImageJobOptions Options { get; init; } = new();
    public DownloadRequest? Download { get; init; }
    public ImageJobResult? Result { get; init; }
    public string? ErrorCaption { get; init; }
    public HistoryStatus Status { get; init; } = HistoryStatus.Queued;
}

public sealed record HistoryLoadResult(IReadOnlyList<HistoryEntry> Entries, string? Error);
