using OpenCvSharp;
using OpenCvSharp.Dnn;
using System.Runtime.InteropServices;

namespace OpenCVCameraTracking.Core.Detection;

/// <summary>
/// ONNX detector compatible with common YOLOv5/YOLOv8 export layouts.
/// The default class filter contains the ten animal classes in COCO.
/// </summary>
public sealed class YoloOnnxDetector : IObjectDetector
{
    public static readonly string[] CocoLabels =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
        "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
        "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
        "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
        "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
        "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone",
        "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear",
        "hair drier", "toothbrush"
    ];

    public static readonly string[] CocoAnimalLabels =
    ["bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"];

    private readonly Net _network;
    private readonly string[] _labels;
    private readonly HashSet<string>? _allowedLabels;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly float _confidenceThreshold;
    private readonly float _nmsThreshold;

    public YoloOnnxDetector(
        string modelFile,
        IEnumerable<string>? labels = null,
        IEnumerable<string>? allowedLabels = null,
        int inputWidth = 640,
        int inputHeight = 640,
        float confidenceThreshold = 0.35f,
        float nmsThreshold = 0.45f)
    {
        if (!File.Exists(modelFile))
        {
            throw new FileNotFoundException("找不到 ONNX 检测模型。", modelFile);
        }

        _labels = (labels ?? CocoLabels).ToArray();
        _allowedLabels = allowedLabels is null
            ? null
            : new HashSet<string>(allowedLabels, StringComparer.OrdinalIgnoreCase);
        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _confidenceThreshold = confidenceThreshold;
        _nmsThreshold = nmsThreshold;

        _network = CvDnn.ReadNetFromOnnx(modelFile)
            ?? throw new InvalidOperationException($"无法加载 ONNX 模型：{modelFile}");
        if (_network.Empty())
        {
            throw new InvalidOperationException($"无法加载 ONNX 模型：{modelFile}");
        }

        _network.SetPreferableBackend(Backend.OPENCV);
        _network.SetPreferableTarget(Target.CPU);
    }

    public IReadOnlyList<Detection> Detect(Mat bgrFrame)
    {
        using var letterboxed = Letterbox(bgrFrame, out var scale, out var paddingX, out var paddingY);
        using var blob = CvDnn.BlobFromImage(
            letterboxed,
            scaleFactor: 1.0 / 255.0,
            size: new Size(_inputWidth, _inputHeight),
            mean: Scalar.All(0),
            swapRB: true,
            crop: false);

        _network.SetInput(blob);
        using var output = _network.Forward();
        return ParseOutput(output, bgrFrame.Size(), scale, paddingX, paddingY);
    }

    private IReadOnlyList<Detection> ParseOutput(
        Mat output,
        Size originalSize,
        float scale,
        int paddingX,
        int paddingY)
    {
        if (output.Dims is < 2 or > 3)
        {
            throw new NotSupportedException($"不支持的 YOLO 输出维度：{output.Dims}");
        }

        var shape = Enumerable.Range(0, output.Dims).Select(output.Size).ToArray();
        var rows = shape[^2];
        var columns = shape[^1];
        var transposed = rows <= 128 && columns > rows;
        var predictionCount = transposed ? columns : rows;
        var attributeCount = transposed ? rows : columns;

        var values = new float[checked((int)output.Total())];
        Marshal.Copy(output.Data, values, 0, values.Length);
        var boxes = new List<Rect>();
        var confidences = new List<float>();
        var classIds = new List<int>();

        float ValueAt(int prediction, int attribute) => transposed
            ? values[attribute * predictionCount + prediction]
            : values[prediction * attributeCount + attribute];

        if (attributeCount == 6)
        {
            ParseNmsOutput();
        }
        else
        {
            ParseRawOutput();
        }

        if (boxes.Count == 0)
        {
            return [];
        }

        CvDnn.NMSBoxes(
            boxes,
            confidences,
            _confidenceThreshold,
            _nmsThreshold,
            out var keptIndices,
            eta: 1f,
            topK: 0);
        return keptIndices
            .Select(index => new Detection(
                boxes[index],
                LabelFor(classIds[index]),
                confidences[index],
                classIds[index]))
            .ToArray();

        void ParseNmsOutput()
        {
            for (var prediction = 0; prediction < predictionCount; prediction++)
            {
                var confidence = ValueAt(prediction, 4);
                var classId = (int)ValueAt(prediction, 5);
                if (!Accept(classId, confidence))
                {
                    continue;
                }

                var left = (ValueAt(prediction, 0) - paddingX) / scale;
                var top = (ValueAt(prediction, 1) - paddingY) / scale;
                var right = (ValueAt(prediction, 2) - paddingX) / scale;
                var bottom = (ValueAt(prediction, 3) - paddingY) / scale;
                AddBox(left, top, right - left, bottom - top, confidence, classId);
            }
        }

        void ParseRawOutput()
        {
            var hasObjectness = attributeCount == _labels.Length + 5;
            var classOffset = hasObjectness ? 5 : 4;
            var classCount = Math.Min(_labels.Length, attributeCount - classOffset);
            if (classCount <= 0)
            {
                throw new NotSupportedException($"无法识别 YOLO 输出形状：[{string.Join(',', shape)}]");
            }

            for (var prediction = 0; prediction < predictionCount; prediction++)
            {
                var objectness = hasObjectness ? ValueAt(prediction, 4) : 1f;
                var bestClass = -1;
                var bestScore = 0f;
                for (var classId = 0; classId < classCount; classId++)
                {
                    var score = ValueAt(prediction, classOffset + classId) * objectness;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestClass = classId;
                    }
                }

                if (!Accept(bestClass, bestScore))
                {
                    continue;
                }

                var centerX = (ValueAt(prediction, 0) - paddingX) / scale;
                var centerY = (ValueAt(prediction, 1) - paddingY) / scale;
                var width = ValueAt(prediction, 2) / scale;
                var height = ValueAt(prediction, 3) / scale;
                AddBox(centerX - width / 2, centerY - height / 2, width, height, bestScore, bestClass);
            }
        }

        bool Accept(int classId, float confidence)
        {
            if (confidence < _confidenceThreshold || classId < 0 || classId >= _labels.Length)
            {
                return false;
            }

            return _allowedLabels is null || _allowedLabels.Contains(_labels[classId]);
        }

        void AddBox(float x, float y, float width, float height, float confidence, int classId)
        {
            var left = Math.Clamp((int)MathF.Round(x), 0, originalSize.Width - 1);
            var top = Math.Clamp((int)MathF.Round(y), 0, originalSize.Height - 1);
            var right = Math.Clamp((int)MathF.Round(x + width), left + 1, originalSize.Width);
            var bottom = Math.Clamp((int)MathF.Round(y + height), top + 1, originalSize.Height);
            boxes.Add(new Rect(left, top, right - left, bottom - top));
            confidences.Add(confidence);
            classIds.Add(classId);
        }
    }

    private Mat Letterbox(Mat source, out float scale, out int paddingX, out int paddingY)
    {
        scale = Math.Min((float)_inputWidth / source.Width, (float)_inputHeight / source.Height);
        var resizedWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));
        paddingX = (_inputWidth - resizedWidth) / 2;
        paddingY = (_inputHeight - resizedHeight) / 2;

        var result = new Mat(new Size(_inputWidth, _inputHeight), MatType.CV_8UC3, new Scalar(114, 114, 114));
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(resizedWidth, resizedHeight));
        using var target = new Mat(result, new Rect(paddingX, paddingY, resizedWidth, resizedHeight));
        resized.CopyTo(target);
        return result;
    }

    private string LabelFor(int classId) => classId >= 0 && classId < _labels.Length
        ? _labels[classId]
        : $"class-{classId}";

    public void Dispose() => _network.Dispose();
}
