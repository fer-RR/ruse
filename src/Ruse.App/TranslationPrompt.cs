namespace Ruse.App;

internal static class TranslationPrompt
{
    public static string System(string targetCode)
    {
        string target = TranslationLanguages.Find(targetCode)?.PromptName ?? "English";
        string prompt = $"You are a professional translator. Translate the user's text into {target}. " +
                        "Preserve names, numbers, tone and punctuation. Output only the translation, " +
                        "without quotes, labels, notes or explanation.";
        string glossary = TranslationSettings.Glossary.Trim();
        return string.IsNullOrEmpty(glossary)
            ? prompt
            : prompt + " Glossary (source=target): " +
              glossary.Replace("\r", " ").Replace("\n", "; ");
    }
}
