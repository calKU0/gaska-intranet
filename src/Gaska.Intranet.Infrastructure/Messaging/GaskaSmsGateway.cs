using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Gaska.Intranet.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Intranet.Infrastructure.Messaging;

/// <summary>
/// Client of the company SMS gateway (a SOAP web service; its address is in Sms:Endpoint).
/// Instead of a generated WCF proxy it posts a plain SOAP 1.1 envelope – fewer dependencies.
/// </summary>
internal sealed class GaskaSmsGateway(
    HttpClient http,
    IOptions<SmsOptions> options,
    ILogger<GaskaSmsGateway> logger) : ISmsSender
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Tempuri = "http://tempuri.org/";
    private readonly SmsOptions _options = options.Value;

    public async Task SendAsync(string phoneNumber, string message, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning("SMS sending disabled (Sms:Enabled=false). To {Phone}: {Message}", phoneNumber, message);
            return;
        }

        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <SendSingleSMS xmlns="http://tempuri.org/">
                  <phoneNumber>{SecurityElement.Escape(phoneNumber)}</phoneNumber>
                  <message>{SecurityElement.Escape(message)}</message>
                </SendSingleSMS>
              </soap:Body>
            </soap:Envelope>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", "\"http://tempuri.org/SendSingleSMS\"");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The SMS gateway returned HTTP {(int)response.StatusCode}.");
        }

        var result = XDocument.Parse(body)
            .Descendants(Tempuri + "SendSingleSMSResult")
            .FirstOrDefault()?.Value;

        if (!string.Equals(result, _options.SuccessResponse, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SMS not sent: {result ?? "no response"}.");
        }

        logger.LogInformation("SMS sent to {Phone}", phoneNumber);
    }
}
