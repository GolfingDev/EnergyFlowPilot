using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Sends a formatted email when the decision worker fails.
/// </summary>
public sealed class WorkerFailureEmailNotifier : IWorkerFailureNotifier
{
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly IUtcClock utcClock;
    private readonly ILogger<WorkerFailureEmailNotifier> logger;

    public WorkerFailureEmailNotifier(
        IControllerSettingStore controllerSettingStore,
        IUtcClock utcClock,
        ILogger<WorkerFailureEmailNotifier> logger)
    {
        this.controllerSettingStore = controllerSettingStore;
        this.utcClock = utcClock;
        this.logger = logger;
    }

    public async Task NotifyAsync(Exception exception, CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);

        if (!settings.Enabled)
        {
            return;
        }

        using var message = new MailMessage(settings.FromAddress, settings.ToAddress)
        {
            Subject = $"{settings.SubjectPrefix} Worker-Fehler",
            Body = CreateBody(exception),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        using var smtpClient = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.EnableSsl
        };

        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            smtpClient.Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword);
        }

        logger.LogInformation(
            "Worker-Fehlermail wird versendet. Empfaenger={Recipient}, Host={SmtpHost}, Port={SmtpPort}",
            settings.ToAddress,
            settings.SmtpHost,
            settings.SmtpPort);

        await smtpClient.SendMailAsync(message, cancellationToken);
    }

    private async Task<WorkerFailureEmailSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var enabled = await GetBooleanSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailEnabledKey,
            defaultValue: false,
            cancellationToken);

        var smtpHost = await GetRequiredStringSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailSmtpHostKey,
            cancellationToken);
        var smtpPort = await GetIntSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailSmtpPortKey,
            defaultValue: 25,
            cancellationToken);
        var smtpUsername = await GetOptionalStringSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailSmtpUsernameKey,
            cancellationToken);
        var smtpPassword = await GetOptionalStringSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailSmtpPasswordKey,
            cancellationToken);
        var fromAddress = await GetRequiredStringSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailFromAddressKey,
            cancellationToken);
        var toAddress = await GetRequiredStringSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailToAddressKey,
            cancellationToken);
        var enableSsl = await GetBooleanSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailEnableSslKey,
            defaultValue: false,
            cancellationToken);
        var subjectPrefix = await GetRequiredStringSettingAsync(
            ControllerSettingDefaults.WorkerFailureEmailSubjectPrefixKey,
            cancellationToken);

        return new WorkerFailureEmailSettings
        {
            Enabled = enabled,
            SmtpHost = smtpHost,
            SmtpPort = smtpPort,
            SmtpUsername = smtpUsername,
            SmtpPassword = smtpPassword,
            FromAddress = fromAddress,
            ToAddress = toAddress,
            EnableSsl = enableSsl,
            SubjectPrefix = subjectPrefix
        };
    }

    private async Task<string> GetRequiredStringSettingAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);

        if (setting is null || !setting.IsConfigured || string.IsNullOrWhiteSpace(setting.Value))
        {
            throw new InvalidOperationException($"Die Worker-Mail-Einstellung '{key}' ist nicht konfiguriert.");
        }

        return setting.Value.Trim();
    }

    private async Task<string?> GetOptionalStringSettingAsync(string key, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);
        return setting is null || !setting.IsConfigured || string.IsNullOrWhiteSpace(setting.Value)
            ? null
            : setting.Value.Trim();
    }

    private async Task<int> GetIntSettingAsync(string key, int defaultValue, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);

        if (setting is null || !setting.IsConfigured || string.IsNullOrWhiteSpace(setting.Value))
        {
            return defaultValue;
        }

        if (!int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"Die Worker-Mail-Einstellung '{key}' muss als Ganzzahl konfiguriert sein.");
        }

        return value;
    }

    private async Task<bool> GetBooleanSettingAsync(string key, bool defaultValue, CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(key, cancellationToken);

        if (setting is null || !setting.IsConfigured || string.IsNullOrWhiteSpace(setting.Value))
        {
            return defaultValue;
        }

        if (!bool.TryParse(setting.Value, out var value))
        {
            throw new InvalidOperationException($"Die Worker-Mail-Einstellung '{key}' muss true oder false sein.");
        }

        return value;
    }

    private string CreateBody(Exception exception)
    {
        var body = new StringBuilder();
        body.AppendLine("Der Decision-Worker hat einen Fehler ausgelost.");
        body.AppendLine();
        body.AppendLine($"Zeitpunkt (UTC): {utcClock.UtcNow:O}");
        body.AppendLine($"Maschine: {Environment.MachineName}");
        body.AppendLine($"Exception-Typ: {exception.GetType().FullName}");
        body.AppendLine($"Nachricht: {exception.Message}");
        body.AppendLine();
        body.AppendLine("StackTrace:");
        body.AppendLine(exception.ToString());

        return body.ToString();
    }

    private sealed class WorkerFailureEmailSettings
    {
        public bool Enabled { get; init; }

        public string SmtpHost { get; init; } = string.Empty;

        public int SmtpPort { get; init; }

        public string? SmtpUsername { get; init; }

        public string? SmtpPassword { get; init; }

        public string FromAddress { get; init; } = string.Empty;

        public string ToAddress { get; init; } = string.Empty;

        public bool EnableSsl { get; init; }

        public string SubjectPrefix { get; init; } = string.Empty;
    }
}
