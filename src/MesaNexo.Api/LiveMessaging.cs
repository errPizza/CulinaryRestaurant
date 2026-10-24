using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// messaging.json (example in deploy/integrations/messaging.example.json):
//   "whatsapp": {phoneNumberId, accessToken, apiVersion, countryCode, templates: {event: {name, language}}}
//   "smtp": {host, port, username, password, from, fromName, implicitTls}
// Either may be left out. WhatsApp templates take the event's values in order (Restaurant.Message): dispatch = customer,
// restaurant, courier, time; pickup-ready = customer, restaurant; paylink = customer, restaurant, amount, link;
// owner-notice = restaurant, notice; test = restaurant.
public sealed class MessagingService(WhatsAppCloud? whatsapp,JsonObject templates,SmtpMailer? smtp)
{
    public static MessagingService From(JsonObject o)
    {
        var wa=o.Obj("whatsapp");var mail=o.Obj("smtp");Need(wa.Count>0||mail.Count>0,"Configura whatsapp, smtp o ambos");
        WhatsAppCloud? whatsapp=null;if(wa.Count>0){var version=wa.S("apiVersion");if(version=="")version="v23.0";Need(System.Text.RegularExpressions.Regex.IsMatch(version,@"^v\d+\.\d+$"),"whatsapp.apiVersion: por ejemplo v23.0");var country=wa.S("countryCode");whatsapp=new(Http(),Text(wa,"phoneNumberId",40),Text(wa,"accessToken",1000),version,country==""?"52":country);}
        SmtpMailer? smtp=null;if(mail.Count>0){var port=mail.I("port",587);Need(port is >0 and <65536,"smtp.port inválido");smtp=new(Text(mail,"host",200),port,mail.S("username"),mail.S("password"),Text(mail,"from",150),mail.S("fromName"),mail.B("implicitTls"));}
        return new(whatsapp,(JsonObject)wa.Obj("templates").DeepClone(),smtp);
    }
    static HttpClient Http()=>new(new SocketsHttpHandler{AllowAutoRedirect=false,PooledConnectionLifetime=TimeSpan.FromMinutes(10)}){Timeout=TimeSpan.FromSeconds(20)};
    static string Subject(string kind,string restaurant)=>kind switch{"invoice"=>"Tu factura de "+restaurant,"owner-notice"=>"Aviso de "+restaurant,"paylink"=>"Liga de pago de "+restaurant,"test"=>"Mensaje de prueba de "+restaurant,_=>"Tu pedido de "+restaurant};
    public async Task<JobResult> Send(BranchState s,JsonObject m,CancellationToken ct)
    {
        var channel=m.S("channel");SendResult r;
        if(channel=="whatsapp"){
            if(whatsapp is null)return JobResult.Of("failed","WhatsApp sin configurar en messaging.json",fields:("message","WhatsApp sin configurar"));
            var template=templates.Obj(m.S("event"));if(template.S("name")=="")return JobResult.Of("failed","Sin plantilla de WhatsApp para «"+m.S("event")+"»",fields:("message","Falta whatsapp.templates."+m.S("event")+" en messaging.json"));
            var language=template.S("language");r=await whatsapp.SendTemplate(m.S("to"),template.S("name"),language==""?"es_MX":language,m.A("values").Strings(),ct);
        }else{
            if(smtp is null)return JobResult.Of("failed","Correo sin configurar en messaging.json",fields:("message","Correo sin configurar"));
            r=await smtp.Send(m.S("to"),Subject(m.S("event"),s.Meta.S("name")),m.S("body"),ct);
        }
        // Nothing went out: tried again in 1, 2, 4 and 8 minutes, then left as failed.
        if(r.Status=="retry"&&m.L("attempts")<5)return JobResult.Of("queued","",fields:[("retryAt",DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2,m.L("attempts")-1)).ToString("O")),("message",r.Message)]);
        return r.Status switch{
            "sent"=>JobResult.Of("sent","",r.ProviderId,("providerId",r.ProviderId),("sentAt",J.Now()),("message","")),
            "uncertain"=>JobResult.Of("uncertain",(channel=="whatsapp"?"WhatsApp":"Correo")+" a "+m.S("to")+": "+r.Message,"",("message",r.Message)),
            _=>JobResult.Of("failed",(channel=="whatsapp"?"WhatsApp":"Correo")+" a "+m.S("to")+": "+r.Message,"",("message",r.Message))};
    }
}
public static partial class LiveServices
{
    static bool Due(JsonObject x)=>!DateTimeOffset.TryParse(x.S("retryAt"),out var at)||at<=DateTimeOffset.UtcNow;
    static void Messaging(IServiceCollection services,IntegrationConfig config)
    {
        if(config.Use("messaging",MessagingService.From) is not {} messaging)return;
        Worker(services,"messaging",branch=>ct=>branch.SendNext("message","messaging-service",m=>m.S("status")=="queued"&&Due(m),messaging.Send,null,ct));
    }
}
