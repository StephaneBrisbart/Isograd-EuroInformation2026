namespace IsogradIA;

/// Emballage des datasets libres dans les besoins des modèles à valeur pleine, type par type,
/// par recuit simulé. Chaque besoin [lb, ub] est un "bac" ; on minimise le déficit total
/// (somme hors intervalle), ce qui permet d'utiliser presque tout le volume libre.
public static class FreePacker
{
    /// bins : (lb, ub) de chaque besoin ; sizes : tailles des datasets libres.
    /// Retourne pour chaque dataset l'indice du bac (-1 = non utilisé).
    static double Env(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    public static long W = (long)Env("PACK_W", 32);
    public static double T0 = Env("PACK_T0", 200), T1 = Env("PACK_T1", 0.5);

    public static int[] Pack(int[] lb, int[] ub, int[] sizes, Random rng, long iterations)
    {
        int nb = lb.Length, ni = sizes.Length;
        var bin = new int[ni];
        var sum = new long[nb];
        Array.Fill(bin, -1);
        // Départ glouton : besoins les plus gros d'abord, plus gros dataset qui rentre.
        var items = Enumerable.Range(0, ni).OrderByDescending(i => sizes[i]).ToList();
        foreach (int b in Enumerable.Range(0, nb).OrderByDescending(b => lb[b]))
        {
            for (int idx = 0; idx < items.Count && sum[b] < lb[b]; idx++)
            {
                int i = items[idx];
                if (sum[b] + sizes[i] > ub[b]) continue;
                bin[i] = b; sum[b] += sizes[i]; items.RemoveAt(idx); idx--;
            }
        }
        // Déficit fortement pénalisé ; le surplus au-dessus de lb coûte 1 (laisse du libre aux autres).
        long Cost(int b, long s) => s < lb[b] ? W * (lb[b] - s) : s > ub[b] ? 2 * W * (s - ub[b]) + (ub[b] - lb[b]) : s - lb[b];
        long total = 0;
        for (int b = 0; b < nb; b++) total += Cost(b, sum[b]);
        long best = total; var bestBin = (int[])bin.Clone();
        double t0 = T0, t1 = T1;
        var deficit = new List<int>();
        for (long it = 0; it < iterations; it++)
        {
            if ((it & 4095) == 0)
            {
                deficit.Clear();
                for (int b = 0; b < nb; b++) if (sum[b] < lb[b] || sum[b] > ub[b]) deficit.Add(b);
            }
            double temp = t0 * Math.Pow(t1 / t0, (double)it / iterations);
            int i = rng.Next(ni);
            int from = bin[i];
            if (rng.Next(2) == 0)
            {
                // Déplacement : vers un bac en déficit de préférence, ou vers la réserve.
                int to = deficit.Count > 0 && rng.Next(3) > 0 ? deficit[rng.Next(deficit.Count)] : rng.Next(nb + 1) - 1;
                if (to == from) continue;
                long d = 0;
                if (from >= 0) d += Cost(from, sum[from] - sizes[i]) - Cost(from, sum[from]);
                if (to >= 0) d += Cost(to, sum[to] + sizes[i]) - Cost(to, sum[to]);
                if (d <= 0 || rng.NextDouble() < Math.Exp(-d / temp))
                {
                    if (from >= 0) sum[from] -= sizes[i];
                    if (to >= 0) sum[to] += sizes[i];
                    bin[i] = to; total += d;
                }
            }
            else
            {
                // Échange de deux datasets entre bacs (ou avec la réserve).
                int j = rng.Next(ni);
                int bj = bin[j];
                if (bj == from) continue;
                long delta = sizes[j] - sizes[i];
                long d = 0;
                if (from >= 0) d += Cost(from, sum[from] + delta) - Cost(from, sum[from]);
                if (bj >= 0) d += Cost(bj, sum[bj] - delta) - Cost(bj, sum[bj]);
                if (d <= 0 || rng.NextDouble() < Math.Exp(-d / temp))
                {
                    if (from >= 0) sum[from] += delta;
                    if (bj >= 0) sum[bj] -= delta;
                    bin[i] = bj; bin[j] = from; total += d;
                }
            }
            if (total < best) { best = total; Array.Copy(bin, bestBin, ni); }
        }
        return bestBin;
    }

    /// Bacs non satisfaits pour une affectation donnée.
    public static bool[] Failed(int[] lb, int[] ub, int[] sizes, int[] bin)
    {
        var sum = new long[lb.Length];
        for (int i = 0; i < sizes.Length; i++) if (bin[i] >= 0) sum[bin[i]] += sizes[i];
        return Enumerable.Range(0, lb.Length).Select(b => sum[b] < lb[b] || sum[b] > ub[b]).ToArray();
    }

    /// Réalise l'ensemble "full" (modèles à valeur pleine) : emballe chaque type, retire les modèles
    /// dont un besoin échoue, recommence. Retourne les affectations datasets -> modèle.
    public static List<(int data, int model)> Realize(Instance inst, HashSet<int> full, Random rng, long iterations, Action<string>? log = null, bool once = false)
    {
        while (true)
        {
            var result = new List<(int, int)>();
            var failedModels = new HashSet<int>();
            for (int t = 0; t < 4; t++)
            {
                var bins = new List<(int model, int k)>();
                foreach (int id in full)
                {
                    var m = inst.Models[id];
                    for (int k = 0; k < m.Types.Length; k++) if (m.Types[k] == t) bins.Add((id, k));
                }
                var data = inst.Datasets.Where(d => d.Type == t && !d.Copy).ToArray();
                int[] lb = bins.Select(x => inst.Models[x.model].Lb[x.k]).ToArray();
                int[] ub = bins.Select(x => inst.Models[x.model].Ub[x.k]).ToArray();
                int[] sizes = data.Select(d => d.Size).ToArray();
                var bin = Pack(lb, ub, sizes, rng, iterations);
                var failed = Failed(lb, ub, sizes, bin);
                int nf = 0;
                for (int b = 0; b < bins.Count; b++) if (failed[b]) { failedModels.Add(bins[b].model); nf++; }
                for (int i = 0; i < data.Length; i++) if (bin[i] >= 0) result.Add((data[i].Id, bins[bin[i]].model));
                long used = Enumerable.Range(0, data.Length).Where(i => bin[i] >= 0).Sum(i => (long)sizes[i]);
                long defi = 0; { var sm = new long[lb.Length]; for (int i = 0; i < data.Length; i++) if (bin[i] >= 0) sm[bin[i]] += sizes[i]; for (int b = 0; b < lb.Length; b++) defi += Math.Max(0, lb[b] - sm[b]); }
                log?.Invoke($"   type {"ntic"[t]} : {bins.Count} besoins, {nf} échecs (déficit {defi}), libre {used}/{sizes.Sum(x => (long)x)}, somme lb {lb.Sum(x => (long)x)}");
            }
            if (failedModels.Count == 0 || once) return result;
            log?.Invoke($"   {failedModels.Count} modèles retirés (valeur {failedModels.Sum(id => inst.Models[id].Value)})");
            full.ExceptWith(failedModels);
        }
    }
}
