using OpenCvSharp;
using OpenCVCameraTracking.Core.Detection;

namespace OpenCVCameraTracking.Core;

/// <summary>
/// A restricted zone stored in frame-relative coordinates. Keeping the values
/// normalized means a zone survives a camera resolution change.
/// </summary>
public sealed record RestrictedZone(
    float X,
    float Y,
    float Width,
    float Height,
    string Name = "Zone",
    string Id = "")
{
    public bool IsValid =>
        !float.IsNaN(X) && !float.IsInfinity(X) &&
        !float.IsNaN(Y) && !float.IsInfinity(Y) &&
        !float.IsNaN(Width) && !float.IsInfinity(Width) &&
        !float.IsNaN(Height) && !float.IsInfinity(Height) &&
        X >= 0f && Y >= 0f && Width > 0f && Height > 0f &&
        X + Width <= 1.001f && Y + Height <= 1.001f;

    public Rect ToPixelRect(int frameWidth, int frameHeight)
    {
        if (!IsValid || frameWidth <= 0 || frameHeight <= 0)
        {
            throw new CoreException(CoreErrorCode.RestrictedZoneInvalid);
        }

        var left = Math.Clamp((int)MathF.Round(X * frameWidth), 0, frameWidth - 1);
        var top = Math.Clamp((int)MathF.Round(Y * frameHeight), 0, frameHeight - 1);
        var right = Math.Clamp((int)MathF.Round((X + Width) * frameWidth), left + 1, frameWidth);
        var bottom = Math.Clamp((int)MathF.Round((Y + Height) * frameHeight), top + 1, frameHeight);
        return new Rect(left, top, right - left, bottom - top);
    }
}

public sealed class RestrictedZoneAlertEventArgs(
    TrackedObject target,
    Rect zone,
    DateTimeOffset timestamp,
    string zoneName = "Zone") : EventArgs
{
    public TrackedObject Target { get; } = target;
    public Rect Zone { get; } = zone;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string ZoneName { get; } = zoneName;
}
