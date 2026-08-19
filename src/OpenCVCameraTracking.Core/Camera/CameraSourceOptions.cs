namespace OpenCVCameraTracking.Core.Camera;

public enum CameraSourceKind
{
    Device,
    Stream,
    File
}

public sealed record CameraSourceOptions
{
    public CameraSourceKind Kind { get; init; } = CameraSourceKind.Device;
    public int DeviceIndex { get; init; }
    public string? Address { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FramesPerSecond { get; init; }
    public int OpenTimeoutMilliseconds { get; init; } = 5_000;
    public int ReadTimeoutMilliseconds { get; init; } = 3_000;
    public bool PreferTcpForRtsp { get; init; } = true;
    public bool LowLatencyMode { get; init; } = true;
}
