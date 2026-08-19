using OpenCvSharp;

namespace OpenCVCameraTracking.Core.Detection;

public interface IObjectDetector : IDisposable
{
    IReadOnlyList<Detection> Detect(Mat bgrFrame);
}
