namespace IsogradIA;

/// Emballage des datasets libres dans les besoins des modèles à valeur pleine, type par type,
/// par recuit simulé. Chaque besoin [lb, ub] est un "bac" ; on minimise le déficit total
/// (somme hors intervalle), ce qui permet d'utiliser presque tout le volume libre.
public static class FreePacker
{
    /// bins : (lb, ub) de chaque besoin ; sizes : tailles des datasets libres.
    /// Retourne pour chaque dataset l'indice du bac (-1 = non utilisé).
    static double Env(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    public static long W = (long)Env("PACK_W", 4), F = (long)Env("PACK_F", 5000), FV = (long)Env("PACK_FV", 0);
    public static double T0 = Env("PACK_T0", 300), T1 = Env("PACK_T1", 1);

    public static int[] Pack(int[] lb, int[] ub, int[] sizes, Random rng, long iterations, long[]? fail = null,
        bool[]? binFree = null, bool[]? itemCopy = null)
    {
        // Un dataset sous copyright est interdit dans un bac "libre seulement" (modèle à valeur pleine).
        bool Bad(int b, int i) => binFree != null && b >= 0 && binFree[b] && itemCopy![i];
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
                if (sum[b] + sizes[i] > ub[b] || Bad(b, i)) continue;
                bin[i] = b; sum[b] += sizes[i]; items.RemoveAt(idx); idx--;
            }
        }
        // Déficit fortement pénalisé ; le surplus au-dessus de lb coûte 1 (laisse du libre aux autres).
        fail ??= Enumerable.Repeat(F, nb).ToArray();
        long Cost(int b, long s) => s < lb[b] ? fail[b] + W * (lb[b] - s) : s > ub[b] ? fail[b] + W * (s - ub[b]) + (ub[b] - lb[b]) : s - lb[b];
        long total = 0;
        for (int b = 0; b < nb; b++) total += Cost(b, sum[b]);
        for (int i = 0; i < ni; i++) if (Bad(bin[i], i)) total += fail[bin[i]];
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
                if (Bad(from, i)) d -= fail[from];
                if (Bad(to, i)) d += fail[to];
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
                if (binFree != null)
                {
                    if (from >= 0) d += ((Bad(from, j) ? 1 : 0) - (Bad(from, i) ? 1 : 0)) * fail[from];
                    if (bj >= 0) d += ((Bad(bj, i) ? 1 : 0) - (Bad(bj, j) ? 1 : 0)) * fail[bj];
                }
                if (d <= 0 || rng.NextDouble() < Math.Exp(-d / temp))
                {
                    if (from >= 0) sum[from] += delta;
                    if (bj >= 0) sum[bj] -= delta;
                    bin[i] = bj; bin[j] = from; total += d;
                }
            }
            if (total < best) { best = total; Array.Copy(bin, bestBin, ni); }
        }
        Repair(lb, ub, sizes, bestBin, rng, Bad);
        if (Environment.GetEnvironmentVariable("PACK_DEBUG") == "1")
        {
            var sm = new long[nb];
            for (int i = 0; i < ni; i++) if (bestBin[i] >= 0) sm[bestBin[i]] += sizes[i];
            for (int b = 0; b < nb; b++)
            {
                if (sm[b] >= lb[b] && sm[b] <= ub[b]) continue;
                var mine = Enumerable.Range(0, ni).Where(i => bestBin[i] == b).ToList();
                Console.WriteLine($"   bac {b} lb={lb[b]} ub={ub[b]} somme={sm[b]} datasets=[{string.Join(",", mine.Select(i => sizes[i]))}] pool={bestBin.Count(x => x < 0)}");
                int fixes = 0;
                foreach (int x in mine)
                    for (int j = 0; j < ni; j++)
                    {
                        int B = bestBin[j]; if (B == b) continue;
                        long d = sizes[j] - sizes[x];
                        long ns = sm[b] + d; if (ns < lb[b] || ns > ub[b]) continue;
                        if (B >= 0 && (sm[B] - d < lb[B] || sm[B] - d > ub[B])) continue;
                        fixes++;
                    }
                Console.WriteLine($"   échanges réparateurs : {fixes}; surplus total {Enumerable.Range(0, nb).Where(c => sm[c] >= lb[c]).Sum(c => sm[c] - lb[c])}");
            }
        }
        return bestBin;
    }

    /// Réparation exacte après le recuit : pour chaque bac en échec, on réemballe exhaustivement
    /// ses datasets avec ceux d'un ou deux autres bacs (et de la réserve) pour que tous soient satisfaits.
    static void Repair(int[] lb, int[] ub, int[] sizes, int[] bin, Random rng, Func<int, int, bool> bad)
    {
        int nb = lb.Length, ni = sizes.Length;
        var sum = new long[nb];
        var members = Enumerable.Range(0, nb).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < ni; i++) if (bin[i] >= 0) { sum[bin[i]] += sizes[i]; members[bin[i]].Add(i); }
        bool Ok(int b) => sum[b] >= lb[b] && sum[b] <= ub[b] && !members[b].Any(i => bad(b, i));
        // Petits datasets de la réserve, utilisables en plus.
        var poolSmall = Enumerable.Range(0, ni).Where(i => bin[i] < 0).OrderBy(i => sizes[i]).Take(4).ToList();

        // Essaie de répartir items entre les bacs bs (chacun satisfait) ; les items non placés vont en réserve
        // seulement s'ils viennent de la réserve.
        bool TryGroup(int[] bs, List<int> items)
        {
            int n = items.Count, k = bs.Length;
            if (n > (k == 2 ? 14 : 10)) return false;
            var assign = new int[n];
            var acc = new long[k];
            bool Rec(int idx)
            {
                if (idx == n)
                {
                    for (int q = 0; q < k; q++) if (acc[q] < lb[bs[q]]) return false;
                    return true;
                }
                int it = items[idx];
                for (int q = (bin[it] < 0 ? -1 : 0); q < k; q++)
                {
                    if (q >= 0 && (acc[q] + sizes[it] > ub[bs[q]] || bad(bs[q], it))) continue;
                    assign[idx] = q;
                    if (q >= 0) acc[q] += sizes[it];
                    bool ok = Rec(idx + 1);
                    if (q >= 0) acc[q] -= sizes[it];
                    if (ok) return true;
                }
                return false;
            }
            if (!Rec(0)) return false;
            for (int q = 0; q < k; q++) { members[bs[q]].Clear(); sum[bs[q]] = 0; }
            for (int idx = 0; idx < n; idx++)
            {
                int it = items[idx], q = assign[idx];
                bin[it] = q < 0 ? -1 : bs[q];
                if (q >= 0) { members[bs[q]].Add(it); sum[bs[q]] += sizes[it]; }
            }
            return true;
        }

        int failing = Enumerable.Range(0, nb).Count(b => !Ok(b));
        if (failing > 40) return;   // trop d'échecs : la réparation exhaustive serait trop lente
        for (int b = 0; b < nb; b++)
        {
            if (Ok(b)) continue;
            bool done = false;
            var partners = Enumerable.Range(0, nb).Where(c => c != b && Ok(c)).OrderBy(_ => rng.Next()).Take(400).ToList();
            foreach (int c in partners)
            {
                var items = members[b].Concat(members[c]).Concat(poolSmall.Where(i => bin[i] < 0)).ToList();
                if (TryGroup(new[] { b, c }, items)) { done = true; break; }
            }
            for (int tries = 0; !done && tries < 3000; tries++)
            {
                int c = partners[rng.Next(partners.Count)], d = partners[rng.Next(partners.Count)];
                if (c == d) continue;
                var items = members[b].Concat(members[c]).Concat(members[d]).ToList();
                if (TryGroup(new[] { b, c, d }, items)) done = true;
            }
        }
    }

    /// Bacs non satisfaits pour une affectation donnée.
    public static bool[] Failed(int[] lb, int[] ub, int[] sizes, int[] bin, bool[]? binFree = null, bool[]? itemCopy = null)
    {
        var sum = new long[lb.Length];
        var bad = new bool[lb.Length];
        for (int i = 0; i < sizes.Length; i++)
            if (bin[i] >= 0)
            {
                sum[bin[i]] += sizes[i];
                if (binFree != null && binFree[bin[i]] && itemCopy![i]) bad[bin[i]] = true;
            }
        return Enumerable.Range(0, lb.Length).Select(b => bad[b] || sum[b] < lb[b] || sum[b] > ub[b]).ToArray();
    }

    /// Réalise tout un plan MIP : modèles à valeur pleine (datasets libres seulement), modèles à valeur/2
    /// (datasets quelconques sauf pour les besoins couverts par une source) et sources (un besoin chacune).
    /// Tous les datasets d'un type sont emballés ensemble. Les modèles en échec sont retirés et on recommence ;
    /// une cible à valeur/2 sans assez de sources de son type est retirée aussi.
    /// Retourne la solution partielle (datasets et sources appariées aux besoins couverts par jeton).
    public static Solution RealizeAll(Instance inst, LpGuide mip, Random rng, long iterations, Action<string>? log = null)
    {
        int n = inst.Models.Length;
        var full = Enumerable.Range(0, n).Where(id => mip.Xf[id] > 0.5).ToHashSet();
        var half = Enumerable.Range(0, n).Where(id => mip.Xh[id] > 0.5).ToHashSet();
        var src = Enumerable.Range(0, n).Where(id => mip.Xs[id] > 0.5).ToHashSet();
        bool Token(int id, int k) => !inst.Models[id].ReqOk(k) || mip.TokenReq[id][k];
        while (true)
        {
            // Jetons : chaque type doit avoir au moins autant de sources que de besoins couverts par jeton.
            for (int t = 0; t < 4; t++)
            {
                int ns = src.Count(id => inst.Models[id].Types[0] == t);
                var needers = half.Where(id => Enumerable.Range(0, inst.Models[id].Types.Length).Any(k => inst.Models[id].Types[k] == t && Token(id, k)))
                    .OrderBy(id => inst.Models[id].Value).ToList();
                int need = needers.Sum(id => Enumerable.Range(0, inst.Models[id].Types.Length).Count(k => inst.Models[id].Types[k] == t && Token(id, k)));
                foreach (int id in needers)
                {
                    if (need <= ns) break;
                    half.Remove(id);
                    need -= Enumerable.Range(0, inst.Models[id].Types.Length).Count(k => inst.Models[id].Types[k] == t && Token(id, k));
                }
            }
            var maps = new List<(int data, int model)>();
            var failedModels = new HashSet<int>();
            for (int t = 0; t < 4; t++)
            {
                var bins = new List<(int model, int k, bool free)>();
                foreach (int id in full) { var m = inst.Models[id]; for (int k = 0; k < m.Types.Length; k++) if (m.Types[k] == t) bins.Add((id, k, true)); }
                foreach (int id in half) { var m = inst.Models[id]; for (int k = 0; k < m.Types.Length; k++) if (m.Types[k] == t && !Token(id, k)) bins.Add((id, k, false)); }
                foreach (int id in src) if (inst.Models[id].Types[0] == t) bins.Add((id, 0, false));
                var data = inst.Datasets.Where(d => d.Type == t).ToArray();
                int[] lb = bins.Select(x => inst.Models[x.model].Lb[x.k]).ToArray();
                int[] ub = bins.Select(x => inst.Models[x.model].Ub[x.k]).ToArray();
                bool[] bf = bins.Select(x => x.free).ToArray();
                bool[] ic = data.Select(d => d.Copy).ToArray();
                int[] sizes = data.Select(d => d.Size).ToArray();
                var fl = bins.Select(x => F + FV * inst.Models[x.model].Value).ToArray();
                var bin = Pack(lb, ub, sizes, rng, iterations, fl, bf, ic);
                var failed = Failed(lb, ub, sizes, bin, bf, ic);
                int nf = 0;
                for (int b = 0; b < bins.Count; b++) if (failed[b]) { failedModels.Add(bins[b].model); nf++; }
                for (int i = 0; i < data.Length; i++) if (bin[i] >= 0) maps.Add((data[i].Id, bins[bin[i]].model));
                long used = Enumerable.Range(0, data.Length).Where(i => bin[i] >= 0).Sum(i => (long)sizes[i]);
                log?.Invoke($"   type {"ntic"[t]} : {bins.Count} besoins, {nf} échecs, données {used}/{sizes.Sum(x => (long)x)}, somme lb {lb.Sum(x => (long)x)}");
            }
            if (failedModels.Count == 0)
            {
                // Appariement sources -> besoins couverts par jeton (n'importe quelle source du type convient).
                var free = Enumerable.Range(0, 4).Select(t => new Queue<int>(src.Where(id => inst.Models[id].Types[0] == t))).ToArray();
                var mm = new List<(int, int)>();
                foreach (int id in half)
                {
                    var m = inst.Models[id];
                    for (int k = 0; k < m.Types.Length; k++)
                        if (Token(id, k)) mm.Add((free[m.Types[k]].Dequeue(), id));
                }
                var usedSrc = mm.Select(x => x.Item1).ToHashSet();
                maps.RemoveAll(x => src.Contains(x.model) && !usedSrc.Contains(x.model));
                return new Solution { DataMappings = maps, ModelMappings = mm };
            }
            log?.Invoke($"   {failedModels.Count} modèles retirés (valeur {failedModels.Sum(id => inst.Models[id].Value)})");
            full.ExceptWith(failedModels); half.ExceptWith(failedModels); src.ExceptWith(failedModels);
        }
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
                var fl = bins.Select(x => F + FV * inst.Models[x.model].Value).ToArray();
                var bin = Pack(lb, ub, sizes, rng, iterations, fl);
                var failed = Failed(lb, ub, sizes, bin);
                int nf = 0;
                for (int b = 0; b < bins.Count; b++) if (failed[b]) { failedModels.Add(bins[b].model); nf++; }
                for (int i = 0; i < data.Length; i++) if (bin[i] >= 0) result.Add((data[i].Id, bins[bin[i]].model));
                long used = Enumerable.Range(0, data.Length).Where(i => bin[i] >= 0).Sum(i => (long)sizes[i]);
                long defi = 0; { var sm = new long[lb.Length]; for (int i = 0; i < data.Length; i++) if (bin[i] >= 0) sm[bin[i]] += sizes[i]; for (int b = 0; b < lb.Length; b++) defi += Math.Max(0, lb[b] - sm[b]); }
                log?.Invoke($"   type {"ntic"[t]} : {bins.Count} besoins, {nf} échecs (déficit {defi}, valeur {Enumerable.Range(0, bins.Count).Where(b => failed[b]).Sum(b => inst.Models[bins[b].model].Value)}), libre {used}/{sizes.Sum(x => (long)x)}, somme lb {lb.Sum(x => (long)x)}");
            }
            if (once) log?.Invoke($"   valeur des modèles en échec : {failedModels.Sum(id => inst.Models[id].Value)}");
            if (failedModels.Count == 0 || once) return result;
            log?.Invoke($"   {failedModels.Count} modèles retirés (valeur {failedModels.Sum(id => inst.Models[id].Value)})");
            full.ExceptWith(failedModels);
        }
    }
}
