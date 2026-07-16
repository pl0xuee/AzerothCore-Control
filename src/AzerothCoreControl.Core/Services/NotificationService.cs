using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using AzerothCoreControl.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzerothCoreControl.Core.Services;

public enum NotificationSeverity { Info, Warning, Critical }

/// <summary>
/// Fans notifications out to the configured sinks: Windows toast (wired up by the WPF layer via
/// <see cref="ToastRequested"/>), a Discord webhook, and SMTP email. Crashes, breaker trips, and
/// update results flow through here.
/// </summary>
public sealed class NotificationService
{
    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public NotificationService(Func<AppSettings> settings, HttpClient? http = null, ILogger<NotificationService>? logger = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _log = logger ?? NullLogger<NotificationService>.Instance;
    }

    /// <summary>Raised for the UI layer to show a native Windows toast (Core has no UI dependency).</summary>
    public event Action<string, string, NotificationSeverity>? ToastRequested;

    public async Task NotifyAsync(string title, string message, NotificationSeverity severity = NotificationSeverity.Info, CancellationToken cancellationToken = default)
    {
        var cfg = _settings().Notifications;

        if (cfg.ToastEnabled)
        {
            try { ToastRequested?.Invoke(title, message, severity); }
            catch (Exception ex) { _log.LogWarning(ex, "Toast sink failed"); }
        }

        var tasks = new List<Task>();
        if (!string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl))
            tasks.Add(SendDiscordAsync(cfg.DiscordWebhookUrl, title, message, severity, cancellationToken));
        if (cfg.EmailEnabled)
            tasks.Add(SendEmailAsync(cfg, title, message, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendDiscordAsync(string webhook, string title, string message, NotificationSeverity severity, CancellationToken ct)
    {
        try
        {
            var color = severity switch
            {
                NotificationSeverity.Critical => 0xE74C3C,
                NotificationSeverity.Warning => 0xF39C12,
                _ => 0x2ECC71,
            };
            var payload = new
            {
                embeds = new[]
                {
                    new { title, description = message, color },
                },
            };
            using var resp = await _http.PostAsJsonAsync(webhook, payload, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Discord webhook returned {Status}", resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Discord notification failed");
        }
    }

    private async Task SendEmailAsync(NotificationSettings cfg, string title, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.SmtpHost) || string.IsNullOrWhiteSpace(cfg.EmailFrom) || string.IsNullOrWhiteSpace(cfg.EmailTo))
            return;
        try
        {
            using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
            {
                EnableSsl = cfg.SmtpUseSsl,
                Credentials = string.IsNullOrWhiteSpace(cfg.SmtpUsername)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(cfg.SmtpUsername, cfg.SmtpPassword),
            };
            using var mail = new MailMessage(cfg.EmailFrom!, cfg.EmailTo!, $"[AzerothCore] {title}", message);
            await client.SendMailAsync(mail, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Email notification failed");
        }
    }
}
