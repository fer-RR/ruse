namespace Ruse.App;

internal static class TranslationSettings
{
    public static string Glossary { get; set; } = "";
    public static bool MemoryEnabled { get; set; } = true;

    public static void Update(string? glossary, bool memoryEnabled)
    {
        glossary = (glossary ?? "").Trim();
        Glossary = glossary.Length > 4000 ? glossary[..4000] : glossary;
        MemoryEnabled = memoryEnabled;
    }
}
