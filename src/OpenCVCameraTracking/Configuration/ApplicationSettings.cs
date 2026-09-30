using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCVCameraTracking.Core.Camera;
using OpenCVCameraTracking.Core.Notifications;

namespace OpenCVCameraTracking.Configuration;

public sealed class ApplicationSettings
{
    public string Language { get; set; } = "zh-CN";
    public string ThemeMode { get; set; } = "System";
    public string SelectedSourceKind { get; set; } = "Device";
    public string SelectedDetectionMode { get; set; } = "Face";
    public string AnimalModelChoice { get; set; } = "BuiltIn";
    public string CustomAnimalModelPath { get; set; } = string.Empty;
    public string LastStreamAddress { get; set; } = string.Empty;
    public string? SelectedStreamId { get; set; }
    public string SelectedStreamVariant { get; set; } = "Main";
    public bool RtspLowLatency { get; set; } = true;
    public VideoCaptureBackend PreferredBackend { get; set; } = VideoCaptureBackend.Auto;
    public string SelectedLayout { get; set; } = "Single";
    public List<string> LayoutStreamIds { get; set; } = [];
    /// <summary>Selected source keys for the multi-camera picker.</summary>
    public List<string> MultiPreviewSourceKeys { get; set; } = [];
    public float FaceConfidence { get; set; } = 0.55f;
    public float AnimalConfidence { get; set; } = 0.35f;
    public RestrictedZoneSettings? RestrictedZone { get; set; }
    public List<NotificationChannelSettings> NotificationChannels { get; set; } = [];
    public List<StreamProfile> Streams { get; set; } = [];
    public List<CameraDeviceProfile> CameraDevices { get; set; } = [];

    public ApplicationSettings DeepClone() =>
        JsonSerializer.Deserialize<ApplicationSettings>(JsonSerializer.Serialize(this)) ?? new ApplicationSettings();
}

public sealed class RestrictedZoneSettings
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        !double.IsNaN(X) && !double.IsInfinity(X) &&
        !double.IsNaN(Y) && !double.IsInfinity(Y) &&
        !double.IsNaN(Width) && !double.IsInfinity(Width) &&
        !double.IsNaN(Height) && !double.IsInfinity(Height) &&
        X >= 0d && Y >= 0d && Width > 0d && Height > 0d &&
        X + Width <= 1.001d && Y + Height <= 1.001d;

    public static RestrictedZoneSettings FromPixelRect(OpenCvSharp.Rect region, int frameWidth, int frameHeight) => new()
    {
        X = Math.Clamp((double)region.X / frameWidth, 0d, 1d),
        Y = Math.Clamp((double)region.Y / frameHeight, 0d, 1d),
        Width = Math.Clamp((double)region.Width / frameWidth, 0d, 1d),
        Height = Math.Clamp((double)region.Height / frameHeight, 0d, 1d)
    };

    public OpenCVCameraTracking.Core.RestrictedZone ToCore() =>
        new((float)X, (float)Y, (float)Width, (float)Height);
}

public sealed class StreamProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string SubAddress { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Group { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public string AddressFor(bool useSubStream) =>
        useSubStream && !string.IsNullOrWhiteSpace(SubAddress) ? SubAddress : Address;

    public override string ToString() => Name;
}

public sealed class CameraDeviceProfile
{
    public int DeviceIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string DisplayName => $"{DeviceIndex}: {Name}";
}
