using System.Text.Json;
using SherpaOnnx;

namespace Ruse.TtsHost;

internal sealed class TtsRequest
{
    public string Family { get; set; } = "";
    public string ModelDir { get; set; } = "";
    public string Text { get; set; } = "";
    public float Speed { get; set; } = 1f;
    public int Voice { get; set; }
    public string OutputPath { get; set; } = "";
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--request", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: Ruse.TtsHost --request <request.json>");
            return 64;
        }

        try
        {
            var request = JsonSerializer.Deserialize<TtsRequest>(File.ReadAllText(args[1]))
                ?? throw new InvalidDataException("The TTS request is empty.");
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                throw new InvalidDataException("The TTS text is empty.");
            }

            string modelDir = Path.GetFullPath(request.ModelDir);
            var config = new OfflineTtsConfig
            {
                MaxNumSentences = 1,
                SilenceScale = 0.2f,
            };
            config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            config.Model.Provider = "cpu";
            config.Model.Debug = 0;

            if (string.Equals(request.Family, "kokoro", StringComparison.OrdinalIgnoreCase))
            {
                config.Model.Kokoro.Model = Required(modelDir, "model.int8.onnx");
                config.Model.Kokoro.Voices = Required(modelDir, "voices.bin");
                config.Model.Kokoro.Tokens = Required(modelDir, "tokens.txt");
                config.Model.Kokoro.Lexicon =
                    Required(modelDir, "lexicon-us-en.txt") + "," + Required(modelDir, "lexicon-zh.txt");
            }
            else if (string.Equals(request.Family, "supertonic", StringComparison.OrdinalIgnoreCase))
            {
                config.Model.Supertonic.DurationPredictor = Required(modelDir, "duration_predictor.int8.onnx");
                config.Model.Supertonic.TextEncoder = Required(modelDir, "text_encoder.int8.onnx");
                config.Model.Supertonic.VectorEstimator = Required(modelDir, "vector_estimator.int8.onnx");
                config.Model.Supertonic.Vocoder = Required(modelDir, "vocoder.int8.onnx");
                config.Model.Supertonic.TtsJson = Required(modelDir, "tts.json");
                config.Model.Supertonic.UnicodeIndexer = Required(modelDir, "unicode_indexer.bin");
                config.Model.Supertonic.VoiceStyle = Required(modelDir, "voice.bin");
            }
            else
            {
                throw new InvalidDataException($"Unknown TTS model family '{request.Family}'.");
            }

            using var tts = new OfflineTts(config);
            var audio = tts.Generate(
                request.Text,
                Math.Clamp(request.Speed <= 0 ? 1f : request.Speed, 0.5f, 2f),
                Math.Max(0, request.Voice));
            try
            {
                if (audio.NumSamples <= 0)
                {
                    throw new InvalidDataException("The TTS model returned no audio.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
                if (!audio.SaveToWaveFile(request.OutputPath))
                {
                    throw new IOException("The TTS host could not write the generated audio.");
                }

                return 0;
            }
            finally
            {
                audio.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Required(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing TTS model file: {name}", path);
        }

        return path;
    }
}
