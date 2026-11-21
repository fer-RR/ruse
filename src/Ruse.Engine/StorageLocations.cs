namespace Ruse.App;

/// <summary>Optional user-selected storage folders, stored outside the movable data folder.</summary>
public sealed class StorageLocations
{
    public string? DataDirectory { get; set; }
    public string? ModelsDirectory { get; set; }
    public string? UserDirectory { get; set; }
}
