namespace VideoIndex.Core.Models;

public class ScanRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Path { get; set; } = null!;
    public DateTimeOffset? LastScannedAt { get; set; }
}
