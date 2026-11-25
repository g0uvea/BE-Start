namespace BEStart.Models;

public sealed class ProjectInfo
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public bool HasIndexPhp { get; init; }

    public string Subtitle => HasIndexPhp
        ? "index.php encontrado"
        : "Pasta de projeto";
}
