namespace MesaNexo.Core;

// Every rule that turns a rate into money. Amounts are whole centavos in a long and prices include IVA; docs/POLITICA-MONETARIA.md
// is the policy these implement. A half centavo rounds away from zero, and what is shared out is never rounded piece by piece:
// Allocate hands the residue to the largest remainders, so the parts always add up to the whole.
public static class Cents {
    public static long Round(decimal value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);
    public static long Percent(long amount, decimal percent) => Round(amount * percent / 100m);
    public static long Bps(long amount, long bps) => Round(amount * (decimal)bps / 10000m);
    // Rewards in the customer's favor are never rounded up.
    public static long BpsFloor(long amount, long bps) => (long)Math.Floor(amount * (decimal)bps / 10000m);
    // The IVA inside a price that already includes it.
    public static long IncludedTax(long amount, long bps) => Round(amount * (decimal)bps / (10000 + bps));
    public static long[] Allocate(long amount, IReadOnlyList<long> weights) {

        var sum = weights.Sum();
        Rules.Need(amount >= 0 && weights.All(x => x >= 0), "Distribución inválida");
        if (sum == 0) { Rules.Need(amount == 0, "Distribución sin base"); return new long[weights.Count]; }
        var raw = weights.Select(x => amount * (decimal)x / sum).ToArray();
        var result = raw.Select(x => (long)decimal.Floor(x)).ToArray();
        var left = amount - result.Sum();
        foreach (var i in Enumerable.Range(0, weights.Count).OrderByDescending(i => raw[i] - result[i]).ThenBy(i => i).Take(checked((int)left))) result[i]++;
        return result;

    }
}
