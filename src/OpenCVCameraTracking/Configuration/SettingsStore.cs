using System.IO;
using System.Text.Json;

namespace OpenCVCameraTracking.Configuration;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCVCameraTracking",
        "settings.json");

    private static string LegacySettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCvSharpCameraTracking",
        "settings.json");

    public static ApplicationSettings Load()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath : LegacySettingsPath;
            if (!File.Exists(path))
            {
                return new ApplicationSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<ApplicationSettings>(json, JsonOptions)
                ?? new ApplicationSettings();
            Normalize(settings);
            return settings;
        }
        catch (JsonException)
        {
            return new ApplicationSettings();
        }
        catch (IOException)
        {
            return new ApplicationSettings();
        }
    }

    public static void Save(ApplicationSettings settings)
    {
        Normalize(settings);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private static void Normalize(ApplicationSettings settings)
    {
        settings.Language = settings.Language is "en-US" ? "en-US" : "zh-CN";
        settings.FaceConfidence = Math.Clamp(settings.FaceConfidence, 0.3f, 0.95f);
        settings.AnimalConfidence = Math.Clamp(settings.AnimalConfidence, 0.15f, 0.9f);
        settings.Streams ??= [];
        settings.Streams = settings.Streams
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name) && !string.IsNullOrWhiteSpace(profile.Address))
            .GroupBy(profile => profile.Id)
            .Select(group => group.First())
            .ToList();
        if (settings.SelectedStreamId is not null && settings.Streams.All(x => x.Id != settings.SelectedStreamId))
        {
            settings.SelectedStreamId = null;
        }
    }
}
