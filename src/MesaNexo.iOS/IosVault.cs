using Foundation;
using Security;
using MesaNexo.Client;
namespace MesaNexo.iOS;
public sealed class IosVault:ISecureVault
{
    const string Service="mx.mesanexo.nativeclient.v1";
    static SecRecord Query(string key)=>new(SecKind.GenericPassword){Service=Service,Account=key};
    public Task<string?> Read(string key){using var query=Query(key);using var record=SecKeyChain.QueryAsRecord(query,out var status);if(status==SecStatusCode.ItemNotFound)return Task.FromResult<string?>(null);if(status!=SecStatusCode.Success)throw new IOException("Desbloquea el llavero del dispositivo");return Task.FromResult<string?>(record.ValueData?.ToString());}
    public Task Write(string key,string value){using var query=Query(key);using var attributes=new SecRecord(SecKind.GenericPassword){ValueData=NSData.FromString(value),Accessible=SecAccessible.WhenUnlockedThisDeviceOnly};var status=SecKeyChain.Update(query,attributes);if(status==SecStatusCode.ItemNotFound){using var add=Query(key);add.ValueData=NSData.FromString(value);add.Accessible=SecAccessible.WhenUnlockedThisDeviceOnly;status=SecKeyChain.Add(add);}if(status!=SecStatusCode.Success)throw new IOException("No se guardó la operación en el llavero");return Task.CompletedTask;}
    public Task Delete(string key){using var query=Query(key);var status=SecKeyChain.Remove(query);if(status!=SecStatusCode.Success&&status!=SecStatusCode.ItemNotFound)throw new IOException("No se actualizó el llavero");return Task.CompletedTask;}
}