using OpenCvSharp;

namespace OpenCVCameraTracking.Core.Detection;

public sealed class HaarFaceDetector : IObjectDetector
{
    private readonly CascadeClassifier _classifier;
    private readonly Size _minimumSize;
    private readonly string _label;

    public HaarFaceDetector(string cascadeFile, int minimumFaceSize = 36, string label = "face")
    {
        if (!File.Exists(cascadeFile))
        {
            throw new FileNotFoundException("找不到人脸级联模型。", cascadeFile);
        }

        _classifier = new CascadeClassifier(cascadeFile);
        if (_classifier.Empty())
        {
            throw new InvalidOperationException($"无法加载人脸级联模型：{cascadeFile}");
        }

        _minimumSize = new Size(minimumFaceSize, minimumFaceSize);
        _label = label;
    }

    public IReadOnlyList<Detection> Detect(Mat bgrFrame)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgrFrame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        return _classifier
            .DetectMultiScale(
                gray,
                scaleFactor: 1.1,
                minNeighbors: 5,
                flags: HaarDetectionTypes.ScaleImage,
                minSize: _minimumSize)
            .Select(box => new Detection(box, _label, 1.0f))
            .ToArray();
    }

    public void Dispose() => _classifier.Dispose();
}
