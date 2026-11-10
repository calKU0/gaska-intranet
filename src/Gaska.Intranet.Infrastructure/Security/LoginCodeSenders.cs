using System.Net;
using Gaska.Intranet.Core.Messaging;
using Gaska.Intranet.Core.Organization;
using Gaska.Intranet.Core.Security;
using Gaska.Intranet.Core.Text;

namespace Gaska.Intranet.Infrastructure.Security;

internal sealed class SmsLoginCodeSender(ISmsSender sms) : ILoginCodeSender
{
    public DeliveryChannel Channel => DeliveryChannel.Sms;

    public string? GetDestination(DirectoryUser user) => ContactFormatter.NormalizePhoneForSms(user.MobilePhone);

    public string MaskDestination(string destination) => ContactFormatter.MaskPhone(destination);

    public Task SendAsync(string destination, string code, TimeSpan validFor, CancellationToken ct = default) =>
        sms.SendAsync(destination, $"Kod logowania do Intranetu Gaska: {code}. Ważny {validFor.TotalMinutes:0} min.", ct);
}

internal sealed class EmailLoginCodeSender(IEmailSender email) : ILoginCodeSender
{
    public DeliveryChannel Channel => DeliveryChannel.Email;

    public string? GetDestination(DirectoryUser user) =>
        string.IsNullOrWhiteSpace(user.Email) || !user.Email.Contains('@') ? null : user.Email.Trim();

    public string MaskDestination(string destination) => ContactFormatter.MaskEmail(destination);

    public Task SendAsync(string destination, string code, TimeSpan validFor, CancellationToken ct = default)
    {
        var minutes = validFor.TotalMinutes.ToString("0");
        var html = $$"""
            <!DOCTYPE html>
            <html lang="pl">
            <body style="margin:0;padding:24px;background:#f4f4f4;font-family:Segoe UI,Arial,sans-serif;color:#2b2b2b">
              <table role="presentation" width="100%" style="max-width:480px;margin:0 auto;background:#ffffff;border-radius:8px;overflow:hidden">
                <tr><td style="background:#2b2b2b;padding:16px 24px;color:#ffffff;font-size:18px;font-weight:600">
                  <span style="color:#f5b400">■</span> Gąska Intranet
                </td></tr>
                <tr><td style="padding:24px">
                  <p style="margin:0 0 16px">Twój jednorazowy kod logowania:</p>
                  <p style="margin:0 0 16px;font-size:32px;letter-spacing:8px;font-weight:700;color:#2b2b2b">{{WebUtility.HtmlEncode(code)}}</p>
                  <p style="margin:0;color:#6b6b6b;font-size:13px">Kod jest ważny przez {{minutes}} min. Jeśli to nie Ty próbujesz się zalogować, zgłoś to do działu IT.</p>
                </td></tr>
              </table>
            </body>
            </html>
            """;

        var text = $"Twój jednorazowy kod logowania do Intranetu Gaska: {code}\nKod jest ważny przez {minutes} min.";

        return email.SendAsync(new EmailMessage(destination, $"Kod logowania: {code}", html, text), ct);
    }
}
