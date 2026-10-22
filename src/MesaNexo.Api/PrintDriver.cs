using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
public sealed record PrintOutcome(string Status,string Evidence);
public interface IPrintDriver
{
    string Name{get;}
    Task Preflight(JsonObject job,CancellationToken ct);
    Task<PrintOutcome> Submit(JsonObject job,CancellationToken ct);
}
// Uses the configured CUPS driver, never arbitrary RJ11/network commands or a shell.
public sealed class CupsPrintDriver(string queue,string? drawerOption=null):IPrintDriver
{
    public string Name=>"CUPS:"+queue;
    public Task Preflight(JsonObject job,CancellationToken ct)
    {
        Need(OperatingSystem.IsLinux()&&File.Exists("/usr/bin/lp"),"Driver CUPS no disponible",503);Need(Regex.IsMatch(queue,"^[A-Za-z0-9_.-]{1,100}$"),"Cola de impresión inválida");if(job.S("type")=="drawer")Need(drawerOption is not null&&Regex.IsMatch(drawerOption,"^[A-Za-z0-9_.-]+=[A-Za-z0-9_.-]+$"),"Configura y verifica la opción de cajonera de este driver");return Task.CompletedTask;
    }
    public async Task<PrintOutcome> Submit(JsonObject job,CancellationToken ct)
    {
        await Preflight(job,ct);var start=new ProcessStartInfo("/usr/bin/lp"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("-d");start.ArgumentList.Add(queue);if(job.S("type")=="drawer"){start.ArgumentList.Add("-o");start.ArgumentList.Add(drawerOption!);}start.ArgumentList.Add("-t");start.ArgumentList.Add("Culinary Restaurant "+job.S("id"));
        using var process=Process.Start(start)??throw new IOException("No se inició CUPS");var text=new StringBuilder("Culinary Restaurant\n");foreach(var unit in job.Obj("snapshot").A("units").Objects())text.AppendLine(unit.S("name")+" · "+(unit.L("price")/100m).ToString("0.00",System.Globalization.CultureInfo.InvariantCulture));text.AppendLine("Total: "+(job.Obj("snapshot").Obj("totals").L("total")/100m).ToString("0.00",System.Globalization.CultureInfo.InvariantCulture));await process.StandardInput.WriteAsync(text.ToString().AsMemory(),ct);process.StandardInput.Close();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));await process.WaitForExitAsync(timeout.Token);var output=await process.StandardOutput.ReadToEndAsync(ct);return new(process.ExitCode==0?"submitted":"failed",process.ExitCode==0?"Spooler aceptó; no comprueba papel/cajonera. "+output[..Math.Min(output.Length,1000)]:"Driver devolvió fallo; revisar dispositivo antes de reintentar");
    }
}
public sealed partial class BranchService
{
    public async Task<bool> ProcessPrint(IPrintDriver driver,CancellationToken ct=default)
    {
        var s=await Load(ct);var job=s.List("printjob").FirstOrDefault(x=>x.S("status")=="pending");if(job is null)return false;await driver.Preflight(job,ct);var id=job.S("id");var attemptId=J.Id();job["status"]="uncertain";job["attemptId"]=attemptId;job["driver"]=driver.Name;job["claimedAt"]=J.Now();try{await Commit(s,J.Id(),"authorized-print-service",J.Hash(id+attemptId),J.O("id",id,"attemptId",attemptId),"print.driver.claim",ct);}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"){return false;}
        PrintOutcome outcome;try{outcome=await driver.Submit((JsonObject)job.DeepClone(),ct);}catch{outcome=new("uncertain","Resultado físico no comprobado. Conciliar antes de reintentar.");}
        for(int retry=0;retry<8;retry++){s=await Load(ct);job=s.Get("printjob",id);Need(job.S("attemptId")==attemptId,"Intento físico cambió",409);job["status"]=outcome.Status;job["evidence"]=outcome.Evidence;try{await Commit(s,J.Id(),"authorized-print-service",J.Hash(id+attemptId),J.O("id",id,"status",outcome.Status),"print.driver.result",ct);return true;}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&retry<7){}}
        return false;
    }
}
