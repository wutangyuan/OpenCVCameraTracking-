using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OpenCVCameraTracking.Core.Notifications;
using OpenCVCameraTracking.Localization;

namespace OpenCVCameraTracking;

public partial class NotificationEditorWindow : Window
{
    private readonly Func<NotificationChannelSettings, Task<NotificationSendResult>>? _testNotification;
    private bool _isLoading;

    public NotificationEditorWindow(
        NotificationChannelSettings channel,
        Func<NotificationChannelSettings, Task<NotificationSendResult>>? testNotification = null)
    {
        _isLoading = true;
        InitializeComponent();
        Result = channel.DeepClone();
        _testNotification = testNotification;
        NameBox.Text = Result.Name;
        EnabledBox.IsChecked = Result.Enabled;
        ServerChanSendKeyBox.Text = Result.ServerChanSendKey;
        FeishuWebhookUrlBox.Text = Result.FeishuWebhookUrl;
        FeishuSecretBox.Text = Result.FeishuSecret;
        SmtpHostBox.Text = Result.SmtpHost;
        SmtpPortBox.Text = Result.SmtpPort.ToString();
        SmtpUseSslBox.IsChecked = Result.SmtpUseSsl;
        SmtpUsernameBox.Text = Result.SmtpUsername;
        SmtpPasswordBox.Password = Result.SmtpPassword;
        MailFromBox.Text = Result.MailFrom;
        MailToBox.Text = Result.MailTo;
        TelegramBotTokenBox.Text = Result.TelegramBotToken;
        TelegramChatIdBox.Text = Result.TelegramChatId;
        SelectComboTag(ChannelTypeBox, Result.Kind.ToString());
        _isLoading = false;
        UpdateProviderPanel();
        BuildNotificationEventSelector();
        TestButton.IsEnabled = _testNotification is not null;
    }

    public NotificationChannelSettings Result { get; }

    private void ChannelTypeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            UpdateProviderPanel();
        }
    }

    private void UpdateProviderPanel()
    {
        var kind = GetSelectedKind();
        ServerChanPanel.Visibility = kind == NotificationChannelKind.ServerChan ? Visibility.Visible : Visibility.Collapsed;
        FeishuPanel.Visibility = kind == NotificationChannelKind.Feishu ? Visibility.Visible : Visibility.Collapsed;
        EmailPanel.Visibility = kind == NotificationChannelKind.Email ? Visibility.Visible : Visibility.Collapsed;
        TelegramPanel.Visibility = kind == NotificationChannelKind.Telegram ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildNotificationEventSelector()
    {
        NotificationEventsPanel.Children.Clear();
        foreach (var definition in NotificationEventCatalog.Definitions)
        {
            var toggle = new ToggleButton
            {
                Content = LocalizationManager.GetNotificationEventName(definition.Key),
                Tag = definition.Key,
                IsChecked = Result.EventKeys.Contains(definition.Key, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 6, 12, 6),
                MinWidth = 100
            };
            toggle.Checked += NotificationEventToggle_OnChanged;
            toggle.Unchecked += NotificationEventToggle_OnChanged;
            NotificationEventsPanel.Children.Add(toggle);
        }

        UpdateNotificationEventSummary();
    }

    private void NotificationEventToggle_OnChanged(object sender, RoutedEventArgs e) =>
        UpdateNotificationEventSummary();

    private void UpdateNotificationEventSummary()
    {
        var selectedCount = NotificationEventsPanel.Children
            .OfType<ToggleButton>()
            .Count(toggle => toggle.IsChecked == true);
        NotificationEventsSummaryText.Text = LocalizationManager.Format(
            "NotificationEventSelectionSummary",
            selectedCount,
            NotificationEventCatalog.Definitions.Count);
    }

    private void SelectAllNotificationEventsButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var toggle in NotificationEventsPanel.Children.OfType<ToggleButton>())
        {
            toggle.IsChecked = true;
        }

        UpdateNotificationEventSummary();
    }

    private async void TestButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_testNotification is null || !TryBuildChannel(out var channel, true))
        {
            return;
        }

        TestButton.IsEnabled = false;
        TestStatusText.Text = LocalizationManager.Get("NotificationTesting");
        try
        {
            var result = await _testNotification(channel);
            var detail = LocalizationManager.GetNotificationResultMessage(result);
            TestStatusText.Text = result.Success
                ? LocalizationManager.Format("NotificationTestSuccess", detail)
                : LocalizationManager.Format("NotificationTestFailed", detail);
        TestStatusText.Foreground = Application.Current.TryFindResource(
            result.Success ? "AccentHighlightBrush" : "WarningBrush") as Brush;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildChannel(out var channel, true))
        {
            return;
        }

        CopyChannel(channel, Result);
        DialogResult = true;
    }

    private bool TryBuildChannel(out NotificationChannelSettings channel, bool validate)
    {
        channel = Result.DeepClone();
        channel.Name = NameBox.Text.Trim();
        channel.Enabled = EnabledBox.IsChecked == true;
        channel.Kind = GetSelectedKind();
        channel.EventKeys = NotificationEventsPanel.Children
            .OfType<ToggleButton>()
            .Where(toggle => toggle.IsChecked == true && toggle.Tag is string)
            .Select(toggle => (string)toggle.Tag)
            .ToList();
        channel.ServerChanSendKey = ServerChanSendKeyBox.Text.Trim();
        channel.FeishuWebhookUrl = FeishuWebhookUrlBox.Text.Trim();
        channel.FeishuSecret = FeishuSecretBox.Text.Trim();
        channel.SmtpHost = SmtpHostBox.Text.Trim();
        channel.SmtpPort = int.TryParse(SmtpPortBox.Text.Trim(), out var port) ? port : 0;
        channel.SmtpUseSsl = SmtpUseSslBox.IsChecked == true;
        channel.SmtpUsername = SmtpUsernameBox.Text.Trim();
        channel.SmtpPassword = SmtpPasswordBox.Password;
        channel.MailFrom = MailFromBox.Text.Trim();
        channel.MailTo = MailToBox.Text.Trim();
        channel.TelegramBotToken = TelegramBotTokenBox.Text.Trim();
        channel.TelegramChatId = TelegramChatIdBox.Text.Trim();

        if (!validate)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(channel.Name))
        {
            ShowInformation("NotificationNameRequired");
            return false;
        }

        if (!channel.Enabled)
        {
            return true;
        }

        var missing = channel.Kind switch
        {
            NotificationChannelKind.ServerChan => string.IsNullOrWhiteSpace(channel.ServerChanSendKey)
                ? "ServerChanSendKey"
                : null,
            NotificationChannelKind.Feishu => string.IsNullOrWhiteSpace(channel.FeishuWebhookUrl)
                ? "FeishuWebhookUrl"
                : null,
            NotificationChannelKind.Email => string.IsNullOrWhiteSpace(channel.SmtpHost) ||
                                              channel.SmtpPort is < 1 or > 65535 ||
                                              string.IsNullOrWhiteSpace(channel.MailTo) ||
                                              (string.IsNullOrWhiteSpace(channel.MailFrom) && string.IsNullOrWhiteSpace(channel.SmtpUsername))
                ? "EmailConfig"
                : null,
            NotificationChannelKind.Telegram => string.IsNullOrWhiteSpace(channel.TelegramBotToken) ||
                                                string.IsNullOrWhiteSpace(channel.TelegramChatId)
                ? "TelegramConfig"
                : null,
            _ => "NotificationConfigRequired"
        };
        if (missing is not null)
        {
            ShowInformation(missing);
            return false;
        }

        if (channel.Kind == NotificationChannelKind.Feishu &&
            (!Uri.TryCreate(channel.FeishuWebhookUrl, UriKind.Absolute, out var webhookUri) ||
             webhookUri.Scheme is not ("http" or "https")))
        {
            ShowInformation("FeishuWebhookInvalid");
            return false;
        }

        return true;
    }

    private NotificationChannelKind GetSelectedKind() =>
        Enum.TryParse<NotificationChannelKind>(
            (ChannelTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var kind)
            ? kind
            : NotificationChannelKind.ServerChan;

    private static void CopyChannel(NotificationChannelSettings source, NotificationChannelSettings target)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.Enabled = source.Enabled;
        target.Kind = source.Kind;
        target.EventKeys = source.EventKeys.ToList();
        target.ServerChanSendKey = source.ServerChanSendKey;
        target.FeishuWebhookUrl = source.FeishuWebhookUrl;
        target.FeishuSecret = source.FeishuSecret;
        target.SmtpHost = source.SmtpHost;
        target.SmtpPort = source.SmtpPort;
        target.SmtpUseSsl = source.SmtpUseSsl;
        target.SmtpUsername = source.SmtpUsername;
        target.SmtpPassword = source.SmtpPassword;
        target.MailFrom = source.MailFrom;
        target.MailTo = source.MailTo;
        target.TelegramBotToken = source.TelegramBotToken;
        target.TelegramChatId = source.TelegramChatId;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowInformation(string resourceKey) =>
        MessageBox.Show(this, LocalizationManager.Get(resourceKey), LocalizationManager.Get("Information"),
            MessageBoxButton.OK, MessageBoxImage.Information);

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedIndex = comboBox.SelectedIndex < 0 ? 0 : comboBox.SelectedIndex;
    }
}
