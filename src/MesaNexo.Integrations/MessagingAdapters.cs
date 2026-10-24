using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;
// How a message ended: sent with the provider's id, refused (nothing went out, safe to fix and repeat), worth trying again
// later (nothing went out yet) or uncertain (it may have gone out; never repeated by itself).
public sealed record SendResult(string Status, string ProviderId = "", string Message = "");
// WhatsApp Cloud API (Meta). Messages the business starts must use templates Meta approved; each event maps to one, with its
// body variables in a fixed order. Numbers go out in international format without "+": a 10-digit Mexican number gets 52.
public sealed class WhatsAppCloud(HttpClient http, string phoneNumberId, string accessToken, string apiVersion = "v23.0", string countryCode = "52") {
    public string Normalize(string phone) { var digits = new string(phone.Where(char.IsDigit).ToArray()); return digits.Length == 10 ? countryCode + digits : digits; }
    public async Task<SendResult> SendTemplate(string to, string template, string language, IReadOnlyList<string> parameters, CancellationToken ct = default) {

        var number = Normalize(to);
        Need(number.Length is >= 11 and <= 15, "Número de WhatsApp inválido");
        Need(Regex.IsMatch(template, "^[a-z0-9_]{1,512}$"), "Nombre de plantilla inválido");
        var components = new JsonArray();
        if (parameters.Count > 0) components.Add(J.O("type", "body", "parameters", new JsonArray(parameters.Select(p => (JsonNode)J.O("type", "text", "text", p.Length > 1000 ? p[..1000] : p)).ToArray())));
        var body = J.O("messaging_product", "whatsapp", "to", number, "type", "template", "template", J.O("name", template, "language", J.O("code", language), "components", components));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.facebook.com/" + apiVersion + "/" + Uri.EscapeDataString(phoneNumberId) + "/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); } catch (HttpRequestException e) when (e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError) { return new("retry", Message: "Sin conexión con WhatsApp"); }
        using (response) {

            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? data = null;
            try { data = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
            if (response.IsSuccessStatusCode && data.A("messages").Objects().FirstOrDefault()?.S("id") is { Length: > 0 } id) return new("sent", id, "Aceptado por WhatsApp");
            var error = data?["error"];
            var detail = error.S("message");
            // Meta's 4xx means the request was refused as sent (template, number, token); rate limits and 5xx may be retried.
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) return new("retry", Message: "WhatsApp no disponible (" + (int)response.StatusCode + ")");
            return new("failed", Message: "WhatsApp rechazó el mensaje: " + (detail == "" ? "HTTP " + (int)response.StatusCode : detail[..Math.Min(detail.Length, 200)]));

        }

    }
}
// Outgoing e-mail over SMTP with STARTTLS (port 587) or implicit TLS (465); the certificate is validated by the OS.
public sealed class SmtpMailer(string host, int port, string username, string password, string from, string fromName, bool implicitTls = false) {
    public async Task<SendResult> Send(string to, string subject, string text, CancellationToken ct = default) {

        Need(Regex.IsMatch(to, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") && to.Length <= 150, "Correo inválido");
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = text };
        using var client = new SmtpClient { Timeout = 20000 };
        // Until the message is handed over nothing has gone out, so a failure there can be retried.
        try { await client.ConnectAsync(host, port, implicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct); if (username != "") await client.AuthenticateAsync(username, password, ct); } catch (AuthenticationException) { return new("failed", Message: "El servidor de correo rechazó el usuario o la contraseña"); } catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or SslHandshakeException or TimeoutException or MailKit.ProtocolException) { return new("retry", Message: "Sin conexión con el servidor de correo"); }
        try { var answer = await client.SendAsync(message, ct); await client.DisconnectAsync(true, ct); return new("sent", message.MessageId, answer.Length > 200 ? answer[..200] : answer); } catch (SmtpCommandException e) when (e.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.MessageNotAccepted) { return new("failed", Message: "El servidor de correo rechazó el mensaje: " + e.Message); } catch (Exception) when (!ct.IsCancellationRequested) { return new("uncertain", Message: "El envío se interrumpió; revisa si llegó antes de repetirlo"); }

    }
}
