using OpenCvSharp;

namespace OpenCVCameraTracking.Core.Detection;

public sealed record Detection(Rect Box, string Label, float Confidence, int ClassId = -1);

public sealed record TrackedObject(int Id, Rect Box, string Label, float Confidence);
