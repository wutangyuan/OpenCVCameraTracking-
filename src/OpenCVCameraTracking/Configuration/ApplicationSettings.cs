using System.Text.Json;

namespace OpenCVCameraTracking.Configuration;

public sealed class ApplicationSettings
{
    public string Language { get; set; } = "zh-CN";
    public string SelectedSourceKind { get; set; } = "Device";
    public string SelectedDetectionMode { get; set; } = "Face";
    public string AnimalModelChoice { get; set; } = "BuiltIn";
    public string CustomAnimalModelPath { get; set; } = string.Empty;
    public string LastStreamAddress { get; set; } = string.Empty;
    public string? SelectedStreamId { get; set; }
    public bool RtspLowLatency { get; set; } = true;
    public float FaceConfidence { get; set; } = 0.55f;
    public float AnimalConfidence { get; set; } = 0.35f;
    public List<StreamProfile> Streams { get; set; } = [];

    public ApplicationSettings DeepClone() =>
        JsonSerializer.Deserialize<ApplicationSettings>(JsonSerializer.Serialize(this)) ?? new ApplicationSettings();
}

public sealed class StreamProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;

    public override string ToString() => Name;
}
