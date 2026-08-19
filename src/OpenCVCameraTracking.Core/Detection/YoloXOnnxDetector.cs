using OpenCvSharp;
using OpenCvSharp.Dnn;
using System.Runtime.InteropServices;

namespace OpenCVCameraTracking.Core.Detection;

/// <summary>
/// Parser for the OpenCV Zoo YOLOX-S COCO model. It is used by the bundled
/// INT8 animal detector and intentionally exposes only COCO animal classes.
/// </summary>
public sealed class YoloXOnnxDetector : IObjectDetector
{
    private const int InputSize = 640;
    private readonly Net _network;
    private readonly HashSet<int> _allowedClassIds;
    private readonly IReadOnlyDictionary<string, string>? _displayLabels;
    private readonly float _confidenceThreshold;
    private readonly float _nmsThreshold;

    public YoloXOnnxDetector(
        string modelFile,
        IEnumerable<string>? allowedLabels = null,
        IReadOnlyDictionary<string, string>? displayLabels = null,
        float confidenceThreshold = 0.35f,
        float nmsThreshold = 0.5f)
    {
        if (!File.Exists(modelFile))
        {
            throw new FileNotFoundException("YOLOX animal model was not found.", modelFile);
        }

        var allowed = new HashSet<string>(
            allowedLabels ?? YoloOnnxDetector.CocoAnimalLabels,
            StringComparer.OrdinalIgnoreCase);
        _allowedClassIds = YoloOnnxDetector.CocoLabels
            .Select((label, index) => (label, index))
            .Where(item => allowed.Contains(item.label))
            .Select(item => item.index)
            .ToHashSet();
        _displayLabels = displayLabels;
        _confidenceThreshold = confidenceThreshold;
        _nmsThreshold = nmsThreshold;

        _network = CvDnn.ReadNetFromOnnx(modelFile)
            ?? throw new InvalidOperationException($"Unable to load YOLOX model: {modelFile}");
        if (_network.Empty())
        {
            throw new InvalidOperationException($"Unable to load YOLOX model: {modelFile}");
        }

        _network.SetPreferableBackend(Backend.OPENCV);
        _network.SetPreferableTarget(Target.CPU);
    }

    public IReadOnlyList<Detection> Detect(Mat bgrFrame)
    {
        using var letterboxed = Letterbox(bgrFrame, out var scale);
        using var blob = CvDnn.BlobFromImage(
            letterboxed,
            scaleFactor: 1.0,
            size: new Size(InputSize, InputSize),
            mean: Scalar.All(0),
            swapRB: true,
            crop: false);
        _network.SetInput(blob);
        using var output = _network.Forward();
        return Parse(output, bgrFrame.Size(), scale);
    }

    private IReadOnlyList<Detection> Parse(Mat output, Size originalSize, float scale)
    {
        var shape = Enumerable.Range(0, output.Dims).Select(output.Size).ToArray();
        var predictionCount = shape[^2];
        var attributeCount = shape[^1];
        if (predictionCount != 8_400 || attributeCount < 85)
        {
            throw new NotSupportedException($"Unexpected YOLOX output shape: [{string.Join(',', shape)}]");
        }

        var values = new float[checked((int)output.Total())];
        Marshal.Copy(output.Data, values, 0, values.Length);
        var boxes = new List<Rect>();
        var confidences = new List<float>();
        var classIds = new List<int>();

        for (var prediction = 0; prediction < predictionCount; prediction++)
        {
            GetGrid(prediction, out var gridX, out var gridY, out var stride);
            var offset = prediction * attributeCount;
            var objectness = values[offset + 4];
            if (objectness < _confidenceThreshold)
            {
                continue;
            }

            var bestClassId = -1;
            var bestConfidence = 0f;
            foreach (var classId in _allowedClassIds)
            {
                var confidence = objectness * values[offset + 5 + classId];
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestClassId = classId;
                }
            }

            if (bestConfidence < _confidenceThreshold || bestClassId < 0)
            {
                continue;
            }

            var centerX = (values[offset] + gridX) * stride / scale;
            var centerY = (values[offset + 1] + gridY) * stride / scale;
            var width = MathF.Exp(values[offset + 2]) * stride / scale;
            var height = MathF.Exp(values[offset + 3]) * stride / scale;
            var left = Math.Clamp((int)MathF.Round(centerX - width / 2), 0, originalSize.Width - 1);
            var top = Math.Clamp((int)MathF.Round(centerY - height / 2), 0, originalSize.Height - 1);
            var right = Math.Clamp((int)MathF.Round(centerX + width / 2), left + 1, originalSize.Width);
            var bottom = Math.Clamp((int)MathF.Round(centerY + height / 2), top + 1, originalSize.Height);

            boxes.Add(new Rect(left, top, right - left, bottom - top));
            confidences.Add(bestConfidence);
            classIds.Add(bestClassId);
        }

        var kept = NonMaximumSuppressionByClass(boxes, confidences, classIds);
        return kept.Select(index =>
        {
            var originalLabel = YoloOnnxDetector.CocoLabels[classIds[index]];
            var label = _displayLabels?.GetValueOrDefault(originalLabel) ?? originalLabel;
            return new Detection(boxes[index], label, confidences[index], classIds[index]);
        }).ToArray();
    }

    private IReadOnlyList<int> NonMaximumSuppressionByClass(
        IReadOnlyList<Rect> boxes,
        IReadOnlyList<float> confidences,
        IReadOnlyList<int> classIds)
    {
        var kept = new List<int>();
        foreach (var group in Enumerable.Range(0, boxes.Count).GroupBy(index => classIds[index]))
        {
            var originalIndices = group.ToArray();
            CvDnn.NMSBoxes(
                originalIndices.Select(index => boxes[index]),
                originalIndices.Select(index => confidences[index]),
                _confidenceThreshold,
                _nmsThreshold,
                out var localIndices,
                eta: 1f,
                topK: 100);
            kept.AddRange(localIndices.Select(index => originalIndices[index]));
        }

        return kept;
    }

    private static void GetGrid(int prediction, out int gridX, out int gridY, out int stride)
    {
        int localIndex;
        int gridWidth;
        if (prediction < 6_400)
        {
            stride = 8;
            gridWidth = 80;
            localIndex = prediction;
        }
        else if (prediction < 8_000)
        {
            stride = 16;
            gridWidth = 40;
            localIndex = prediction - 6_400;
        }
        else
        {
            stride = 32;
            gridWidth = 20;
            localIndex = prediction - 8_000;
        }

        gridX = localIndex % gridWidth;
        gridY = localIndex / gridWidth;
    }

    private static Mat Letterbox(Mat source, out float scale)
    {
        scale = Math.Min((float)InputSize / source.Width, (float)InputSize / source.Height);
        var resizedWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));
        var result = new Mat(new Size(InputSize, InputSize), MatType.CV_32FC3, Scalar.All(114));
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(resizedWidth, resizedHeight), interpolation: InterpolationFlags.Linear);
        using var resizedFloat = new Mat();
        resized.ConvertTo(resizedFloat, MatType.CV_32FC3);
        using var target = new Mat(result, new Rect(0, 0, resizedWidth, resizedHeight));
        resizedFloat.CopyTo(target);
        return result;
    }

    public void Dispose() => _network.Dispose();
}
