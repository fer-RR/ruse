namespace Ruse.App;

internal sealed record TranslationLanguage(
    string Code,
    string Label,
    string PromptName,
    string WhisperCode,
    bool NeedsModel);

internal static class TranslationLanguages
{
    public const string English = "en";
    public const string DefaultCode = English;

    public static IReadOnlyList<TranslationLanguage> All { get; } = new[]
    {
        new TranslationLanguage("en", "English", "English", "en", false),
        new TranslationLanguage("pt-BR", "Portuguese (Brazil)", "Brazilian Portuguese", "pt", true),
        new TranslationLanguage("es", "Spanish", "Spanish", "es", true),
        new TranslationLanguage("fr", "French", "French", "fr", true),
        new TranslationLanguage("de", "German", "German", "de", true),
        new TranslationLanguage("it", "Italian", "Italian", "it", true),
        new TranslationLanguage("ja", "Japanese", "Japanese", "ja", true),
        new TranslationLanguage("ko", "Korean", "Korean", "ko", true),
        new TranslationLanguage("zh-CN", "Chinese (Simplified)", "Simplified Chinese", "zh", true),
    };

    public static string Normalize(string? code) =>
        Find(code)?.Code ?? DefaultCode;

    public static TranslationLanguage? Find(string? code) =>
        All.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));

    public static bool NeedsModel(string? code) => Find(code)?.NeedsModel ?? false;

    public static bool MatchesSource(string? target, string? detectedWhisperCode)
    {
        var language = Find(target);
        if (language == null || string.IsNullOrWhiteSpace(detectedWhisperCode))
        {
            return false;
        }

        return string.Equals(language.WhisperCode, detectedWhisperCode, StringComparison.OrdinalIgnoreCase);
    }
}
