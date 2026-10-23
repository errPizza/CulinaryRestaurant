using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Stage 4: simulated third parties. Each simulator drives the same records and states a real adapter would, but nothing leaves the branch:
// no network, no money and no fiscal validity. Live mode stays locked until there is a contract, server-side credentials and explicit authorization.
public sealed partial class Restaurant
{
    public static readonly string[] IntegrationKeys=["pac","terminal","onlinepay","messaging","aggregators","pms","maps","callerid","printer"];
    static readonly Dictionary<string,string> Platforms=new(){["uber"]="UBER EATS",["rappi"]="RAPPI",["didi"]="DIDI FOOD"};
    // Test cards of the simulated PIN pad: the outcome picks brand and last four digits. Card data never enters the branch.
    static readonly Dictionary<string,(string Brand,string Last4)> TestCards=new(){["approve"]=("Visa","4242"),["decline"]=("Mastercard","0002"),["timeout"]=("Visa","0119")};
    static string Code(int digits)=>RandomNumberGenerator.GetInt32((int)Math.Pow(10,digits-1),(int)Math.Pow(10,digits)).ToString(CultureInfo.InvariantCulture);
    static string Ref(string prefix)=>prefix+"-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
    static string Pesos(long cents)=>"$"+(cents/100m).ToString("#,0.00",CultureInfo.InvariantCulture);
    static string Digits(string value)=>new(value.Where(char.IsDigit).ToArray());
    static string Plain(string value)=>new string(value.Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    // Hotels verify the surname: any word after the first name of the registered guest must appear in what the waiter typed.
    static bool SameGuest(string registered,string given){var words=Plain(given).Split(' ',StringSplitOptions.RemoveEmptyEntries);var names=Plain(registered).Split(' ',StringSplitOptions.RemoveEmptyEntries);return names.Skip(names.Length>1?1:0).Where(w=>w.Length>=3).Any(words.Contains);}
    static string Pick(JsonObject p,string key,string fallback,params string[] choices){var value=p.S(key);if(value=="")value=fallback;Need(choices.Contains(value),key+": valor inválido");return value;}
    JsonObject? Simulated(BranchState s,string key)=>s.Related("integration","id",key).FirstOrDefault(x=>x.S("mode")=="simulated");
    JsonObject Simulator(BranchState s,string key,string name)=>(Simulated(s,key)??throw new BusinessException(400,"Activa la simulación de "+name+" en Integraciones")).Obj("settings");
    JsonObject Log(BranchState s,JsonObject u,string provider,string operation,string status,string summary,string reference="")=>Add(s,"integrationlog",J.O("provider",provider,"operation",operation,"status",status,"summary",Sanitize(summary,200),"reference",reference,"actor",u.S("id"),"at",J.Now(),"simulated",true));
    // Simulated WhatsApp and e-mail: one message per event, reference and channel, stored as sent without contacting anyone.
    JsonObject? Message(BranchState s,string channel,string to,string kind,string refId,string body)
    {
        var config=Simulated(s,"messaging")?.Obj("settings");to=to.Trim();if(config is null||!config.B(channel)||to=="")return null;
        var prior=s.Related("message","event",kind).FirstOrDefault(x=>x.S("refId")==refId&&x.S("channel")==channel);if(prior is not null)return prior;
        return Add(s,"message",J.O("channel",channel,"to",to,"event",kind,"refId",refId,"body",Sanitize(body,600),"status","simulated-sent","providerId",Ref(channel=="whatsapp"?"WAMID":"MSG"),"at",J.Now()));
    }
    JsonObject IntegrationSettings(BranchState s,string key,JsonObject p)
    {
        switch(key){
        case "pac":{
            var rfc=p.S("issuerRfc","EKU9003173C9").Trim().ToUpperInvariant();if(rfc=="")rfc="EKU9003173C9";Need(Regex.IsMatch(rfc,@"^[A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3}$"),"RFC emisor inválido");
            var series=p.S("series","A").Trim().ToUpperInvariant();if(series=="")series="A";Need(Regex.IsMatch(series,"^[A-Z0-9]{1,10}$"),"Serie de 1 a 10 letras o números");
            var name=Sanitize(p.S("issuerName"),250);return J.O("issuerRfc",rfc,"issuerName",name==""?s.Meta.S("name"):name,"series",series,"autoStamp",p.B("autoStamp"));}
        case "terminal":{var id=Sanitize(p.S("terminalId"),20);return J.O("terminalId",id==""?"SIM-01":id);}
        case "onlinepay":return J.O("provider",Pick(p,"provider","stripe","stripe","mercadopago"),"expiresMinutes",p.ContainsKey("expiresMinutes")?Amount(p,"expiresMinutes",5,1440):60);
        case "messaging":{
            var email=p.S("ownerEmail").Trim();Need(email==""||Regex.IsMatch(email,@"^[^@\s]+@[^@\s]+\.[^@\s]+$")&&email.Length<=150,"Correo del dueño inválido");var phone=Digits(p.S("ownerPhone"));Need(phone==""||phone.Length is >=10 and <=15,"Teléfono del dueño inválido");
            return J.O("whatsapp",p.B("whatsapp",true),"email",p.B("email",true),"ownerEmail",email,"ownerPhone",phone);}
        case "aggregators":{
            var platforms=p.ContainsKey("platforms")?p.A("platforms").Strings():Platforms.Keys.ToArray();Need(platforms.Length>0&&platforms.All(Platforms.ContainsKey)&&platforms.Distinct().Count()==platforms.Length,"Elige al menos una plataforma válida");
            return J.O("platforms",J.N(platforms),"commissionBps",p.ContainsKey("commissionBps")?Amount(p,"commissionBps",0,5000):2500,"autoAccept",p.B("autoAccept"));}
        case "pms":{
            var rooms=p.A("rooms");Need(rooms.Count<=100&&rooms.Objects().Count()==rooms.Count,"Máximo 100 habitaciones");var clean=rooms.Objects().Select(r=>J.O("room",Text(r,"room",10).ToUpperInvariant(),"guest",Text(r,"guest",100),"limit",Amount(r,"limit"))).ToArray();Need(clean.Select(x=>x.S("room")).Distinct().Count()==clean.Length,"Habitación repetida");
            return J.O("system",Pick(p,"system","opera","opera","cloudbeds"),"rooms",J.N(clean));}
        case "maps":return J.O("centerLat",p.ContainsKey("centerLat")?Coordinate(p,"centerLat",90):19.4326m,"centerLon",p.ContainsKey("centerLon")?Coordinate(p,"centerLon",180):-99.1332m,"radiusKm",p.ContainsKey("radiusKm")?Amount(p,"radiusKm",1,30):5);
        default:return new();
        }
    }
    // Deterministic stand-in for a geocoder: the same address always lands on the same point inside the configured radius.
    JsonObject Geocode(BranchState s,string text)
    {
        var config=Simulator(s,"maps","mapas");var h=SHA256.HashData(Encoding.UTF8.GetBytes(Plain(text).Trim()));var lat0=(double)Coordinate(config,"centerLat",90);var lon0=(double)Coordinate(config,"centerLon",180);
        var distance=Math.Max(0.3,Math.Sqrt(BitConverter.ToUInt16(h,0)/65535.0)*config.L("radiusKm",5));var angle=BitConverter.ToUInt16(h,2)/65535.0*2*Math.PI;
        var lat=lat0+distance*Math.Cos(angle)/111.32;var lon=lon0+distance*Math.Sin(angle)/(111.32*Math.Cos(lat0*Math.PI/180));
        return J.O("lat",Math.Round((decimal)lat,6),"lon",Math.Round((decimal)lon,6),"distanceKm",Math.Round((decimal)distance,1),"etaMinutes",Eta(distance),"source","simulated-geocoder");
    }
    // City traffic estimate: 20 km/h plus five minutes to hand over the order.
    static long Eta(double km)=>(long)Math.Ceiling(km/20*60)+5;
    static double Distance(double lat1,double lon1,double lat2,double lon2){var r=Math.PI/180;var a=Math.Pow(Math.Sin((lat2-lat1)*r/2),2)+Math.Cos(lat1*r)*Math.Cos(lat2*r)*Math.Pow(Math.Sin((lon2-lon1)*r/2),2);return 6371*2*Math.Atan2(Math.Sqrt(a),Math.Sqrt(1-a));}
    JsonObject StampSimulated(BranchState s,JsonObject u,JsonObject f)
    {
        var config=Simulator(s,"pac","facturación (PAC)");Need(f.S("status")=="pending","Sólo una solicitud pendiente se puede timbrar");var r=f.Obj("receiver");var generic=r.S("rfc")=="XAXX010101000";
        // Same receiver rule the SAT applies to CFDI 4.0: the generic RFC only goes with regime 616 and use S01, and regime 616 only with it.
        if(generic!=(r.S("regime")=="616")||generic&&r.S("use")!="S01"){f["status"]="simulated-rejected";f["message"]="SIMULADO · CFDI40144: el RFC genérico XAXX010101000 exige régimen 616 y uso S01, y el régimen 616 sólo se usa con ese RFC. Crea una solicitud nueva con los datos corregidos";f["environment"]="simulated";Log(s,u,"pac","stamp","rejected","Rechazo de validación para "+r.S("rfc"));return f;}
        s.Meta["simulatedFolio"]=s.Meta.L("simulatedFolio")+1;var uuid=Guid.NewGuid().ToString().ToUpperInvariant();var totals=f.Obj("totals");
        f["status"]="simulated-stamped";f["fiscalUuid"]=uuid;f["stampedAt"]=J.Now();f["environment"]="simulated";f["message"]="SIMULADO · sin validez fiscal. Timbre del PAC simulado";
        f["cfdi"]=J.O("series",config.S("series"),"folio",s.Meta.L("simulatedFolio"),"issuerRfc",config.S("issuerRfc"),"issuerName",config.S("issuerName"),"receiverRfc",r.S("rfc"),"receiverName",r.S("name"),"total",totals.L("total"),"tax",totals.L("tax"),"seal",J.Hash(uuid+J.Canonical(totals))[..44],"certificate","00001000000SIMULADO000");
        Log(s,u,"pac","stamp","stamped","CFDI "+config.S("series")+"-"+s.Meta.L("simulatedFolio")+" para "+r.S("rfc")+" · "+Pesos(totals.L("total")),uuid);
        if(r.S("email")!="")Message(s,"email",r.S("email"),"invoice",f.S("id"),"Tu factura de "+s.Meta.S("name")+" por "+Pesos(totals.L("total"))+" quedó timbrada. UUID "+uuid+". SIMULADA: no tiene validez fiscal.");
        return f;
    }
    void ApproveTerminal(BranchState s,JsonObject u,JsonObject txn)
    {
        var pay=Act(s,u,"payment.add",J.O("visitId",txn.S("visitId"),"amount",txn.L("amount"),"tip",txn.L("tip"),"method","card","folioId",txn.S("folioId")));var auth=Code(6);
        s.Get("payment",pay.S("id"))["terminal"]=J.O("terminalId",txn.S("terminalId"),"brand",txn.S("brand"),"last4",txn.S("last4"),"authCode",auth,"reference",txn.S("reference"),"simulated",true);
        txn["status"]="approved";txn["authCode"]=auth;txn["paymentId"]=pay.S("id");txn["message"]="Aprobada · autorización "+auth;
    }
    void AcceptAggregator(BranchState s,JsonObject u,JsonObject order)
    {
        Need(order.S("status")=="pending","Pedido de plataforma ya resuelto");var config=Simulator(s,"aggregators","plataformas de reparto");
        var visit=Act(s,u,"visit.quick",J.O("customerName",order.S("platformName")+" · "+order.S("customerName")));var v=s.Get("visit",visit.S("id"));v["channel"]="aggregator";v["platform"]=order.S("platform");v["priceListId"]="base";v["aggregatorOrderId"]=order.S("id");
        Act(s,u,"command.send",J.O("visitId",v.S("id"),"items",new JsonArray(order.A("items").Objects().Select((x,i)=>(JsonNode)J.O("productId",x.S("productId"),"quantity",x.L("quantity"),"notes",i==0?order.S("notes"):"")).ToArray())));
        // The platform already charged the customer; the branch records what it will be paid, priced locally, minus the commission.
        var total=Totals(s,v).L("total");var commission=(long)Math.Round(total*config.L("commissionBps")/10000m,MidpointRounding.AwayFromZero);
        if(total>0){var pay=InternalPayment(s,u,v,total,"platform");pay["platform"]=order.S("platform");pay["reference"]=order.S("externalId");}
        v["prepaid"]=true;order["status"]="accepted";order["visitId"]=v.S("id");order["total"]=total;order["commission"]=commission;order["net"]=total-commission;order["acceptedBy"]=u.S("id");order["acceptedAt"]=J.Now();
        Log(s,u,"aggregators","order.accept","accepted",order.S("platformName")+" "+order.S("externalId")+" · "+Pesos(total),order.S("externalId"));
    }
    static string OwnerText(BranchState s,JsonObject n)=>n.S("type") switch{
        "cash-limit"=>"El efectivo en caja llegó a "+Pesos(n.L("amount"))+" y superó el límite de "+Pesos(n.L("limit"))+". Conviene hacer un retiro.",
        "blind-cash-closed"=>"Se cerró un turno con arqueo ciego. Revisa la diferencia en Caja.",
        "courier-reconciliation"=>"Se concilió el cobro de un reparto a domicilio.",
        _=>"Aviso de la sucursal: "+n.S("type")};
    bool TryIntegrations(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);var id=p.S("id");
        switch(type){
        case "integration.configure":{
            Role(u,"manager");var key=Choice(p,"provider",IntegrationKeys);var mode=p.S("mode");
            Need(mode!="live","La conexión real necesita contrato con el proveedor, credenciales guardadas en el servidor y tu autorización expresa; por ahora sólo está disponible la simulación",403);Need(mode is "simulated" or "off","Modo inválido");
            var row=s.Related("integration","id",key).FirstOrDefault()??Add(s,"integration",J.O("id",key,"key",key,"since",J.Now()));
            row["settings"]=p["settings"] is JsonObject given?IntegrationSettings(s,key,given):row["settings"] is JsonObject prior&&prior.Count>0?prior.DeepClone():IntegrationSettings(s,key,new());
            row["mode"]=mode;row["updatedBy"]=u.S("id");row["updatedAt"]=J.Now();Log(s,u,key,"configure",mode,mode=="simulated"?"Simulación activa":"Integración apagada");result=row;break;}
        case "pac.stamp":Role(u,"manager");result=StampSimulated(s,u,s.Get("fiscal",id));break;
        case "terminal.charge":{
            Role(u,"mesero","manager");Need(u.S("role")=="manager"||u.Obj("permissions").B("payments",true),"Permiso de pagos deshabilitado",403);var config=Simulator(s,"terminal","la terminal bancaria");var v=Own(s,u,p.S("visitId"));
            var amount=Amount(p,"amount",1);var tip=Amount(p,"tip");Need(amount<=Totals(s,v).L("balance"),"El pago supera el saldo");Need(s.Related("cash","status","open").Any(),"Abre una caja antes de registrar pagos");
            Need(!s.Related("terminaltxn","visitId",v.S("id")).Any(x=>x.S("status")=="uncertain"),"Hay un cobro con terminal sin respuesta en esta cuenta; consúltalo antes de cobrar otra vez");
            var outcome=p.S("card","approve");Need(TestCards.ContainsKey(outcome),"Tarjeta de prueba inválida");var card=TestCards[outcome];
            var txn=Add(s,"terminaltxn",J.O("visitId",v.S("id"),"folioId",p.S("folioId"),"amount",amount,"tip",tip,"terminalId",config.S("terminalId"),"brand",card.Brand,"last4",card.Last4,"reference",Ref("TRM"),"actor",u.S("id"),"at",J.Now()));
            if(outcome=="approve")ApproveTerminal(s,u,txn);
            else if(outcome=="decline"){txn["status"]="declined";txn["message"]="Rechazada por el banco emisor: fondos insuficientes (tarjeta de prueba)";}
            else{txn["status"]="uncertain";txn["message"]="La terminal no respondió. Consulta el resultado antes de cobrar de nuevo";}
            Log(s,u,"terminal","charge",txn.S("status"),card.Brand+" •••• "+card.Last4+" · "+Pesos(amount+tip),txn.S("reference"));result=txn;break;}
        case "terminal.reconcile":{
            Role(u,"mesero","manager");Simulator(s,"terminal","la terminal bancaria");var txn=s.Get("terminaltxn",id);Need(txn.S("status")=="uncertain","Ese cobro ya tiene resultado");var v=s.Get("visit",txn.S("visitId"));Need(u.S("role")=="manager"||v.S("waiterId")==u.S("id"),"La cuenta pertenece a otro mesero",403);
            // The simulated bank reports that the silent charge did go through: it is applied once, or reversed if the account was covered meanwhile.
            if(v.S("status")=="open"&&txn.L("amount")<=Totals(s,v).L("balance"))ApproveTerminal(s,u,txn);else{txn["status"]="reversed";txn["message"]="El banco sí cobró, pero la cuenta ya estaba cubierta: la terminal revirtió el cargo";}
            txn["reconciledAt"]=J.Now();txn["reconciledBy"]=u.S("id");Log(s,u,"terminal","reconcile",txn.S("status"),"Consulta de cobro sin respuesta · "+Pesos(txn.L("amount")+txn.L("tip")),txn.S("reference"));result=txn;break;}
        case "paylink.create":{
            Role(u,"mesero","manager");var config=Simulator(s,"onlinepay","pagos en línea");var v=Own(s,u,p.S("visitId"));var balance=Totals(s,v).L("balance");Need(balance>0,"La cuenta no tiene saldo pendiente");var amount=p.ContainsKey("amount")?Amount(p,"amount",1,balance):balance;
            Need(!s.Related("folio","visitId",v.S("id")).Any(f=>!f.B("void")),"Con subfolios usa otra forma de pago");Need(!s.Related("paylink","visitId",v.S("id")).Any(x=>x.S("status")=="pending"),"Ya hay una liga de pago vigente para esta cuenta");
            var provider=config.S("provider");var token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            var link=Add(s,"paylink",J.O("visitId",v.S("id"),"amount",amount,"provider",provider,"payUrl","https://pagos.simulado.invalid/"+provider+"/"+token,"reference",(provider=="stripe"?"plink_sim_":"pref_sim_")+token[..12],"status","pending","expiresAt",DateTimeOffset.UtcNow.AddMinutes(config.L("expiresMinutes",60)).ToString("O"),"actor",u.S("id"),"at",J.Now()));
            var customer=s.Related("customer","id",v.S("customerId")).FirstOrDefault();if(customer is not null&&Message(s,"whatsapp",customer.S("phone"),"paylink",link.S("id"),"Hola "+customer.S("name")+", paga tu cuenta de "+s.Meta.S("name")+" por "+Pesos(amount)+" aquí: "+link.S("payUrl")) is {} sent)link["messageId"]=sent.S("id");
            Log(s,u,"onlinepay","link.create","pending",(provider=="stripe"?"Stripe":"Mercado Pago")+" · liga por "+Pesos(amount),link.S("reference"));result=link;break;}
        case "paylink.simulate":{
            Role(u,"manager");Simulator(s,"onlinepay","pagos en línea");var link=s.Get("paylink",id);Need(link.S("status")=="pending","La liga ya no está vigente");var outcome=Choice(p,"outcome","paid","failed","expired");var v=s.Get("visit",link.S("visitId"));
            if(outcome=="paid"&&DateTimeOffset.Parse(link.S("expiresAt"))<DateTimeOffset.UtcNow)outcome="expired";
            if(outcome=="paid"){
                // Same rule as a provider webhook: a payment that arrives for an already covered account is refunded, never applied twice.
                if(v.S("status")=="open"&&link.L("amount")<=Totals(s,v).L("balance")){var pay=InternalPayment(s,u,v,link.L("amount"),"online");pay["provider"]=link.S("provider");pay["reference"]=link.S("reference");link["status"]="paid";link["paymentId"]=pay.S("id");link["paidAt"]=J.Now();}
                else{link["status"]="refunded";link["message"]="Llegó el pago pero la cuenta ya estaba cubierta; el proveedor lo reembolsó";}
            }else{link["status"]=outcome;link["message"]=outcome=="failed"?"El proveedor rechazó el pago del cliente":"La liga venció sin pago";}
            Log(s,u,"onlinepay","webhook."+outcome,link.S("status"),"Aviso del proveedor · "+Pesos(link.L("amount")),link.S("reference"));result=link;break;}
        case "paylink.cancel":{Role(u,"mesero","manager");var link=s.Get("paylink",id);Need(link.S("status")=="pending","La liga ya no está vigente");var v=s.Get("visit",link.S("visitId"));Need(u.S("role")=="manager"||v.S("waiterId")==u.S("id"),"La cuenta pertenece a otro mesero",403);link["status"]="cancelled";link["cancelledBy"]=u.S("id");Log(s,u,"onlinepay","link.cancel","cancelled","Liga cancelada · "+Pesos(link.L("amount")),link.S("reference"));result=link;break;}
        case "pms.charge":{
            Role(u,"mesero","manager");Need(u.S("role")=="manager"||u.Obj("permissions").B("payments",true),"Permiso de pagos deshabilitado",403);var config=Simulator(s,"pms","el hotel (PMS)");var v=Own(s,u,p.S("visitId"));
            var number=Text(p,"room",10).ToUpperInvariant();var room=config.A("rooms").Objects().FirstOrDefault(x=>x.S("room")==number);Need(room is not null,"El PMS no tiene un huésped registrado en la habitación "+number,404);
            Need(SameGuest(room!.S("guest"),Text(p,"guest",100)),"El apellido no coincide con el huésped de la habitación "+number,403);Need(!s.Related("folio","visitId",v.S("id")).Any(f=>!f.B("void")),"Con subfolios usa otra forma de pago");
            var balance=Totals(s,v).L("balance");var amount=p.ContainsKey("amount")?Amount(p,"amount",1,balance):balance;var limit=room.L("limit");var used=s.Related("pmspost","room",number).Where(x=>x.S("status")=="posted").Sum(x=>x.L("amount"));
            Need(limit==0||used+amount<=limit,"Supera el crédito autorizado de la habitación "+number+": quedan "+Pesos(Math.Max(0,limit-used)),402);
            var system=config.S("system");var reference=Ref(system=="opera"?"OPERA":"CLDB");var pay=InternalPayment(s,u,v,amount,"room");pay["reference"]=reference;pay["room"]=number;
            result=Add(s,"pmspost",J.O("room",number,"guest",room.S("guest"),"amount",amount,"visitId",v.S("id"),"paymentId",pay.S("id"),"system",system,"reference",reference,"status","posted","actor",u.S("id"),"at",J.Now()));
            Log(s,u,"pms","charge","posted","Cargo a habitación "+number+" · "+Pesos(amount),reference);break;}
        case "geo.lookup":{Role(u,"mesero","manager");var text=Text(p,"text",500);result=Geocode(s,text);Log(s,u,"maps","geocode","ok","Dirección ubicada a "+result["distanceKm"]+" km");break;}
        case "callerid.simulate":{
            Role(u,"mesero","manager");Simulator(s,"callerid","identificador de llamadas");var number=Digits(Text(p,"number",40));Need(number.Length is >=7 and <=15,"Número telefónico inválido");var tail=number[^Math.Min(10,number.Length)..];
            var customer=s.List("customer").FirstOrDefault(x=>Digits(x.S("phone")).EndsWith(tail,StringComparison.Ordinal)&&Digits(x.S("phone")).Length>=7);
            result=Add(s,"call",J.O("number",number,"name",Sanitize(p.S("name"),150),"customerId",customer?.S("id"),"at",J.Now(),"source","simulated-callerid","attended",false));Log(s,u,"callerid","call","ringing","Llamada de "+number+(customer is null?" · número nuevo":" · "+customer.S("name")));break;}
        case "call.attend":{Role(u,"mesero","manager");var call=s.Get("call",id);call["attended"]=true;call["attendedBy"]=u.S("id");call["attendedAt"]=J.Now();result=call;break;}
        case "message.test":{
            Role(u,"manager");var config=Simulator(s,"messaging","mensajería");var channel=Choice(p,"channel","whatsapp","email");Need(config.B(channel),(channel=="whatsapp"?"WhatsApp":"El correo")+" está apagado en la mensajería");var to=Text(p,"to",150);
            Need(channel=="email"?Regex.IsMatch(to,@"^[^@\s]+@[^@\s]+\.[^@\s]+$"):Digits(to).Length is >=10 and <=15,"Destinatario inválido");result=Message(s,channel,to,"test",J.Id(),"Mensaje de prueba de "+s.Meta.S("name")+". Simulado: no se envió a nadie.")!;break;}
        case "aggregator.simulate":{
            Role(u,"manager");var config=Simulator(s,"aggregators","plataformas de reparto");var platform=Choice(p,"platform",config.A("platforms").Strings());
            var menu=s.List("product").Where(x=>x.B("visible")&&x.B("available")&&!x.A("modifiers").Objects().Any(g=>g.B("required"))&&!(x.B("supportsCooking")&&x.B("cookingRequired"))).OrderBy(x=>x.S("id"),StringComparer.Ordinal).ToArray();Need(menu.Length>0,"Publica al menos un producto disponible sin opciones obligatorias");
            JsonArray items;var given=p.A("items");
            if(given.Count>0){Need(given.Count<=10&&given.Objects().Count()==given.Count,"Máximo 10 renglones");items=new JsonArray(given.Objects().Select(x=>{Need(menu.Any(m=>m.S("id")==x.S("productId")),"Producto no publicado en la plataforma");return (JsonNode)J.O("productId",x.S("productId"),"quantity",Amount(x,"quantity",1,10));}).ToArray());}
            else items=new JsonArray(menu.OrderBy(_=>RandomNumberGenerator.GetInt32(int.MaxValue)).Take(RandomNumberGenerator.GetInt32(1,Math.Min(3,menu.Length)+1)).Select(x=>(JsonNode)J.O("productId",x.S("id"),"quantity",RandomNumberGenerator.GetInt32(1,3))).ToArray());
            foreach(var row in items.Objects()){var product=menu.First(x=>x.S("id")==row.S("productId"));row["name"]=product.S("name");row["price"]=product.L("price");}
            var subtotal=items.Objects().Sum(x=>x.L("price")*x.L("quantity"));var names=new[]{"Laura M.","Carlos R.","Sofía G.","Diego P.","Valeria T.","Andrés L.","Mónica S.","Jorge H."};var customerName=Sanitize(p.S("customerName"),80);
            var order=Add(s,"aggregatororder",J.O("platform",platform,"platformName",Platforms[platform],"externalId",(platform=="uber"?"UE-":platform=="rappi"?"RP-":"DD-")+Code(6),"customerName",customerName==""?names[RandomNumberGenerator.GetInt32(names.Length)]:customerName,"items",items,"subtotal",subtotal,"notes",Sanitize(p.S("notes"),200),"status","pending","receivedAt",J.Now(),"acceptBy",DateTimeOffset.UtcNow.AddMinutes(8).ToString("O")));
            Log(s,u,"aggregators","order.received","pending",order.S("platformName")+" "+order.S("externalId")+" · "+Pesos(subtotal),order.S("externalId"));if(config.B("autoAccept"))AcceptAggregator(s,u,order);result=order;break;}
        case "aggregator.decide":{
            Role(u,"manager");var order=s.Get("aggregatororder",id);if(p.B("accept")){AcceptAggregator(s,u,order);result=order;break;}
            Need(order.S("status")=="pending","Pedido de plataforma ya resuelto");order["status"]="rejected";order["reason"]=Text(p,"reason",200);order["decidedBy"]=u.S("id");Log(s,u,"aggregators","order.reject","rejected",order.S("platformName")+" "+order.S("externalId")+" · "+order.S("reason"),order.S("externalId"));result=order;break;}
        case "aggregator.handoff":{
            Role(u,"mesero","manager");var order=s.Get("aggregatororder",id);Need(order.S("status")=="accepted","El pedido no está listo para entregar");var vid=order.S("visitId");
            Need(!s.Related("unit","visitId",vid).Any(x=>!Final.Append("ready").Contains(x.S("status"))),"Termina la preparación antes de entregar al repartidor");Act(s,u,"visit.deliverAll",J.O("id",vid));
            var couriers=new[]{"Jorge","Ana","Luis","Paola","Raúl"};order["status"]="handed-off";order["courier"]=couriers[RandomNumberGenerator.GetInt32(couriers.Length)];order["handedOffAt"]=J.Now();order["handedOffBy"]=u.S("id");
            Log(s,u,"aggregators","order.handoff","handed-off",order.S("platformName")+" "+order.S("externalId")+" entregado a "+order.S("courier"),order.S("externalId"));result=order;break;}
        case "aggregator.sync":{
            Role(u,"manager");var config=Simulator(s,"aggregators","plataformas de reparto");var products=s.List("product").Where(x=>x.B("visible")).ToArray();var published=products.Count(x=>x.B("available")&&!x.A("modifiers").Objects().Any(g=>g.B("required"))&&!(x.B("supportsCooking")&&x.B("cookingRequired")));
            var row=s.Get("integration","aggregators");row["lastSync"]=J.O("at",J.Now(),"published",published,"excluded",products.Length-published,"platforms",config["platforms"]?.DeepClone());
            Log(s,u,"aggregators","menu.sync","ok","Menú publicado en "+config.A("platforms").Count+" plataformas · "+published+" productos"+(products.Length>published?" · "+(products.Length-published)+" con opciones obligatorias o agotados quedaron fuera":""));result=row.Obj("lastSync");break;}
        default:return false;
        }return true;
    }
    // Runs after every action, including nested ones; each step is idempotent.
    void IntegrationAfterAction(BranchState s,JsonObject u,string type,JsonObject p,JsonObject result)
    {
        if(type=="delivery.create"&&Simulated(s,"maps") is {} maps){var d=s.Get("delivery",result.S("id"));var a=d.Obj("addressSnapshot");if(d.S("mode")=="delivery"&&a.ContainsKey("lat")){var config=maps.Obj("settings");var km=Distance((double)Coordinate(config,"centerLat",90),(double)Coordinate(config,"centerLon",180),(double)Coordinate(a,"lat",90),(double)Coordinate(a,"lon",180));d["distanceKm"]=Math.Round((decimal)km,1);d["etaMinutes"]=Eta(km);}}
        if(type=="delivery.dispatch"){var d=s.Get("delivery",p.S("id"));var c=s.Related("customer","id",d.S("customerId")).FirstOrDefault();var courier=s.Related("courier","id",d.S("courierId")).FirstOrDefault();if(c is not null)Message(s,"whatsapp",c.S("phone"),"dispatch",d.S("id"),"Hola "+c.S("name")+", tu pedido de "+s.Meta.S("name")+" ya va en camino"+(courier is null?"":" con "+courier.S("name"))+(d.L("etaMinutes")>0?". Llega en unos "+d.L("etaMinutes")+" minutos":"")+".");}
        if(type=="unit.state"&&p.S("status")=="ready"){
            var vid=s.Get("unit",p.S("id")).S("visitId");var d=s.Related("delivery","visitId",vid).FirstOrDefault(x=>x.S("mode")=="pickup");
            if(d is not null&&!s.Related("unit","visitId",vid).Any(x=>!Final.Append("ready").Contains(x.S("status")))&&s.Related("customer","id",d.S("customerId")).FirstOrDefault() is {} c)Message(s,"whatsapp",c.S("phone"),"pickup-ready",d.S("id"),"Hola "+c.S("name")+", tu pedido de "+s.Meta.S("name")+" está listo para recoger.");
        }
        if(type=="fiscal.request"&&Simulated(s,"pac")?.Obj("settings").B("autoStamp")==true)StampSimulated(s,u,s.Get("fiscal",result.S("id")));
        var owner=Simulated(s,"messaging")?.Obj("settings").S("ownerEmail")??"";
        if(owner!="")foreach(var n in s.Related("ownernotice","messageId","").Take(5).ToArray())if(Message(s,"email",owner,"owner-notice",n.S("id"),OwnerText(s,n)) is {} sent)n["messageId"]=sent.S("id");
        if(Simulated(s,"printer") is not null)foreach(var job in s.Related("printjob","status","pending").Take(20).ToArray()){job["status"]="simulated";job["attemptId"]=J.Id();job["driver"]="simulated-escpos";job["evidence"]=job.S("type")=="drawer"?"Pulso RJ11 simulado · el cajón físico no se abrió":"Impresión simulada · no salió papel";job["resolvedAt"]=J.Now();}
    }
    void FilterIntegrationSnapshot(BranchState s,JsonObject u,JsonObject output)
    {
        if(u.S("role")=="manager")return;var role=u.S("role");
        output["integrations"]=new JsonArray(s.List("integration").Select(x=>(JsonNode)J.O("id",x.S("id"),"mode",x.S("mode"))).ToArray());
        foreach(var kind in new[]{"integrationlog","message","pmspost"})output[kind+"s"]=new JsonArray();
        var mine=s.Related("visit","waiterId",u.S("id")).Select(v=>v.S("id")).ToHashSet();
        foreach(var kind in new[]{"terminaltxn","paylink"})output[kind+"s"]=new JsonArray(role=="mesero"?s.List(kind).Where(x=>mine.Contains(x.S("visitId"))).TakeLast(200).Select(x=>(JsonNode)x.DeepClone()).ToArray():[]);
        if(role!="mesero")output["aggregatororders"]=new JsonArray();
    }
}
