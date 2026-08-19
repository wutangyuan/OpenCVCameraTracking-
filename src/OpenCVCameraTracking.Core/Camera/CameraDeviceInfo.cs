namespace OpenCVCameraTracking.Core.Camera;

public sealed record CameraDeviceInfo(int Index, string Name)
{
    public override string ToString() => $"{Index}: {Name}";
}
