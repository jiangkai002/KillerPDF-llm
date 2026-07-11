using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using RapidOcrNet;
using SkiaSharp;

namespace KillerPDF.Services
{
    /// <summary>
    /// Offline PP-OCRv5 engine backed by RapidOcrNet/ONNX Runtime. The Chinese main recognizer also
    /// handles English, Traditional Chinese, Japanese and pinyin, making it a better default for
    /// mixed-language PDF pages than a single Tesseract language pack.
    /// </summary>
    internal sealed class PaddleOcrService : IOcrEngine
    {
        private const string ModelVersion = "ppocr-v5-ch-1";
        private static readonly object ExtractLock = new();
        private static ModelPaths? _cachedPaths;
        private readonly RapidOcr _engine = new();

        private sealed record ModelSpec(string FileName, string ResourceName, string Sha256);
        private sealed record ModelPaths(string Detector, string Classifier, string Recognizer, string Dictionary);

        private static readonly ModelSpec[] Models =
        [
            new("ch_PP-OCRv5_mobile_det.onnx", "KillerPDF.PaddleOcr.ch_PP-OCRv5_mobile_det.onnx",
                "4D97C44A20D30A81AAD087D6A396B08F786C4635742AFC391F6621F5C6AE78AE"),
            new("ch_ppocr_mobile_v2.0_cls_infer.onnx", "KillerPDF.PaddleOcr.ch_ppocr_mobile_v2.0_cls_infer.onnx",
                "E47ACEDF663230F8863FF1AB0E64DD2D82B838FCEB5957146DAB185A89D6215C"),
            new("ch_PP-OCRv5_rec_mobile.onnx", "KillerPDF.PaddleOcr.ch_PP-OCRv5_rec_mobile.onnx",
                "5825FC7EBF84AE7A412BE049820B4D86D77620F204A041697B0494669B1742C5"),
            new("ppocrv5_dict.txt", "KillerPDF.PaddleOcr.ppocrv5_dict.txt",
                "D1979E9F794C464C0D2E0B70A7FE14DD978E9DC644C0E71F14158CDF8342AF1B"),
        ];

        public PaddleOcrService()
        {
            var paths = EnsureModelsExtracted();
            _engine.InitModels(paths.Detector, paths.Classifier, paths.Recognizer, paths.Dictionary,
                Math.Max(1, Math.Min(Environment.ProcessorCount, 4)));
        }

        public OcrResult RecognizeImageFile(string imagePath)
        {
            using var bitmap = SKBitmap.Decode(imagePath) ?? throw new InvalidDataException("Could not decode OCR image.");
            return Recognize(bitmap);
        }

        public OcrResult RecognizeImageBytes(byte[] encodedImage)
        {
            using var bitmap = SKBitmap.Decode(encodedImage) ?? throw new InvalidDataException("Could not decode OCR image.");
            return Recognize(bitmap);
        }

        public OcrResult RecognizeBgra(byte[] bgra, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
            return Recognize(bitmap);
        }

        private OcrResult Recognize(SKBitmap bitmap)
        {
            var options = RapidOcrOptions.Default with
            {
                ReturnWordBox = true,
                TextScore = 0.45f,
                DoAngle = true,
            };
            RapidOcrNet.OcrResult source = _engine.Detect(bitmap, options);
            var result = new OcrResult { Text = source.StrRes?.Trim() ?? "" };
            double confidenceSum = 0;
            int confidenceCount = 0;

            foreach (var block in source.TextBlocks)
            {
                if (block.CharScores is { Length: > 0 })
                {
                    confidenceSum += block.CharScores.Sum(v => (double)v);
                    confidenceCount += block.CharScores.Length;
                }

                if (block.WordResults is { Length: > 0 })
                {
                    foreach (var word in block.WordResults)
                        AddWord(result, word.Text, word.Score, word.BoxPoints);
                }
                else
                {
                    float score = block.CharScores is { Length: > 0 } ? block.CharScores.Average() : block.BoxScore;
                    AddWord(result, block.Text, score, block.BoxPoints);
                }
            }
            result.MeanConfidence = confidenceCount > 0 ? (float)(confidenceSum / confidenceCount) : 0;
            return result;
        }

        private static void AddWord(OcrResult result, string text, float score, SKPointI[] points)
        {
            if (string.IsNullOrWhiteSpace(text) || points.Length == 0) return;
            result.Words.Add(new OcrWord
            {
                Text = text,
                Confidence = score,
                Left = points.Min(p => p.X), Top = points.Min(p => p.Y),
                Right = points.Max(p => p.X), Bottom = points.Max(p => p.Y),
            });
        }

        private static ModelPaths EnsureModelsExtracted()
        {
            lock (ExtractLock)
            {
                if (_cachedPaths is not null) return _cachedPaths;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KillerPDF", "Ocr", ModelVersion);
                Directory.CreateDirectory(dir);
                var assembly = Assembly.GetExecutingAssembly();

                foreach (var model in Models)
                {
                    string destination = Path.Combine(dir, model.FileName);
                    if (File.Exists(destination) && HashFile(destination) == model.Sha256) continue;
                    string temp = destination + ".tmp";
                    using (var input = assembly.GetManifestResourceStream(model.ResourceName)
                        ?? throw new InvalidOperationException("Missing embedded PaddleOCR model: " + model.FileName))
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    if (HashFile(temp) != model.Sha256)
                    {
                        File.Delete(temp);
                        throw new InvalidDataException("PaddleOCR model integrity check failed: " + model.FileName);
                    }
                    File.Move(temp, destination, true);
                }

                return _cachedPaths = new ModelPaths(
                    Path.Combine(dir, Models[0].FileName), Path.Combine(dir, Models[1].FileName),
                    Path.Combine(dir, Models[2].FileName), Path.Combine(dir, Models[3].FileName));
            }
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public void Dispose() => _engine.Dispose();
    }
}
