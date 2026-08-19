using OpenCVCameraTracking.Core.Detection;
using OpenCvSharp;
using ObjectDetection = OpenCVCameraTracking.Core.Detection.Detection;

namespace OpenCVCameraTracking.Core.Tracking;

/// <summary>
/// Lightweight tracking-by-detection implementation. It associates detections by
/// class and intersection-over-union and keeps a stable numeric ID across frames.
/// </summary>
public sealed class IouMultiObjectTracker
{
    private readonly List<TrackState> _tracks = [];
    private readonly float _minimumIou;
    private readonly int _maximumMisses;
    private readonly float _smoothing;
    private int _nextId = 1;

    public IouMultiObjectTracker(float minimumIou = 0.25f, int maximumMisses = 8, float smoothing = 0.65f)
    {
        _minimumIou = minimumIou;
        _maximumMisses = maximumMisses;
        _smoothing = smoothing;
    }

    public IReadOnlyList<TrackedObject> Update(IReadOnlyList<ObjectDetection> detections)
    {
        var candidates = new List<(int Track, int Detection, float Iou)>();
        for (var trackIndex = 0; trackIndex < _tracks.Count; trackIndex++)
        {
            for (var detectionIndex = 0; detectionIndex < detections.Count; detectionIndex++)
            {
                if (!string.Equals(_tracks[trackIndex].Label, detections[detectionIndex].Label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var iou = IntersectionOverUnion(_tracks[trackIndex].Box, detections[detectionIndex].Box);
                if (iou >= _minimumIou)
                {
                    candidates.Add((trackIndex, detectionIndex, iou));
                }
            }
        }

        var usedTracks = new HashSet<int>();
        var usedDetections = new HashSet<int>();
        foreach (var candidate in candidates.OrderByDescending(x => x.Iou))
        {
            if (!usedTracks.Add(candidate.Track) || !usedDetections.Add(candidate.Detection))
            {
                continue;
            }

            var detection = detections[candidate.Detection];
            var track = _tracks[candidate.Track];
            track.Box = Smooth(track.Box, detection.Box);
            track.Confidence = detection.Confidence;
            track.Misses = 0;
        }

        for (var index = 0; index < _tracks.Count; index++)
        {
            if (!usedTracks.Contains(index))
            {
                _tracks[index].Misses++;
            }
        }

        foreach (var (detection, index) in detections.Select((value, index) => (value, index)))
        {
            if (!usedDetections.Contains(index))
            {
                _tracks.Add(new TrackState(_nextId++, detection.Box, detection.Label, detection.Confidence));
            }
        }

        _tracks.RemoveAll(track => track.Misses > _maximumMisses);
        return Current;
    }

    public IReadOnlyList<TrackedObject> Current => _tracks
        .Select(track => new TrackedObject(track.Id, track.Box, track.Label, track.Confidence))
        .ToArray();

    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
    }

    private Rect Smooth(Rect previous, Rect current)
    {
        var oldWeight = 1f - _smoothing;
        return new Rect(
            (int)MathF.Round(previous.X * oldWeight + current.X * _smoothing),
            (int)MathF.Round(previous.Y * oldWeight + current.Y * _smoothing),
            (int)MathF.Round(previous.Width * oldWeight + current.Width * _smoothing),
            (int)MathF.Round(previous.Height * oldWeight + current.Height * _smoothing));
    }

    private static float IntersectionOverUnion(Rect first, Rect second)
    {
        var intersection = first & second;
        if (intersection.Width <= 0 || intersection.Height <= 0)
        {
            return 0;
        }

        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = first.Width * first.Height + second.Width * second.Height - intersectionArea;
        return unionArea <= 0 ? 0 : (float)intersectionArea / unionArea;
    }

    private sealed class TrackState(int id, Rect box, string label, float confidence)
    {
        public int Id { get; } = id;
        public Rect Box { get; set; } = box;
        public string Label { get; } = label;
        public float Confidence { get; set; } = confidence;
        public int Misses { get; set; }
    }
}
