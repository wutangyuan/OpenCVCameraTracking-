using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace OpenCVCameraTracking.Core.Detection;

/// <summary>
/// Lightweight DNN face detector based on OpenCV YuNet. Frames are letterboxed
/// to a small fixed input so RTSP capture stays responsive on CPU-only systems.
/// </summary>
public sealed class YuNetFaceDetector : IObjectDetector
{
    private readonly FaceDetectorYN _detector;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly string _label;

    public YuNetFaceDetector(
        string modelFile,
        float confidenceThreshold = 0.55f,
        int inputWidth = 320,
        int inputHeight = 320,
        string label = "face")
    {
        if (!File.Exists(modelFile))
        {
            throw new FileNotFoundException("YuNet face model was not found.", modelFile);
        }

        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _label = label;
        _detector = FaceDetectorYN.Create(
            modelFile,
            string.Empty,
            new Size(inputWidth, inputHeight),
            confidenceThreshold,
            0.3f,
            5_000,
            Backend.OPENCV,
            Target.CPU)
            ?? throw new InvalidOperationException($"Unable to load YuNet face model: {modelFile}");
    }

    public IReadOnlyList<Detection> Detect(Mat bgrFrame)
    {
        using var input = Letterbox(bgrFrame, out var scale);
        using var faces = new Mat();
        if (_detector.Detect(input, faces) == 0 || faces.Empty())
        {
            return [];
        }

        var rowCount = faces.Rows;
        var detections = new List<Detection>(rowCount);
        for (var row = 0; row < rowCount; row++)
        {
            var x = faces.At<float>(row, 0) / scale;
            var y = faces.At<float>(row, 1) / scale;
            var width = faces.At<float>(row, 2) / scale;
            var height = faces.At<float>(row, 3) / scale;
            var confidence = faces.At<float>(row, 14);

            var left = Math.Clamp((int)MathF.Round(x), 0, bgrFrame.Width - 1);
            var top = Math.Clamp((int)MathF.Round(y), 0, bgrFrame.Height - 1);
            var right = Math.Clamp((int)MathF.Round(x + width), left + 1, bgrFrame.Width);
            var bottom = Math.Clamp((int)MathF.Round(y + height), top + 1, bgrFrame.Height);
            detections.Add(new Detection(
                new Rect(left, top, right - left, bottom - top),
                _label,
                confidence));
        }

        return detections;
    }

    private Mat Letterbox(Mat source, out float scale)
    {
        scale = Math.Min((float)_inputWidth / source.Width, (float)_inputHeight / source.Height);
        var resizedWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));
        var result = new Mat(new Size(_inputWidth, _inputHeight), MatType.CV_8UC3, new Scalar(114, 114, 114));
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(resizedWidth, resizedHeight), interpolation: InterpolationFlags.Linear);
        using var target = new Mat(result, new Rect(0, 0, resizedWidth, resizedHeight));
        resized.CopyTo(target);
        return result;
    }

    public void Dispose() => _detector.Dispose();
}
