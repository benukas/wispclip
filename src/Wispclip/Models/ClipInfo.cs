namespace Wispclip.Models;

public class ClipInfo
{
    public required string Path { get; init; }
    public required string Name { get; set; }
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
    public double? DurationSeconds { get; set; }
    public string? ThumbnailPath { get; set; }
}
