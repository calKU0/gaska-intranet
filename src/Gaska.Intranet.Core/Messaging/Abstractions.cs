namespace Gaska.Intranet.Core.Messaging;

public interface ISmsSender
{
    /// <param name="phoneNumber">Numer w formacie bramki (np. "600123456").</param>
    Task SendAsync(string phoneNumber, string message, CancellationToken ct = default);
}

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
