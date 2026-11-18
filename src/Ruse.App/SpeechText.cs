using System.Text.RegularExpressions;

namespace Ruse.App;

/// <summary>
/// Splits TTS input into short, speakable clauses. Short clauses start playing
/// sooner and make the timing scheduler less likely to fall behind.
/// </summary>
internal static class SpeechText
{
    private const int MinChars = 18;
    private const int MaxChars = 110;

    private static readonly Regex Boundary = new(@"(?<=[.!?,;:…])\s+", RegexOptions.Compiled);

    public static List<string> Clauses(string text)
    {
        var result = new List<string>();
        string remaining = text.Trim();
        if (remaining.Length == 0) return result;

        foreach (string sentence in SentenceSplitter.Split(remaining, 0, 1).Select(cue => cue.Text))
        {
            string buffer = "";
            foreach (string part in Boundary.Split(sentence))
            {
                string value = part.Trim();
                if (value.Length == 0) continue;
                buffer = buffer.Length == 0 ? value : buffer + " " + value;
                if (buffer.Length >= MinChars || buffer.EndsWith('.') || buffer.EndsWith('!') || buffer.EndsWith('?'))
                {
                    Flush(buffer);
                    buffer = "";
                }
            }

            if (buffer.Length > 0) Flush(buffer);
        }

        return result;

        void Flush(string value)
        {
            value = value.Trim();
            while (value.Length > MaxChars)
            {
                int split = value.LastIndexOf(' ', MaxChars);
                if (split < MinChars) split = MaxChars;
                result.Add(value[..split].Trim());
                value = value[split..].Trim();
            }

            if (value.Length > 0) result.Add(value);
        }
    }
}
