using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using MesaNexo.Core;
namespace MesaNexo.Api;
public sealed class Passwords:IPasswordHasher
{
    const int Memory=65536,Iterations=3,Parallelism=1;
    public string Hash(string password)
    {
        Rules.Need(password.Length is >=12 and <=200,"Contraseña: de 12 a 200 caracteres");var salt=RandomNumberGenerator.GetBytes(16);var hash=Derive(password,salt,Memory,Iterations,Parallelism);
        return $"$argon2id$v=19$m={Memory},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public bool Verify(string encoded,string password)
    {
        if(password.Length>200)return false;
        try{var parts=encoded.Split('$');if(parts.Length!=6||parts[1]!="argon2id"||parts[2]!="v=19")return false;var pars=parts[3].Split(',').Select(x=>int.Parse(x.Split('=')[1])).ToArray();if(pars.Length!=3||pars[0]!=Memory||pars[1]!=Iterations||pars[2]!=Parallelism)return false;var salt=Convert.FromBase64String(parts[4]);var expected=Convert.FromBase64String(parts[5]);return salt.Length==16&&expected.Length==32&&CryptographicOperations.FixedTimeEquals(expected,Derive(password,salt,pars[0],pars[1],pars[2]));}catch{return false;}
    }
    static byte[] Derive(string password,byte[] salt,int memory,int iterations,int parallelism){using var a=new Argon2id(Encoding.UTF8.GetBytes(password)){Salt=salt,MemorySize=memory,Iterations=iterations,DegreeOfParallelism=parallelism};return a.GetBytes(32);}
}