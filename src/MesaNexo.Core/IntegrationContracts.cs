using System.Text.Json.Nodes;
namespace MesaNexo.Core;

// HttpStatus is the PAC's answer code for the stamping log; 0 when no answer arrived (timeout or network).
public sealed record PacResult(string Status, string ProviderId = "", string FiscalUuid = "", string Message = "", int HttpStatus = 0);
// Loads the issuer's CSD into the PAC. The files and the password are used for this call only and never stored.
public interface ICsdLoader {
    bool IsSandbox { get; }
    Task<PacResult> LoadCsd(byte[] certificate, byte[] privateKey, string password, CancellationToken ct = default);
}
// The PDF of a stamped CFDI, fetched from the PAC each time it is asked for and never stored in the branch.
public interface ICfdiPdfSource {
    Task<byte[]> Pdf(string providerId, CancellationToken ct = default);
}
public interface IPacAdapter {
    bool IsSandbox { get; }
    Task<PacResult> Stamp(JsonObject frozen, CancellationToken ct = default);
    Task<PacResult> Consult(string id, CancellationToken ct = default);
    Task<PacResult> Cancel(string id, string fiscalUuid, string motive, string? replacement = null, CancellationToken ct = default);
}
