using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Runs optional OPUS-MT encoder/decoder models through ONNX Runtime.</summary>
internal sealed class OpusMtEngine
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, OpusModel> Cache = new();
    private static readonly object Gate = new();

    public bool IsGpu => false;

    public string Translate(string text, string? sourceCode, string targetCode, CancellationToken token)
    {
        var plan = OpusTranslationModels.Resolve(sourceCode, targetCode);
        if (plan.Error != null)
        {
            throw new InvalidOperationException(plan.Error);
        }

        if (plan.IsEmpty)
        {
            return text;
        }

        var missing = plan.ModelIds.Where(id => !OpusTranslationModels.IsInstalled(id)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Install the OPUS-MT model for this language pair in Settings > Speech & translation.");
        }

        lock (Gate)
        {
            string current = text;
            foreach (string id in plan.ModelIds)
            {
                token.ThrowIfCancellationRequested();
                current = GetModel(id).Translate(current, token);
            }

            return current;
        }
    }

    public static void Drop(string id)
    {
        OpusModel? model;
        lock (CacheLock)
        {
            if (!Cache.Remove(id, out model)) return;
        }

        model.Dispose();
    }

    public static void DropAll()
    {
        List<OpusModel> models;
        lock (CacheLock)
        {
            models = Cache.Values.ToList();
            Cache.Clear();
        }

        foreach (var model in models) model.Dispose();
    }

    private static OpusModel GetModel(string id)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(id, out var existing)) return existing;
            var model = OpusModel.Load(id);
            Cache[id] = model;
            AppLog.Info($"OPUS-MT model '{id}' loaded.");
            return model;
        }
    }
}

internal sealed class OpusModel : IDisposable
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly Dictionary<string, int> _tokenToId;
    private readonly string[] _idToToken;
    private readonly int _decoderStart;
    private readonly int _eos;
    private readonly int _maxLength;
    private readonly string? _prefix;

    private OpusModel(
        InferenceSession encoder,
        InferenceSession decoder,
        SentencePieceTokenizer tokenizer,
        Dictionary<string, int> tokenToId,
        string[] idToToken,
        int decoderStart,
        int eos,
        int maxLength,
        string? prefix)
    {
        _encoder = encoder;
        _decoder = decoder;
        _tokenizer = tokenizer;
        _tokenToId = tokenToId;
        _idToToken = idToToken;
        _decoderStart = decoderStart;
        _eos = eos;
        _maxLength = maxLength;
        _prefix = prefix;
    }

    public static OpusModel Load(string id)
    {
        var descriptor = OpusTranslationModels.Find(id)
            ?? throw new InvalidOperationException("Unknown OPUS-MT model.");
        string directory = OpusTranslationModels.PathFor(id);
        string EncoderPath = Path.Combine(directory, "encoder.onnx");
        string DecoderPath = Path.Combine(directory, "decoder.onnx");

        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(directory, "vocab.json")))
            ?? throw new InvalidDataException("The OPUS-MT vocabulary is missing.");
        var idToToken = new string[vocab.Values.Max() + 1];
        foreach (var pair in vocab)
        {
            idToToken[pair.Value] = pair.Key;
        }

        using var configStream = File.OpenRead(Path.Combine(directory, "config.json"));
        using var config = JsonDocument.Parse(configStream);
        var root = config.RootElement;
        int start = Int(root, "decoder_start_token_id", vocab.Count - 1);
        int eos = Int(root, "eos_token_id", 0);
        int maxLength = Int(root, "max_length", 512);

        using var spm = File.OpenRead(Path.Combine(directory, "source.spm"));
        var tokenizer = SentencePieceTokenizer.Create(spm, false, false, null);
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        };
        var encoder = new InferenceSession(EncoderPath, options);
        var decoder = new InferenceSession(DecoderPath, options);
        return new OpusModel(
            encoder, decoder, tokenizer, vocab, idToToken, start, eos,
            Math.Clamp(maxLength, 32, 512), descriptor.Prefix);
    }

    public string Translate(string text, CancellationToken token)
    {
        var source = new List<long>();
        string prefix = _prefix?.Trim() ?? "";
        if (prefix.Length > 0 && _tokenToId.TryGetValue(prefix, out int prefixId))
        {
            source.Add(prefixId);
            prefix = "";
        }

        foreach (var piece in _tokenizer.EncodeToTokens(
                     prefix + text, out _, false, false, true, true))
        {
            source.Add(_tokenToId.TryGetValue(piece.Value, out int id) ? id : 1);
        }

        if (source.Count > 500) source = source.Take(500).ToList();
        source.Add(_eos);
        long[] sourceIds = source.ToArray();
        var encoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                "input_ids", new DenseTensor<long>(sourceIds, new[] { 1, sourceIds.Length })),
            NamedOnnxValue.CreateFromTensor(
                "attention_mask",
                new DenseTensor<long>(
                    Enumerable.Repeat(1L, sourceIds.Length).ToArray(),
                    new[] { 1, sourceIds.Length })),
        };

        float[] hidden;
        int[] hiddenShape;
        using (var encoded = _encoder.Run(encoderInputs))
        {
            var tensor = encoded.First(item => item.Name == "last_hidden_state").AsTensor<float>();
            hidden = tensor.ToArray();
            hiddenShape = tensor.Dimensions.ToArray();
        }

        var hiddenTensor = new DenseTensor<float>(hidden, hiddenShape);
        var decoderIds = new List<long> { _decoderStart };
        var output = new List<int>();
        int limit = Math.Min(_maxLength, Math.Max(32, sourceIds.Length * 2));
        for (int step = 0; step < limit; step++)
        {
            token.ThrowIfCancellationRequested();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    new DenseTensor<long>(decoderIds.ToArray(), new[] { 1, decoderIds.Count })),
                NamedOnnxValue.CreateFromTensor(
                    "encoder_attention_mask",
                    new DenseTensor<long>(
                        Enumerable.Repeat(1L, sourceIds.Length).ToArray(),
                        new[] { 1, sourceIds.Length })),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hiddenTensor),
            };

            using var result = _decoder.Run(inputs);
            var logits = result.First(item => item.Name == "logits").AsTensor<float>();
            int vocabSize = logits.Dimensions[2];
            int last = decoderIds.Count - 1;
            int best = 0;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < vocabSize; i++)
            {
                float value = logits[0, last, i];
                if (value > bestScore)
                {
                    bestScore = value;
                    best = i;
                }
            }

            if (best == _eos) break;
            output.Add(best);
            decoderIds.Add(best);
        }

        var textBuilder = new StringBuilder();
        var bytes = new List<byte>();
        foreach (int id in output)
        {
            if (id < 0 || id >= _idToToken.Length) continue;
            string value = _idToToken[id];
            if (value is "</s>" or "<pad>" or "<unk>") continue;
            if (value.StartsWith("<0x", StringComparison.Ordinal) &&
                value.EndsWith(">", StringComparison.Ordinal) &&
                int.TryParse(value.AsSpan(3, value.Length - 4),
                    System.Globalization.NumberStyles.HexNumber, null, out int code))
            {
                bytes.Add((byte)code);
                continue;
            }

            if (bytes.Count > 0)
            {
                textBuilder.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                bytes.Clear();
            }

            textBuilder.Append(value);
        }

        if (bytes.Count > 0) textBuilder.Append(Encoding.UTF8.GetString(bytes.ToArray()));
        return textBuilder.ToString().Replace('\u2581', ' ').Trim();
    }

    private static int Int(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    public void Dispose()
    {
        _decoder.Dispose();
        _encoder.Dispose();
    }
}
