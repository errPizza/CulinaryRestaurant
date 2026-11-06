using System.Text.RegularExpressions;
namespace MesaNexo.Core;

// SAT catalogs for CFDI 4.0 that the branch checks before anything reaches the PAC. CulinaryServer applies the same RFC rules
// (src/sat.ts) to the business behind each license.
public static partial class Sat {
    const string Digits = "0123456789ABCDEFGHIJKLMN&OPQRSTUVWXYZ Ñ";
    public const string GenericRfc = "XAXX010101000";
    public const string ForeignRfc = "XEXX010101000";
    [GeneratedRegex(@"^[A-ZÑ&]{3,4}(\d{2})(\d{2})(\d{2})[A-Z\d]{2}[A\d]$")] private static partial Regex RfcShape();

    // Moral (12 characters) or física (13); the generic RFCs are valid shapes with their own rules.
    public static bool IsMoral(string rfc) => rfc.Length == 12;
    public static string? RfcProblem(string rfc) {

        var m = RfcShape().Match(rfc);
        if (!m.Success) return "RFC: 3 letras (persona moral) o 4 (persona física), 6 dígitos de fecha y 3 de homoclave";
        if (rfc is GenericRfc or ForeignRfc) return null;
        if (!DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value, "yyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) return "RFC: la fecha no existe";
        var padded = rfc.Length == 12 ? " " + rfc : rfc;
        var sum = 0;
        for (int i = 0; i < 12; i++) sum += Digits.IndexOf(padded[i]) * (13 - i);
        var rest = sum % 11;
        var expected = rest == 0 ? '0' : 11 - rest == 10 ? 'A' : (char)('0' + 11 - rest);
        return padded[12] == expected ? null : "RFC: el dígito verificador no corresponde; revísalo en la Constancia de Situación Fiscal";

    }
    // c_RegimenFiscal: (applies to moral, applies to física).
    public static readonly Dictionary<string, (bool Moral, bool Fisica)> Regimes = new() {
        ["601"] = (true, false), ["603"] = (true, false), ["605"] = (false, true), ["606"] = (false, true), ["607"] = (false, true),
        ["608"] = (false, true), ["610"] = (true, true), ["611"] = (false, true), ["612"] = (false, true), ["614"] = (false, true),
        ["615"] = (false, true), ["616"] = (false, true), ["620"] = (true, false), ["621"] = (false, true), ["622"] = (true, false),
        ["623"] = (true, false), ["624"] = (true, false), ["625"] = (false, true), ["626"] = (true, true)
    };
    // c_UsoCFDI: the personal deductions (D01–D10) and payroll (CN01) are only for a persona física.
    static readonly string[] Uses = ["G01", "G02", "G03", "I01", "I02", "I03", "I04", "I05", "I06", "I07", "I08", "S01", "CP01"];
    static readonly string[] PersonalUses = ["D01", "D02", "D03", "D04", "D05", "D06", "D07", "D08", "D09", "D10", "CN01"];
    public static string? ReceiverProblem(string rfc, string regime, string use) {

        if (RfcProblem(rfc) is { } bad) return bad;
        // Público en general and foreign receivers: régimen 616 and use S01, the only combination the SAT accepts.
        if (rfc is GenericRfc or ForeignRfc) return regime == "616" && use == "S01" ? null : "RFC genérico: régimen 616 y uso S01";
        if (!Regimes.TryGetValue(regime, out var applies)) return "Régimen fiscal: clave del catálogo c_RegimenFiscal del SAT";
        if (IsMoral(rfc) ? !applies.Moral : !applies.Fisica) return "Régimen fiscal " + regime + " no aplica a una persona " + (IsMoral(rfc) ? "moral" : "física");
        if (PersonalUses.Contains(use)) return IsMoral(rfc) ? "Uso CFDI " + use + " sólo aplica a una persona física" : null;
        return Uses.Contains(use) ? null : "Uso CFDI: clave del catálogo c_UsoCFDI del SAT";

    }
}
