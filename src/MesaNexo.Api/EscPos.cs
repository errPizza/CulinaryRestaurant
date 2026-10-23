using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// The receipt printer, from /etc/culinaryos/printer.json: {"uri":"tcp://10.77.0.20:9100","columns":48}
// (network printers on their RAW port) or {"uri":"file:///dev/usb/lp0","columns":32} (USB; the backend user needs the lp group).
// 48 columns is 80 mm paper with font A, 32 is 58 mm.
public sealed record PrinterOptions(string Uri,int Columns=48)
{
    public static PrinterOptions Load(string path)=>From(JsonNode.Parse(File.ReadAllText(path)) as JsonObject??throw new BusinessException(400,"Configuración de impresora inválida"));
    public static PrinterOptions From(JsonObject o)
    {
        var options=new PrinterOptions(o.S("uri"),o.I("columns",48));Need(options.Columns is >=24 and <=64,"Columnas de impresora entre 24 y 64");
        var uri=new Uri(options.Uri);Need(uri.Scheme=="tcp"&&uri.Host!=""&&uri.Port is >0 and <65536||uri.Scheme=="file"&&uri.LocalPath!="","Impresora: usa tcp://host:puerto o file:///dev/usb/lp0");
        return options;
    }
}
// What the printer answers to the real-time status requests (DLE EOT 1, 2 and 4). A printer that does not answer is
// reachable but of unknown status: it is printed to, and the evidence says the status was not confirmed.
public sealed record PrinterStatus(bool Reachable,bool Answered=false,bool Offline=false,bool CoverOpen=false,bool PaperOut=false,bool PaperLow=false)
{
    public bool CanPrint=>Reachable&&!(Answered&&(Offline||CoverOpen||PaperOut));
    public string Describe()=>!Reachable?"sin conexión":!Answered?"sin estado (no responde DLE EOT)":PaperOut?"sin papel":CoverOpen?"tapa abierta":Offline?"fuera de línea":PaperLow?"papel por terminarse":"lista";
    // Every real-time status byte has bits 1 and 4 set and bits 0 and 7 clear.
    public static bool Valid(int b)=>b>=0&&(b&0x93)==0x12;
    public static PrinterStatus From(int printer,int offline,int paper)=>new(true,true,(printer&0x08)!=0,(offline&0x04)!=0,(paper&0x60)!=0||(offline&0x20)!=0,(paper&0x0C)!=0);
}
// ESC/POS commands as Epson documents them; code page PC850 covers Spanish (á é í ó ú ñ ü ¡ ¿).
sealed class EscPosWriter(int columns)
{
    static readonly Encoding Text=CodePage850();
    static Encoding CodePage850(){Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);return Encoding.GetEncoding(850,new EncoderReplacementFallback("?"),new DecoderReplacementFallback("?"));}
    readonly List<byte> bytes=[];bool big;
    public int Width=>big?columns/2:columns;
    public EscPosWriter Raw(params byte[] b){bytes.AddRange(b);return this;}
    public EscPosWriter Init()=>Raw(0x1B,0x40).Raw(0x1B,0x74,0x02);
    public EscPosWriter Center()=>Raw(0x1B,0x61,0x01);
    public EscPosWriter Left()=>Raw(0x1B,0x61,0x00);
    public EscPosWriter Bold(bool on)=>Raw(0x1B,0x45,(byte)(on?1:0));
    public EscPosWriter Big(bool on){big=on;return Raw(0x1D,0x21,(byte)(on?0x11:0x00));}
    // Control characters never reach the printer: in text they would be read as commands.
    public EscPosWriter Line(string text=""){bytes.AddRange(Text.GetBytes(new string(text.Where(c=>!char.IsControl(c)).ToArray())));bytes.Add(0x0A);return this;}
    // Left text and right-aligned amount; a long left text wraps onto further lines, the amount stays on the first.
    public EscPosWriter Row(string left,string right)
    {
        var room=Math.Max(1,Width-right.Length-1);var words=Wrap(left,room).ToArray();
        Line(words[0].PadRight(Width-right.Length)+right);foreach(var rest in words.Skip(1))Line("  "+rest);return this;
    }
    static IEnumerable<string> Wrap(string text,int width)
    {
        var line=new StringBuilder();
        foreach(var word in text.Split(' ',StringSplitOptions.RemoveEmptyEntries))
            for(var piece=word;piece.Length>0;){
                if(line.Length>0&&line.Length+1+piece.Length>width){yield return line.ToString();line.Clear();}
                var take=Math.Min(piece.Length,width-(line.Length>0?line.Length+1:0));if(take<=0){yield return line.ToString();line.Clear();continue;}
                if(line.Length>0)line.Append(' ');line.Append(piece[..take]);piece=piece[take..];
            }
        if(line.Length>0||text.Trim()=="")yield return line.ToString();
    }
    public EscPosWriter Rule()=>Line(new string('-',Width));
    // Feed three lines and cut partially (GS V 66 3).
    public EscPosWriter Cut()=>Raw(0x1D,0x56,0x42,0x03);
    // Drawer kick on pin 2: 50 ms on, 500 ms off (ESC p 0 25 250).
    public EscPosWriter Pulse()=>Raw(0x1B,0x70,0x00,0x19,0xFA);
    public byte[] ToArray()=>bytes.ToArray();
}
// Turns a print job, enriched by BranchService with the names it refers to, into the bytes of its document.
public static class EscPosDocument
{
    static readonly CultureInfo Mexico=CultureInfo.GetCultureInfo("es-MX");
    static string Money(long cents)=>(cents<0?"-":"")+"$"+(Math.Abs(cents)/100m).ToString("#,0.00",CultureInfo.InvariantCulture);
    static string Local(string at)=>DateTimeOffset.TryParse(at,CultureInfo.InvariantCulture,DateTimeStyles.None,out var t)?t.ToLocalTime().ToString("dd/MM/yyyy HH:mm",Mexico):"";
    static readonly Dictionary<string,string> Methods=new(){["cash"]="Efectivo",["card"]="Tarjeta",["transfer"]="Transferencia",["wallet"]="Monedero",["payroll"]="Nómina",["online"]="En línea",["room"]="Cargo a habitación",["platform"]="Plataforma"};
    public static byte[] Render(JsonObject job,int columns)=>job.S("type") switch{
        "drawer"=>new EscPosWriter(columns).Init().Pulse().ToArray(),
        "bill"=>Bill(job,columns),
        "zreport"=>ZReport(job,columns),
        var other=>throw new BusinessException(400,"Tipo de impresión desconocido: "+other)};
    static EscPosWriter Header(JsonObject job,int columns,string title)
    {
        var w=new EscPosWriter(columns).Init().Center().Bold(true).Big(true);w.Line(job.S("restaurantName"));w.Big(false).Bold(false);
        if(title!="")w.Line(title);if(job.S("retryOf")!="")w.Line("*** REIMPRESIÓN ***");return w.Left().Rule();
    }
    static byte[] Bill(JsonObject job,int columns)
    {
        var snapshot=job.Obj("snapshot");var totals=snapshot.Obj("totals");
        var w=Header(job,columns,job.S("tableName")!=""?job.S("tableName"):"Ticket "+job.S("visitId")[..Math.Min(8,job.S("visitId").Length)]);
        w.Row("Cuenta",job.S("visitId")[..Math.Min(8,job.S("visitId").Length)]);if(job.S("waiterName")!="")w.Row("Atendió",job.S("waiterName"));w.Row("Fecha",Local(job.S("at")));w.Rule();
        // Units are sold one by one; the receipt groups equal name and price into one line.
        foreach(var group in snapshot.A("units").Objects().Where(x=>x.S("status") is not ("cancelled" or "unavailable")).GroupBy(x=>(Name:x.S("name"),Price:x.L("price"))))
            w.Row(group.Count()+" x "+group.Key.Name,Money(group.Key.Price*group.Count()));
        w.Rule().Row("Subtotal",Money(totals.L("gross")));if(totals.L("discount")>0)w.Row("Descuento","-"+Money(totals.L("discount")));
        w.Bold(true).Big(true).Row("TOTAL",Money(totals.L("total"))).Big(false).Bold(false);
        w.Row("IVA incluido",Money(totals.L("tax")));if(totals.L("paid")>0)w.Row("Pagado",Money(totals.L("paid"))).Row("Saldo",Money(totals.L("balance")));
        return w.Line().Center().Line("¡Gracias por su visita!").Cut().ToArray();
    }
    static byte[] ZReport(JsonObject job,int columns)
    {
        var z=job.Obj("zreport");var s=z.Obj("summary");
        var w=Header(job,columns,"CORTE Z No. "+z.L("number"));
        w.Row("Desde",Local(s.S("from"))).Row("Hasta",Local(s.S("to"))).Rule();
        w.Row("Cuentas",s.L("tickets").ToString(CultureInfo.InvariantCulture)).Row("Comensales",s.L("people").ToString(CultureInfo.InvariantCulture));
        w.Row("Venta bruta",Money(s.L("gross"))).Row("Descuentos","-"+Money(s.L("discounts")));
        w.Bold(true).Row("Venta neta",Money(s.L("net"))).Bold(false).Row("IVA incluido",Money(s.L("tax"))).Row("Ticket promedio",Money(s.L("average"))).Rule();
        w.Bold(true).Line("FORMAS DE PAGO").Bold(false);
        foreach(var (method,value) in s.Obj("methods"))w.Row((Methods.GetValueOrDefault(method)??method)+" ("+value.L("count")+")",Money(value.L("amount")));
        w.Row("Propinas",Money(s.L("tips"))).Bold(true).Row("Total cobrado",Money(s.L("payments"))).Bold(false).Rule();
        w.Row("Cancelaciones ("+s.L("voidedCount")+")",Money(s.L("voidedAmount")));if(s.L("comps")>0)w.Row("Cortesías",s.L("comps").ToString(CultureInfo.InvariantCulture));if(s.L("staffMeals")>0)w.Row("Consumo de empleados",s.L("staffMeals").ToString(CultureInfo.InvariantCulture));
        if(s.A("waiters").Count>0){w.Rule().Bold(true).Line("MESEROS").Bold(false);foreach(var waiter in s.A("waiters").Objects())w.Row(waiter.S("name")+" ("+waiter.L("tickets")+")",Money(waiter.L("amount")));}
        if(s.A("categories").Count>0){w.Rule().Bold(true).Line("CATEGORÍAS").Bold(false);foreach(var category in s.A("categories").Objects().Take(15))w.Row(category.S("name")+" ("+category.L("quantity")+")",Money(category.L("amount")));}
        return w.Rule().Center().Line("Huella "+z.S("digest")[..Math.Min(16,z.S("digest").Length)]).Cut().ToArray();
    }
}
// ESC/POS over a network printer's RAW port (tcp://) or a character device (file://). Real-time status is asked
// over TCP only: a device file read would block when the printer does not answer.
public sealed class EscPosPrintDriver(PrinterOptions options):IPrintDriver
{
    readonly Uri uri=new(options.Uri);
    public string Name=>"ESC/POS "+options.Uri;
    public PrinterStatus? Last{get;private set;}
    async Task<Stream> Open(CancellationToken ct)
    {
        if(uri.Scheme=="file")return new FileStream(uri.LocalPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);
        var client=new TcpClient{NoDelay=true};
        try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(3));await client.ConnectAsync(uri.Host,uri.Port,timeout.Token);return new NetworkStream(client.Client,true);}
        catch{client.Dispose();throw;}
    }
    // One status byte per request; null when the printer closed the connection, -1 when it did not answer in time.
    static async Task<int?> Ask(Stream stream,byte n,CancellationToken ct)
    {
        await stream.WriteAsync(new byte[]{0x10,0x04,n},ct);await stream.FlushAsync(ct);var buffer=new byte[1];
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try{return await stream.ReadAsync(buffer,timeout.Token)==0?null:buffer[0];}catch(OperationCanceledException)when(!ct.IsCancellationRequested){return -1;}
    }
    static async Task<PrinterStatus?> Query(Stream stream,CancellationToken ct)
    {
        var printer=await Ask(stream,1,ct);if(printer is null)return null;if(!PrinterStatus.Valid(printer.Value))return new(true);
        var offline=await Ask(stream,2,ct);var paper=offline is null?null:await Ask(stream,4,ct);if(offline is null||paper is null)return null;
        return PrinterStatus.Valid(offline.Value)&&PrinterStatus.Valid(paper.Value)?PrinterStatus.From(printer.Value,offline.Value,paper.Value):new(true);
    }
    public async Task<PrinterStatus> Status(CancellationToken ct=default)
    {
        if(uri.Scheme=="file")return Last=new(File.Exists(uri.LocalPath));
        try{await using var stream=await Open(ct);return Last=await Query(stream,ct)??new(false);}
        catch(Exception e)when(e is SocketException or IOException or OperationCanceledException&&!ct.IsCancellationRequested){return Last=new(false);}
    }
    // Before the job is claimed: whatever is found here leaves it pending, so nothing is lost and nothing is printed twice.
    public async Task Preflight(JsonObject job,CancellationToken ct)
    {
        var status=await Status(ct);Need(status.Reachable,"Impresora sin conexión: "+options.Uri,503);
        // Opening the drawer needs neither paper nor a closed cover.
        if(job.S("type")!="drawer")Need(status.CanPrint,"Impresora "+status.Describe(),503);
    }
    // After the job is claimed. "failed" only when nothing reached the printer; any doubt is "uncertain" for a manager to resolve.
    public async Task<PrintOutcome> Submit(JsonObject job,CancellationToken ct)
    {
        var data=EscPosDocument.Render(job,options.Columns);var what=job.S("type")=="drawer"?"Pulso de cajón":"Impresión";Stream stream;
        try{stream=await Open(ct);}catch(Exception e)when(!ct.IsCancellationRequested){Last=new(false);return new("failed","No se pudo conectar con la impresora; no se envió nada ("+e.GetType().Name+")");}
        await using(stream){
            try{await stream.WriteAsync(data,ct);await stream.FlushAsync(ct);}
            catch(Exception)when(!ct.IsCancellationRequested){return new("uncertain",what+": la conexión se cortó al enviar; revisar si salió antes de reintentar");}
            if(uri.Scheme=="file"){Last=new(true);return new("submitted",what+$" enviada al dispositivo ({data.Length} bytes) · estado no consultado");}
            PrinterStatus? after;try{after=await Query(stream,ct);}catch(Exception)when(!ct.IsCancellationRequested){after=null;}
            if(after is null){Last=new(false);return new("uncertain",what+": la impresora cerró la conexión tras recibir los datos; revisar si salió completo");}
            Last=after;
            if(job.S("type")!="drawer"&&after.Answered&&!after.CanPrint)return new("uncertain",what+$" enviada ({data.Length} bytes) pero la impresora reporta: {after.Describe()}; revisar si salió completo");
            return new("submitted",what+$" enviada ({data.Length} bytes) · estado: {after.Describe()}");
        }
    }
}
