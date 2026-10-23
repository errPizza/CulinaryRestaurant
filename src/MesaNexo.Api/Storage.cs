using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;
public sealed class StoreException:Exception{public string Code=>Message;public StoreException(string code):base(code){}}
public sealed class RuntimeStore:IBranchStore,IDisposable
{
    readonly string? socketPath;readonly Process? process;readonly SemaphoreSlim gate=new(1);const int Limit=128*1024;
    public RuntimeStore(string socketPath){this.socketPath=socketPath;}
    public RuntimeStore(string python,string labScript,string directory,string site)
    {
        var start=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{labScript,"--directory",directory,"--site",site})start.ArgumentList.Add(arg);
        process=Process.Start(start)??throw new InvalidOperationException("No se inició runtime de laboratorio");
        process.ErrorDataReceived+=(_,e)=>{if(e.Data is not null)Console.Error.WriteLine("Runtime de laboratorio: fallo de proceso");};process.BeginErrorReadLine();
    }
    public async Task<JsonNode?> CallAsync(JsonObject request,CancellationToken ct=default)
    {
        var bytes=Encoding.UTF8.GetBytes(request.ToJsonString()+"\n");if(bytes.Length>Limit)throw new StoreException("COS-DATA-INVALID");
        await gate.WaitAsync(ct);
        try{
            string raw;
            if(process is not null){
                if(process.HasExited)throw new StoreException("COS-DATA-UNAVAILABLE");
                // Single shared pipe: once a request is written its reply must be consumed even if the caller cancels,
                // otherwise the next caller reads this reply and the framing is permanently shifted.
                await process.StandardInput.BaseStream.WriteAsync(bytes,CancellationToken.None);await process.StandardInput.BaseStream.FlushAsync(CancellationToken.None);
                raw=await ReadLine(process.StandardOutput.BaseStream,CancellationToken.None);
            }else{
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath!),deadline.Token);
                using var stream=new NetworkStream(socket,true);await stream.WriteAsync(bytes,deadline.Token);raw=await ReadLine(stream,deadline.Token);
            }
            var reply=JsonNode.Parse(raw)??throw new StoreException("COS-DATA-UNAVAILABLE");if(!reply.B("ok"))throw new StoreException(reply.S("error","COS-DATA-UNAVAILABLE"));return reply["result"]?.DeepClone();
        }catch(StoreException){throw;}catch(Exception e) when(e is IOException or SocketException or OperationCanceledException){throw new StoreException("COS-DATA-UNAVAILABLE");}finally{gate.Release();}
    }
    static async Task<string> ReadLine(Stream stream,CancellationToken ct)
    {
        using var buffer=new MemoryStream();var chunk=new byte[4096];while(buffer.Length<Limit){var count=await stream.ReadAsync(chunk,ct);if(count==0)throw new IOException("IPC cerrado");var end=Array.IndexOf(chunk,(byte)10,0,count);buffer.Write(chunk,0,end>=0?end:count);if(buffer.Length>Limit)break;if(end>=0)return Encoding.UTF8.GetString(buffer.ToArray());}throw new StoreException("COS-DATA-INVALID");
    }
    public void Dispose(){if(process is not null&&!process.HasExited){process.StandardInput.Close();if(!process.WaitForExit(2000))process.Kill(true);}process?.Dispose();gate.Dispose();}
}