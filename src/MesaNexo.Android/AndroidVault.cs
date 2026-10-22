using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using MesaNexo.Client;
namespace MesaNexo.Android;
public sealed class AndroidVault(Context context):ISecureVault
{
    const string Alias="MesaNexoDeviceVaultV1";readonly object gate=new();
    ISecretKey Key()
    {
        using var store=KeyStore.GetInstance("AndroidKeyStore")!;store.Load(null);
        if(store.ContainsAlias(Alias))return (ISecretKey)store.GetKey(Alias,null)!;
        using var generator=KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes,"AndroidKeyStore")!;
        using var spec=new KeyGenParameterSpec.Builder(Alias,KeyStorePurpose.Encrypt|KeyStorePurpose.Decrypt).SetBlockModes(KeyProperties.BlockModeGcm).SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone).SetRandomizedEncryptionRequired(true).Build();generator.Init(spec);return generator.GenerateKey()!;
    }
    public Task<string?> Read(string key){lock(gate){var raw=context.GetSharedPreferences("mesanexo-secure",FileCreationMode.Private)!.GetString(key,null);if(raw is null)return Task.FromResult<string?>(null);var parts=raw.Split('.');using var cipher=Cipher.GetInstance("AES/GCM/NoPadding")!;using var spec=new GCMParameterSpec(128,Convert.FromBase64String(parts[0]));cipher.Init(CipherMode.DecryptMode,Key(),spec);return Task.FromResult<string?>(Encoding.UTF8.GetString(cipher.DoFinal(Convert.FromBase64String(parts[1]))!));}}
    public Task Write(string key,string value){lock(gate){using var cipher=Cipher.GetInstance("AES/GCM/NoPadding")!;cipher.Init(CipherMode.EncryptMode,Key());var encrypted=cipher.DoFinal(Encoding.UTF8.GetBytes(value))!;var raw=Convert.ToBase64String(cipher.GetIV()!)+"."+Convert.ToBase64String(encrypted);if(!context.GetSharedPreferences("mesanexo-secure",FileCreationMode.Private)!.Edit()!.PutString(key,raw)!.Commit())throw new IOException("No se guardó la operación protegida");return Task.CompletedTask;}}
    public Task Delete(string key){lock(gate){if(!context.GetSharedPreferences("mesanexo-secure",FileCreationMode.Private)!.Edit()!.Remove(key)!.Commit())throw new IOException("No se confirmó el cambio del almacén seguro");return Task.CompletedTask;}}
}