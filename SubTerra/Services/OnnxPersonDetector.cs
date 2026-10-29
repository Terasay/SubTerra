using System.IO;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SubTerra.Models;

namespace SubTerra.Services;

public interface IPersonImageDetector : IDisposable
{
    ImageBox[] Detect(ScanImage image, float threshold, CancellationToken token);
}

/// <summary>Official YOLOX-Nano export. Decoding adapted from Megvii YOLOX demo_utils.py (Apache-2.0; bundled license).
/// Raw BGR CHW, grid/stride decode, objectness × COCO person probability.</summary>
public sealed class OnnxPersonDetector : IPersonImageDetector
{
    public const string ModelName = "YOLOX-Nano COCO · ONNX · CPU";
    private const string Sha256 = "C789161ED43C8269FCD4E67C67EEEB4E80C622DA2EB296A20BC6007BD18A0B7D";
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "yolox_nano.onnx");
    private readonly InferenceSession _session;
    public OnnxPersonDetector(string? path = null)
    {
        path ??= DefaultModelPath;
        if (!File.Exists(path)) throw new FileNotFoundException("Модель людей не найдена. Скопируйте папку Assets/Models из сборки.", path);
        using (var stream = File.OpenRead(path))
            if (Convert.ToHexString(SHA256.HashData(stream)) != Sha256) throw new InvalidDataException("Модель YOLOX-Nano повреждена или заменена.");
        using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new(path, options);
        var input = _session.InputMetadata.Single();
        if (!input.Value.Dimensions.SequenceEqual(new[] { 1, 3, 416, 416 })) { _session.Dispose(); throw new InvalidDataException("Неожиданный вход модели людей."); }
    }
    public ImageBox[] Detect(ScanImage image, float threshold, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (image.Size != 416 || image.Rgb.Length != 416 * 416 * 3 || !float.IsFinite(threshold) || threshold is < .1f or > .95f)
            throw new ArgumentException("Изображение детектора: 416 × 416; порог: 0,1–0,95.");
        var pixels = 416 * 416; var input = new float[3 * pixels];
        for (var i = 0; i < pixels; i++) { input[i] = image.Rgb[i * 3 + 2]; input[pixels + i] = image.Rgb[i * 3 + 1]; input[2 * pixels + i] = image.Rgb[i * 3]; }
        using var run = new RunOptions();
        using var registration = token.Register(() => run.Terminate = true);
        try
        {
            using var result = _session.Run([NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.Single(), new DenseTensor<float>(input, [1, 3, 416, 416]))], _session.OutputMetadata.Keys.ToArray(), run);
            token.ThrowIfCancellationRequested();
            var output = result.Single().AsTensor<float>();
            if (!output.Dimensions.SequenceEqual(new[] { 1, 3549, 85 })) throw new InvalidDataException("Неожиданный выход модели людей.");
            return Decode(output.ToArray(), threshold);
        }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
    public static ImageBox[] Decode(float[] output, float threshold)
    {
        if (output.Length != 3549 * 85) throw new ArgumentException("Неожиданный размер выхода YOLOX.");
        var boxes = new List<ImageBox>(); var row = 0;
        foreach (var stride in new[] { 8, 16, 32 })
            for (var y = 0; y < 416 / stride; y++) for (var x = 0; x < 416 / stride; x++, row++)
            {
                var offset = row * 85; var score = output[offset + 4] * output[offset + 5];
                if (!float.IsFinite(score) || score < threshold || score > 1) continue;
                var cx = (output[offset] + x) * stride; var cy = (output[offset + 1] + y) * stride;
                var w = MathF.Exp(output[offset + 2]) * stride; var h = MathF.Exp(output[offset + 3]) * stride;
                if (!float.IsFinite(cx + cy + w + h)) continue;
                var box = new ImageBox(Math.Clamp(cx - w / 2, 0, 415), Math.Clamp(cy - h / 2, 0, 415), Math.Clamp(cx + w / 2, 0, 415), Math.Clamp(cy + h / 2, 0, 415), score);
                if (box.Area >= 64) boxes.Add(box);
            }
        var kept = new List<ImageBox>();
        foreach (var box in boxes.OrderByDescending(b => b.Score))
        {
            if (kept.Any(k => IntersectionOverUnion(k, box) > .45)) continue;
            kept.Add(box); if (kept.Count == 64) break;
        }
        return kept.ToArray();
    }
    public static float IntersectionOverUnion(ImageBox a, ImageBox b)
    {
        var area = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return area / Math.Max(1e-6f, a.Area + b.Area - area);
    }
    public void Dispose() => _session.Dispose();
}
