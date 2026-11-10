using Gaska.Intranet.Core.Messaging;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Gaska.Intranet.Infrastructure.Messaging;

internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning("E-mail sending disabled (Smtp:Enabled=false). To {To}: {Subject}\n{Body}",
                message.To, message.Subject, message.TextBody);
            return;
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        var security = Enum.TryParse<SecureSocketOptions>(_options.Security, ignoreCase: true, out var s)
            ? s
            : SecureSocketOptions.Auto;

        await client.ConnectAsync(_options.Host, _options.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(_options.UserName))
        {
            await client.AuthenticateAsync(_options.UserName, _options.Password ?? string.Empty, ct);
        }

        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);

        logger.LogInformation("E-mail '{Subject}' sent to {To}", message.Subject, message.To);
    }
}
