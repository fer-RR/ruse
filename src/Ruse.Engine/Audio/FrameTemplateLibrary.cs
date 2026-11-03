using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruse.App.Audio;

/// <summary>A saved frame and the nodes that were inside it.</summary>
public sealed class FrameTemplate
{
    public string Name { get; set; } = "";
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 240;
    public Preset Content { get; set; } = new();

    [JsonIgnore]
    public int NodeCount => Content.Sources.Count + Content.Outputs.Count + Content.Modifiers.Count;
}

/// <summary>The user's saved frame templates, stored next to the other settings.</summary>
public static class FrameTemplateLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string LibraryPath { get; } = Path.Combine(AppSettings.Directory, "frame-templates.json");

    private static readonly List<FrameTemplate> _templates = new();

    public static IReadOnlyList<FrameTemplate> Templates => _templates;

    public static void Load()
    {
        _templates.Clear();
        try
        {
            if (!File.Exists(LibraryPath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<FrameTemplate>>(File.ReadAllText(LibraryPath), Options);
            if (loaded != null)
            {
                _templates.AddRange(loaded.Where(template => !string.IsNullOrWhiteSpace(template.Name)));
            }
        }
        catch
        {
            // A broken library must never stop the app from starting.
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(_templates, Options));
        }
        catch
        {
        }
    }

    public static FrameTemplate? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _templates.FirstOrDefault(template =>
                string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Store(FrameTemplate template)
    {
        for (int i = 0; i < _templates.Count; i++)
        {
            if (string.Equals(_templates[i].Name, template.Name, StringComparison.OrdinalIgnoreCase))
            {
                _templates[i] = template;
                Save();
                return;
            }
        }

        _templates.Add(template);
        Save();
    }

    public static void Remove(FrameTemplate template)
    {
        _templates.Remove(template);
        Save();
    }
}
