using System.IO;
using System.Text.Json;
using OpenCVCameraTracking.Core.Notifications;
using OpenCVCameraTracking.Localization;
using OpenCVCameraTracking.Themes;

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
        ApplyLocalizedDefaults(settings);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static void ApplyLocalizedDefaults(ApplicationSettings settings)
    {
        if (settings.NotificationChannels is null)
        {
            return;
        }

        foreach (var channel in settings.NotificationChannels)
        {
            if (string.IsNullOrWhiteSpace(channel.Name))
            {
                channel.Name = LocalizationManager.Get(GetDefaultNotificationNameResourceKey(channel.Kind));
            }
        }
    }

    private static void Normalize(ApplicationSettings settings)
    {
        settings.Language = settings.Language is "en-US" ? "en-US" : "zh-CN";
        settings.ThemeMode = ThemeManager.Normalize(settings.ThemeMode);
        settings.SelectedStreamVariant = settings.SelectedStreamVariant is "Sub" ? "Sub" : "Main";
        settings.FaceConfidence = Math.Clamp(settings.FaceConfidence, 0.3f, 0.95f);
        settings.AnimalConfidence = Math.Clamp(settings.AnimalConfidence, 0.15f, 0.9f);
        if (settings.RestrictedZone is not null && !settings.RestrictedZone.IsValid)
        {
            settings.RestrictedZone = null;
        }
        settings.Streams ??= [];
        settings.CameraDevices ??= [];
        settings.LayoutStreamIds ??= [];
        settings.MultiPreviewSourceKeys ??= [];
        settings.NotificationChannels ??= [];
        settings.NotificationChannels = settings.NotificationChannels
            .Where(channel => channel is not null)
            .Select(channel =>
            {
                channel.Id = string.IsNullOrWhiteSpace(channel.Id) ? Guid.NewGuid().ToString("N") : channel.Id;
                if (!Enum.IsDefined(channel.Kind))
                {
                    channel.Kind = NotificationChannelKind.ServerChan;
                }
                channel.EventKeys ??= NotificationEventCatalog.DefaultKeys.ToList();
                channel.EventKeys = channel.EventKeys
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                channel.Name = channel.Name?.Trim() ?? string.Empty;
                channel.SmtpPort = channel.SmtpPort is < 1 or > 65535 ? 587 : channel.SmtpPort;
                return channel;
            })
            .GroupBy(channel => channel.Id)
            .Select(group => group.First())
            .ToList();
        settings.Streams = settings.Streams
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name) && !string.IsNullOrWhiteSpace(profile.Address))
            .GroupBy(profile => profile.Id)
            .Select(group => group.First())
            .ToList();
        settings.CameraDevices = settings.CameraDevices
            .GroupBy(device => device.DeviceIndex)
            .Select(group => group.Last())
            .ToList();
        if (settings.SelectedStreamId is not null && settings.Streams.All(x => x.Id != settings.SelectedStreamId))
        {
            settings.SelectedStreamId = null;
        }
    }

    private static string GetDefaultNotificationNameResourceKey(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.ServerChan => "NotificationTypeServerChan",
        NotificationChannelKind.Feishu => "NotificationTypeFeishu",
        NotificationChannelKind.Email => "NotificationTypeEmail",
        NotificationChannelKind.Telegram => "NotificationTypeTelegram",
        _ => "NotificationDefaultName"
    };
}
