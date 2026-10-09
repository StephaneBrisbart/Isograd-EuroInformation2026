// Concours Isograd / Euro-Information 2026 - "IA responsables"
// Glouton paramétré : on trie les modèles par rentabilité (valeur / coût en ressources),
// on remplit chaque besoin avec des datasets (libres de droits en priorité),
// et on peut compléter un besoin avec un modèle mono-type utilisé comme source.
// Plusieurs jeux de paramètres sont essayés en parallèle, on garde le meilleur.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IsogradIA;

public sealed class Model
{
    public int Id;
    public long Value;
    public long Cost;
    public int[] Types = Array.Empty<int>(); // 0=n 1=t 2=i 3=c
    public int[] Lb = Array.Empty<int>();
    public int[] Ub = Array.Empty<int>();
    public bool ReqOk(int k) => Lb[k] <= Ub[k] && Ub[k] > 0;
    // Tous les besoins sont réalisables avec des datasets. Sinon, un besoin impossible
    // (borne sup négative...) peut quand même être couvert par une source : le vérificateur ne le contrôle pas.
    public bool AllReqOk => Lb.Length > 0 && Enumerable.Range(0, Lb.Length).All(ReqOk);
    public bool IsMono => Types.Length == 1;
}

public sealed class DataSet
{
    public int Id;
    public int Type;
    public int Size;
    public bool Copy;
}

public sealed class Instance
{
    public long EnergyCap;
    public Model[] Models = Array.Empty<Model>();
    public DataSet[] Datasets = Array.Empty<DataSet>();
    public long[] Supply = new long[4];
    public long[] FreeSupply = new long[4];

    public static int TypeIndex(string s) => "ntic".IndexOf(s[0]);

    public static Instance Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var inst = new Instance { EnergyCap = root.GetProperty("energyCap").GetInt64() };

        var models = new List<Model>();
        foreach (var m in root.GetProperty("models").EnumerateArray())
        {
            var types = new List<int>(); var lb = new List<int>(); var ub = new List<int>();
            foreach (var req in m.GetProperty("dataRequirements").EnumerateObject())
            {
                types.Add(TypeIndex(req.Name));
                lb.Add(req.Value.GetProperty("lowerBound").GetInt32());
                ub.Add(req.Value.GetProperty("upperBound").GetInt32());
            }
            models.Add(new Model
            {
                Id = m.GetProperty("id").GetInt32(),
                Value = m.GetProperty("value").GetInt64(),
                Cost = m.GetProperty("energyCost").GetInt64(),
                Types = types.ToArray(), Lb = lb.ToArray(), Ub = ub.ToArray()
            });
        }
        var datasets = new List<DataSet>();
        foreach (var d in root.GetProperty("datasets").EnumerateArray())
        {
            datasets.Add(new DataSet
            {
                Id = d.GetProperty("id").GetInt32(),
                Type = TypeIndex(d.GetProperty("dataType").GetString()!),
                Size = d.GetProperty("size").GetInt32(),
                Copy = d.GetProperty("isCopyrighted").GetInt32() != 0
            });
        }
        inst.Models = models.OrderBy(m => m.Id).ToArray();
        inst.Datasets = datasets.OrderBy(d => d.Id).ToArray();
        foreach (var d in inst.Datasets)
        {
            inst.Supply[d.Type] += d.Size;
            if (!d.Copy) inst.FreeSupply[d.Type] += d.Size;
        }
        return inst;
    }
}

public sealed class Solution
{
    public long Score;
    public string Label = "";
    public List<(int data, int model)> DataMappings = new();
    public List<(int source, int target)> ModelMappings = new();

    public static Solution FromJson(string json)
    {
        var sol = new Solution();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.GetProperty("dataMappings").EnumerateArray())
                sol.DataMappings.Add((p[0].GetInt32(), p[1].GetInt32()));
            foreach (var p in doc.RootElement.GetProperty("modelMappings").EnumerateArray())
                sol.ModelMappings.Add((p[0].GetInt32(), p[1].GetInt32()));
        }
        catch (Exception) { return new Solution(); }
        return sol;
    }

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"dataMappings\": [");
        sb.Append(string.Join(",", DataMappings.Select(p => $"\n    [{p.data},{p.model}]")));
        sb.Append("\n  ],\n  \"modelMappings\": [");
        sb.Append(string.Join(",", ModelMappings.Select(p => $"\n    [{p.source},{p.target}]")));
        sb.Append("\n  ]\n}\n");
        return sb.ToString();
    }
}

public sealed class Params
{
    public double LambdaFree;       // poids des données libres de droits (relatif à l'énergie)
    public double LambdaCopy;       // poids des données sous copyright (relatif à l'énergie)
    public bool Sources = true;     // autoriser les modèles mono-type comme sources
    public double SrcQuantile;      // quantile des sources utilisé pour estimer leur coût
    public double Noise;            // bruit multiplicatif sur les priorités (recherche aléatoire)
    public int Seed;
    public int PairTries = 300;     // nombre de candidats essayés pour finir avec une paire
    public bool MinWaste;           // finir un besoin avec le minimum de surplus (seul ou paire)
    public int WasteTolerance = 0;  // surplus accepté sans chercher mieux
    public int SourceLookahead = 1; // nombre de sources comparées sur leur gaspillage réel
    public bool ReserveSources;     // appariement global datasets -> sources avant la construction
    public double ReserveThreshold = 0.5;
    public int FreePenalty = 300;   // surcoût (en taille) d'un dataset libre réservé à une source
    public Prices? Prices;          // si renseigné : tri par profit réduit lagrangien
    public LpGuide? Lp;             // si renseigné : construction guidée par la relaxation linéaire
    public double LpWeight = 1;     // poids du guide LP face au profit réduit
    public override string ToString() =>
        (Lp != null ? $"lp(w={LpWeight:G2} look={SourceLookahead} tol={(MinWaste ? WasteTolerance : -1)}{(ReserveSources ? $" réserve fp={FreePenalty}" : "")}) " : Prices != null ? "lagrange " : "") + $"lambdaFree={LambdaFree:G3} lambdaCopy={LambdaCopy:G3} sources={Sources} q={SrcQuantile:G2} noise={Noise:G2} seed={Seed}";
}

/// Prix (multiplicateurs de Lagrange) des ressources, par unité de capacité totale.
public sealed class Prices
{
    public double Energy;
    public double[] Free = new double[4];
    public double[] Total = new double[4];
    public double[] Token = new double[4];   // prix d'un besoin couvert par une source

    public Prices Scaled(Random r, double sigma)
    {
        double f() => Math.Exp(sigma * (r.NextDouble() * 2 - 1));
        return new Prices { Energy = Energy * f(), Free = Free.Select(x => x * f()).ToArray(), Total = Total.Select(x => x * f()).ToArray(), Token = Token.Select(x => x * f()).ToArray() };
    }

    /// Sous-gradient multiplicatif sur la relaxation : chaque modèle choisit seul entre rien,
    /// valeur pleine (données libres), valeur/2 (besoins en données ou en jetons source) et source.
    public static Prices Compute(Instance inst, bool sources, int iterations = 3000)
    {
        var ms = inst.Models.Where(m => m.Lb.Length > 0).ToArray();
        int n = ms.Length;
        var e = ms.Select(m => (double)m.Cost / inst.EnergyCap).ToArray();
        var reqs = ms.Select(m => Enumerable.Range(0, m.Lb.Length).Select(k => (
            t: m.Types[k],
            tot: (double)Math.Max(m.Lb[k], 0) / Math.Max(1, inst.Supply[m.Types[k]]),
            fre: inst.FreeSupply[m.Types[k]] > 0 ? (double)Math.Max(m.Lb[k], 0) / inst.FreeSupply[m.Types[k]] : 1e9,
            ok: m.ReqOk(k))).ToArray()).ToArray();
        var hasSrc = new bool[4];
        foreach (var m in ms) if (sources && m.IsMono && m.AllReqOk) hasSrc[m.Types[0]] = true;

        // Prix initial de l'énergie : ratio valeur/énergie marginal du sac à dos fractionnaire.
        var byRatio = Enumerable.Range(0, n).Where(j => ms[j].Value > 0).OrderByDescending(j => ms[j].Value / Math.Max(e[j], 1e-18)).ToArray();
        double acc = 0, pe = 0;
        foreach (int j in byRatio) { acc += e[j]; pe = ms[j].Value / Math.Max(e[j], 1e-18); if (acc >= 1) break; }
        var p = new Prices { Energy = pe };
        double avgV = ms.Average(m => (double)m.Value);
        for (int t = 0; t < 4; t++) { p.Free[t] = pe * 0.05; p.Total[t] = pe * 0.05; p.Token[t] = hasSrc[t] ? avgV * 0.2 : 1e18; }
        double floor = pe * 1e-6;

        for (int it = 0; it < iterations; it++)
        {
            double eta = 0.3 * Math.Pow(0.003 / 0.3, (double)it / iterations);
            double ue = 0; var uf = new double[4]; var ut = new double[4]; var dem = new double[4]; var sup = new double[4];
            for (int j = 0; j < n; j++)
            {
                var m = ms[j];
                double ce = p.Energy * e[j];
                double pf = m.AllReqOk && m.Value > 0 ? m.Value - ce : double.NegativeInfinity;
                double ph = m.Value > 0 ? m.Value / 2.0 - ce : double.NegativeInfinity;
                foreach (var r in reqs[j])
                {
                    double dt = p.Total[r.t] * r.tot;
                    pf -= dt + p.Free[r.t] * r.fre;
                    ph -= r.ok ? Math.Min(dt, p.Token[r.t]) : p.Token[r.t];
                }
                double ps = hasSrc[m.Types[0]] && m.IsMono && m.AllReqOk ? p.Token[m.Types[0]] - ce - p.Total[m.Types[0]] * reqs[j][0].tot : double.NegativeInfinity;
                double best = Math.Max(Math.Max(pf, ph), ps);
                if (best <= 0) continue;
                ue += e[j];
                if (best == ps) { sup[m.Types[0]] += 1; ut[m.Types[0]] += reqs[j][0].tot; }
                else if (best == pf) foreach (var r in reqs[j]) { ut[r.t] += r.tot; uf[r.t] += r.fre; }
                else foreach (var r in reqs[j])
                {
                    if (r.ok && p.Total[r.t] * r.tot <= p.Token[r.t]) ut[r.t] += r.tot; else dem[r.t] += 1;
                }
            }
            p.Energy = Math.Max(floor, p.Energy * Math.Exp(eta * Math.Clamp(ue - 1, -1, 1)));
            for (int t = 0; t < 4; t++)
            {
                p.Free[t] = Math.Max(floor, p.Free[t] * Math.Exp(eta * Math.Clamp(uf[t] - 1, -1, 1)));
                p.Total[t] = Math.Max(floor, p.Total[t] * Math.Exp(eta * Math.Clamp(ut[t] - 1, -1, 1)));
                if (hasSrc[t])
                    p.Token[t] = Math.Max(1e-3, p.Token[t] * Math.Exp(eta * Math.Clamp((dem[t] - sup[t]) / Math.Max(1, (dem[t] + sup[t]) / 2), -1, 1)));
            }
        }
        return p;
    }
}

/// Réserve de datasets disponibles, par type et par statut (0 = libre, 1 = copyright).
/// Clé = taille * K + id, ce qui permet des recherches par intervalle de taille.
public sealed class Pools
{
    const long K = 1_000_000;
    readonly SortedSet<long>[,] _sets = new SortedSet<long>[4, 2];

    public Pools(Instance inst)
    {
        for (int t = 0; t < 4; t++) for (int c = 0; c < 2; c++) _sets[t, c] = new SortedSet<long>();
        foreach (var d in inst.Datasets) _sets[d.Type, d.Copy ? 1 : 0].Add(d.Size * K + d.Id);
    }

    public static int IdOf(long key) => (int)(key % K);
    public static long KeyOf(int size, int id) => size * K + id;
    public static int SizeOf(long key) => (int)(key / K);

    public void Remove(int t, int c, long key) => _sets[t, c].Remove(key);
    public void Add(int t, int c, long key) => _sets[t, c].Add(key);

    /// Plus petit dataset dont la taille est dans [lo, hi], 0 si aucun.
    public long MinInRange(int t, int c, int lo, int hi)
    {
        if (lo < 1) lo = 1;
        if (hi < lo) return 0;
        var s = _sets[t, c];
        if (s.Count == 0) return 0;
        return s.GetViewBetween(lo * K, hi * K + K - 1).Min;
    }

    /// Datasets de taille <= hi, du plus grand au plus petit (ne pas modifier le pool pendant l'itération).
    public List<long> Descending(int t, int c, int hi, int max = 400)
    {
        var res = new List<long>();
        if (hi < 1 || _sets[t, c].Count == 0) return res;
        foreach (long k in _sets[t, c].GetViewBetween(0, hi * K + K - 1).Reverse())
        {
            res.Add(k);
            if (res.Count >= max) break;
        }
        return res;
    }

    /// Plus grand dataset de taille <= hi, 0 si aucun.
    public long MaxAtMost(int t, int c, int hi)
    {
        if (hi < 1) return 0;
        var s = _sets[t, c];
        if (s.Count == 0) return 0;
        return s.GetViewBetween(0, hi * K + K - 1).Max;
    }
}

public sealed class Solver
{
    readonly Instance _inst;
    readonly Params _p;
    readonly Pools _pools;
    long _energyLeft;
    readonly bool[] _used;          // modèle entraîné (cible ou source)
    readonly List<(int t, int c, long key)> _journal = new();
    // Affectation de chaque modèle cible entraîné : ses datasets, ses sources (et leurs datasets), sa valeur.
    sealed class Assign
    {
        public int[] Data = Array.Empty<int>();
        public (int src, int[] data)[] Sources = Array.Empty<(int, int[])>();
        public long Gain;
    }
    readonly Dictionary<int, Assign> _assign = new();
    long _score;
    (int id, bool direct)[] _order = Array.Empty<(int, bool)>();
    readonly List<int>[] _sourceCandidates = new List<int>[4];
    readonly int[] _sourcePtr = new int[4];
    readonly double[] _srcEstimate = new double[4];

    public Solver(Instance inst, Params p)
    {
        _inst = inst; _p = p;
        _pools = new Pools(inst);
        _energyLeft = inst.EnergyCap;
        _used = new bool[inst.Models.Length];
    }

    double EnergyW(long cost) => (double)cost / _inst.EnergyCap;
    // Poids d'une quantité de données prise dans les datasets libres / sous copyright.
    double FreeW(int t, int lb) => _p.LambdaFree * Math.Max(lb, 0) / Math.Max(1.0, _inst.FreeSupply[t]);
    double DataW(int t, int lb) => _p.LambdaCopy * Math.Max(lb, 0) /
        Math.Max(1.0, _inst.Supply[t] - _inst.FreeSupply[t] > 0 ? _inst.Supply[t] - _inst.FreeSupply[t] : _inst.Supply[t]);

    /// Poids "ressources" d'un modèle entraîné uniquement avec des datasets.
    double DirectWeight(Model m)
    {
        if (!m.AllReqOk) return double.PositiveInfinity;
        double w = EnergyW(m.Cost);
        for (int k = 0; k < m.Types.Length; k++) w += FreeW(m.Types[k], m.Lb[k]);
        return w + 1e-15;
    }

    /// Poids d'un modèle à valeur divisée par deux : chaque besoin prend le moins cher
    /// entre datasets directs et une source estimée.
    double HalfWeight(Model m)
    {
        double w = EnergyW(m.Cost);
        for (int k = 0; k < m.Types.Length; k++)
            w += m.ReqOk(k) ? Math.Min(DataW(m.Types[k], m.Lb[k]), _srcEstimate[m.Types[k]]) : _srcEstimate[m.Types[k]];
        return w + 1e-15;
    }

    double SourceWeight(Model s) => EnergyW(s.Cost) + DataW(s.Types[0], s.Lb[0]);

    /// Remplit un besoin [lb, ub] du type t avec les pools indiqués (dans l'ordre de préférence).
    /// Gros datasets d'abord, puis on termine par un ou deux datasets qui tombent pile dans l'intervalle.
    /// En cas d'échec, l'appelant fait le rollback via le journal.
    bool FillRequirement(int t, int lb, int ub, int[] classes, List<(int data, long key, int c)> taken)
    {
        if (ub < lb || ub <= 0) return false;
        int sum = 0;
        for (int iter = 0; iter < 1000; iter++)
        {
            int lo = lb - sum, hi = ub - sum;
            if (lo <= 0) return true;

            int maxSize = 0;
            foreach (int c in classes) maxSize = Math.Max(maxSize, Pools.SizeOf(_pools.MaxAtMost(t, c, int.MaxValue / 2)));
            if (maxSize == 0) return false;

            if (_p.MinWaste)
            {
                // Fin du remplissage avec le minimum de surplus au-delà du minimum (le volume est la ressource rare).
                // On épuise d'abord le pool préféré avant d'autoriser le suivant (les libres restent aux valeurs pleines).
                for (int n = 1; n <= classes.Length; n++)
                    if (FinishMinWaste(t, lo, hi, classes[..n], taken, maxSize)) return true;
            }
            else
            {
                // Un seul dataset suffit-il pour finir ? On prend le plus petit qui convient.
                foreach (int c in classes)
                {
                    long key = _pools.MinInRange(t, c, lo, hi);
                    if (key != 0) { Take(t, c, key, taken); return true; }
                }
                // Proche du but : on cherche une paire (a, b) avec a + b dans [lo, hi].
                if (lo <= 2 * maxSize && TryPair(t, lo, hi, classes, taken, int.MaxValue, firstFound: true)) return true;
            }

            // Sinon on prend le plus gros dataset qui ne fait pas encore atteindre le minimum.
            long best = 0; int bestC = -1;
            foreach (int c in classes)
            {
                long key = _pools.MaxAtMost(t, c, lo - 1);
                if (key != 0) { best = key; bestC = c; break; }
            }
            if (best == 0) return false;
            Take(t, bestC, best, taken);
            sum += Pools.SizeOf(best);
        }
        return false;
    }

    /// Cherche une paire (a, b) avec a + b dans [lo, hi] et un surplus strictement inférieur à beatWaste.
    bool FinishMinWaste(int t, int lo, int hi, int[] classes, List<(int data, long key, int c)> taken, int maxSize)
    {
        long single = 0; int singleC = -1, singleWaste = int.MaxValue;
        foreach (int c in classes)
        {
            long key = _pools.MinInRange(t, c, lo, hi);
            if (key != 0 && Pools.SizeOf(key) - lo < singleWaste) { single = key; singleC = c; singleWaste = Pools.SizeOf(key) - lo; }
        }
        if (single != 0 && singleWaste <= _p.WasteTolerance) { Take(t, singleC, single, taken); return true; }
        if (lo <= 2 * maxSize && TryPair(t, lo, hi, classes, taken, single != 0 ? singleWaste : int.MaxValue, firstFound: false)) return true;
        if (single != 0) { Take(t, singleC, single, taken); return true; }
        return false;
    }

    /// Cherche une paire (a, b) avec a + b dans [lo, hi] et un surplus strictement inférieur à beatWaste
    /// (la première trouvée si firstFound, sinon la meilleure parmi les candidats examinés).
    bool TryPair(int t, int lo, int hi, int[] classes, List<(int data, long key, int c)> taken, int beatWaste, bool firstFound)
    {
        long bestA = 0, bestB = 0; int bestCa = -1, bestCb = -1, bestW = beatWaste;
        foreach (int ca in classes)
        {
            foreach (long a in _pools.Descending(t, ca, lo - 1, _p.PairTries))
            {
                int sa = Pools.SizeOf(a);
                if (2 * sa < lo) break; // b serait plus grand que a : paire déjà essayée
                _pools.Remove(t, ca, a);
                foreach (int cb in classes)
                {
                    long b = _pools.MinInRange(t, cb, lo - sa, hi - sa);
                    if (b == 0) continue;
                    int w = sa + Pools.SizeOf(b) - lo;
                    if (w < bestW) { bestW = w; bestA = a; bestB = b; bestCa = ca; bestCb = cb; }
                    if (firstFound) break;
                }
                _pools.Add(t, ca, a);
                if (bestA != 0 && (firstFound || bestW <= _p.WasteTolerance)) break;
            }
            if (bestA != 0 && (firstFound || bestW <= _p.WasteTolerance)) break;
        }
        if (bestA == 0) return false;
        Take(t, bestCa, bestA, taken);
        Take(t, bestCb, bestB, taken);
        return true;
    }

    void Take(int t, int c, long key, List<(int data, long key, int c)> taken)
    {
        _pools.Remove(t, c, key);
        _journal.Add((t, c, key));
        taken.Add((Pools.IdOf(key), key, c));
    }

    void Rollback(int mark)
    {
        for (int i = _journal.Count - 1; i >= mark; i--)
        {
            var (t, c, key) = _journal[i];
            _pools.Add(t, c, key);
        }
        _journal.RemoveRange(mark, _journal.Count - mark);
    }

    static readonly int[] FreeOnly = { 0 };
    static readonly int[] CopyFirst = { 1, 0 };

    /// Poids de la prochaine source disponible du type t (infini s'il n'y en a plus).
    double PeekSource(int t)
    {
        var list = _sourceCandidates[t];
        while (_sourcePtr[t] < list.Count && _used[list[_sourcePtr[t]]]) _sourcePtr[t]++;
        if (_sourcePtr[t] >= list.Count) return double.PositiveInfinity;
        int id = list[_sourcePtr[t]];
        return _srcCost != null ? _srcCost[id] : SourceWeight(_inst.Models[id]);
    }

    /// Essaie d'entraîner un modèle mono-type comme source de données. Renvoie l'id ou -1.
    int TakeSource(int t, long energyBudget, List<(int data, int model)> dataOut)
    {
        var list = _sourceCandidates[t];
        PeekSource(t);
        int tries = 0;
        IEnumerable<int> candidates = Enumerable.Range(_sourcePtr[t], list.Count - _sourcePtr[t]);
        if (_p.SourceLookahead > 1)
        {
            // Appariement : parmi les prochaines sources, on préfère celle dont le dataset réel gaspille le moins.
            var scored = new List<(int i, double cost)>();
            for (int i = _sourcePtr[t]; i < list.Count && scored.Count < _p.SourceLookahead; i++)
            {
                var s = _inst.Models[list[i]];
                if (_used[s.Id] || s.Cost > energyBudget) continue;
                double baseCost = _srcCost != null ? _srcCost[s.Id] : SourceWeight(s);
                int vol = s.Lb[0];
                long key = 0;
                foreach (int c in CopyFirst)
                {
                    long k2 = _pools.MinInRange(t, c, s.Lb[0], s.Ub[0]);
                    if (k2 != 0 && (key == 0 || k2 / 1_000_000 < key / 1_000_000)) key = k2;
                }
                if (key != 0) vol = Pools.SizeOf(key);
                scored.Add((i, baseCost + ReqCost(t, vol) - ReqCost(t, s.Lb[0])));
            }
            candidates = scored.OrderBy(x => x.cost).Select(x => x.i).Concat(candidates);
        }
        foreach (int i in candidates)
        {
            if (tries >= 40) break;
            var s = _inst.Models[list[i]];
            if (_used[s.Id]) continue;
            if (s.Cost > energyBudget) continue;
            tries++;
            var taken = new List<(int, long, int)>();
            int mark = _journal.Count;
            if (_reserved != null && _reserved[s.Id] != 0)
            {
                // Dataset réservé à l'avance par l'appariement global : on le prend directement.
                long key = _reserved[s.Id];
                _reserved[s.Id] = 0;
                int c = _inst.Datasets[Pools.IdOf(key)].Copy ? 1 : 0;
                _pools.Add(t, c, key);
                Take(t, c, key, taken);
                _used[s.Id] = true;
                foreach (var (d, _, _) in taken) dataOut.Add((d, s.Id));
                return s.Id;
            }
            // La valeur d'une source n'est pas comptée : le copyright n'a pas d'importance.
            if (FillRequirement(t, s.Lb[0], s.Ub[0], CopyFirst, taken))
            {
                _used[s.Id] = true;
                foreach (var (d, _, _) in taken) dataOut.Add((d, s.Id));
                return s.Id;
            }
            Rollback(mark);
        }
        return -1;
    }

    bool TryFullFree(Model m, out long gained)
    {
        gained = 0;
        int mark = _journal.Count;
        var taken = new List<(int data, long key, int c)>();
        bool ok = true;
        for (int k = 0; k < m.Types.Length && ok; k++)
            ok = FillRequirement(m.Types[k], m.Lb[k], m.Ub[k], FreeOnly, taken);
        if (!ok) { Rollback(mark); return false; }
        gained = m.Value;
        Commit(m, taken, new List<(int, int)>(), new List<(int, int)>(), gained);
        return true;
    }

    bool TryHalf(Model m, out long gained)
    {
        gained = 0;
        int mark = _journal.Count;
        var markUsed = new List<int>();
        var taken = new List<(int data, long key, int c)>();
        var srcData = new List<(int, int)>();
        var srcMap = new List<(int, int)>();
        long budget = _energyLeft - m.Cost;
        bool ok = true;
        _used[m.Id] = true; // un modèle ne peut pas être sa propre source
        for (int k = 0; k < m.Types.Length && ok; k++)
        {
            int t = m.Types[k];
            bool sourceFirst = !m.ReqOk(k) || (_p.Sources &&
                (_p.Lp != null && _p.Lp.Xh[m.Id] > 0.01 ? _p.Lp.TokenReq[m.Id][k] : PeekSource(t) < ReqCost(t, m.Lb[k])));
            bool done = false;
            for (int attempt = 0; attempt < 2 && !done; attempt++)
            {
                bool useSource = (attempt == 0) == sourceFirst;
                if (useSource)
                {
                    if (!_p.Sources) continue;
                    if (!m.ReqOk(k) && attempt > 0) break;
                    int s = TakeSource(t, budget, srcData);
                    if (s >= 0) { budget -= _inst.Models[s].Cost; srcMap.Add((s, m.Id)); markUsed.Add(s); done = true; }
                }
                else if (m.ReqOk(k))
                {
                    int jm = _journal.Count; int tm = taken.Count;
                    done = FillRequirement(t, m.Lb[k], m.Ub[k], CopyFirst, taken);
                    if (!done) { Rollback(jm); taken.RemoveRange(tm, taken.Count - tm); }
                }
            }
            ok = done;
        }
        if (ok)
        {
            bool halved = srcMap.Count > 0 || taken.Any(x => x.c == 1);
            foreach (var (s, _) in srcMap) _energyLeft -= _inst.Models[s].Cost;
            gained = halved ? m.Value / 2 : m.Value;
            Commit(m, taken, srcData, srcMap, gained);
            return true;
        }
        Rollback(mark);
        foreach (int id in markUsed) _used[id] = false;
        _used[m.Id] = false;
        return false;
    }

    void Commit(Model m, List<(int data, long key, int c)> taken, List<(int, int)> srcData, List<(int, int)> srcMap, long gain)
    {
        _used[m.Id] = true;
        _energyLeft -= m.Cost;
        _assign[m.Id] = new Assign
        {
            Data = taken.Select(x => x.data).ToArray(),
            Sources = srcMap.Select(sm => (sm.Item1, srcData.Where(x => x.Item2 == sm.Item1).Select(x => x.Item1).ToArray())).ToArray(),
            Gain = gain
        };
        _score += gain;
        _journal.Clear();
    }

    long KeyOf(int dataId) { var d = _inst.Datasets[dataId]; return Pools.KeyOf(d.Size, d.Id); }

    /// Retire un modèle cible (et ses sources) de la solution : ressources rendues.
    Assign RemoveTarget(int id)
    {
        var a = _assign[id];
        _assign.Remove(id);
        foreach (int d in a.Data.Concat(a.Sources.SelectMany(x => x.data)))
            _pools.Add(_inst.Datasets[d].Type, _inst.Datasets[d].Copy ? 1 : 0, KeyOf(d));
        _energyLeft += _inst.Models[id].Cost;
        _used[id] = false;
        foreach (var (src, _) in a.Sources) { _energyLeft += _inst.Models[src].Cost; _used[src] = false; }
        _score -= a.Gain;
        return a;
    }

    /// Remet exactement une affectation retirée (ses ressources doivent être libres).
    void Restore(int id, Assign a)
    {
        foreach (int d in a.Data.Concat(a.Sources.SelectMany(x => x.data)))
            _pools.Remove(_inst.Datasets[d].Type, _inst.Datasets[d].Copy ? 1 : 0, KeyOf(d));
        _energyLeft -= _inst.Models[id].Cost;
        _used[id] = true;
        foreach (var (src, _) in a.Sources) { _energyLeft -= _inst.Models[src].Cost; _used[src] = true; }
        _score += a.Gain;
        _assign[id] = a;
    }

    /// Passe gloutonne : essaie d'entraîner, dans l'ordre de priorité, les modèles encore libres.
    List<int> InsertPass(HashSet<int>? tabu)
    {
        var inserted = new List<int>();
        foreach (var (id, direct) in _order)
        {
            if (_used[id] || (tabu != null && tabu.Contains(id))) continue;
            var m = _inst.Models[id];
            if (m.Cost > _energyLeft) continue;
            if ((direct && TryFullFree(m, out _)) || TryHalf(m, out _)) inserted.Add(id);
        }
        return inserted;
    }

    /// Recherche locale "détruire puis reconstruire" : on retire quelques modèles au hasard,
    /// on réinsère glouton (sans eux), et on garde si le score ne baisse pas.
    public int Improve(Func<bool> keepGoing, Random rng, int maxRemove)
    {
        int accepted = 0;
        while (keepGoing())
        {
            long before = _score;
            var keys = _assign.Keys.ToList();
            if (keys.Count == 0) break;
            int k = 1 + rng.Next(Math.Min(maxRemove, keys.Count));
            var removed = new List<(int id, Assign a)>();
            var tabu = new HashSet<int>();
            for (int i = 0; i < k; i++)
            {
                int id = keys[rng.Next(keys.Count)];
                if (!_assign.ContainsKey(id)) continue;
                removed.Add((id, RemoveTarget(id)));
                tabu.Add(id);
            }
            var inserted = InsertPass(tabu);
            if (_score >= before) { if (_score > before) accepted++; continue; }
            foreach (int id in inserted) RemoveTarget(id);
            foreach (var (id, a) in removed) Restore(id, a);
        }
        return accepted;
    }

    public Solution ToSolution() => new Solution
    {
        Score = _score, Label = _p.ToString() + (Environment.GetEnvironmentVariable("ISOGRAD_VERBOSE") == "1" ? $" order[{string.Join(",", _order.Take(5).Select(o => o.id))}] n={_order.Length}" : ""),
        DataMappings = _assign.SelectMany(kv => kv.Value.Data.Select(d => (d, kv.Key))
            .Concat(kv.Value.Sources.SelectMany(s => s.data.Select(d => (d, s.src))))).ToList(),
        ModelMappings = _assign.SelectMany(kv => kv.Value.Sources.Select(s => (s.src, kv.Key))).ToList()
    };

    double[]? _srcCost;   // mode lagrangien : coût d'utiliser chaque modèle comme source
    long[]? _reserved;    // dataset réservé (clé) pour chaque source choisie par le LP

    /// Appariement global datasets -> sources : chaque source retenue par le LP reçoit un seul dataset
    /// dont la taille tombe dans son intervalle. Sources triées par borne sup croissante et plus petit
    /// dataset suffisant (appariement intervalles/points), ce qui laisse les petits datasets aux petites sources.
    void ReserveSourceDatasets(LpGuide lp)
    {
        _reserved = new long[_inst.Models.Length];
        for (int t = 0; t < 4; t++)
        {
            var srcs = _sourceCandidates[t].Select(id => _inst.Models[id])
                .Where(m => lp.Xs[m.Id] >= _p.ReserveThreshold)
                .OrderBy(m => m.Ub[0]).ThenByDescending(m => m.Lb[0]);
            foreach (var m in srcs)
            {
                long best = 0; int bestC = -1;
                foreach (int c in CopyFirst)
                {
                    long key = _pools.MinInRange(t, c, m.Lb[0], m.Ub[0]);
                    if (key == 0) continue;
                    if (best == 0 || Pools.SizeOf(key) + (c == 0 ? _p.FreePenalty : 0) < Pools.SizeOf(best) + (bestC == 0 ? _p.FreePenalty : 0))
                    { best = key; bestC = c; }
                }
                if (best == 0) continue;
                _pools.Remove(t, bestC, best);
                _reserved[m.Id] = best;
            }
        }
    }
    Prices? _pr;          // prix utilisés (sous-gradient ou duaux du LP)

    /// Coût (dans les unités du mode courant) d'un besoin rempli directement avec des datasets.
    double ReqCost(int t, int lb) => _pr is { } pr
        ? pr.Total[t] * Math.Max(lb, 0) / Math.Max(1.0, _inst.Supply[t])
        : DataW(t, lb);

    public Solution Run()
    {
        var rng = new Random(_p.Seed);
        var pr = _pr = _p.Prices ?? _p.Lp?.Duals;
        var lp = _p.Lp;
        var prio = new Dictionary<int, (double score, bool direct)>();
        var monos = Enumerable.Range(0, 4).Select(t => !_p.Sources ? new List<Model>() :
            _inst.Models.Where(m => m.AllReqOk && m.IsMono && m.Types[0] == t).ToList()).ToArray();
        bool[] hasSource = monos.Select(l => l.Count > 0).ToArray();
        var feasible = _inst.Models.Where(m => m.Lb.Length > 0 &&
            Enumerable.Range(0, m.Lb.Length).All(k => m.ReqOk(k) || hasSource[m.Types[k]])).ToArray();

        if (pr == null)
        {
            for (int t = 0; t < 4; t++)
            {
                _sourceCandidates[t] = monos[t].OrderBy(SourceWeight).Select(m => m.Id).ToList();
                var l = _sourceCandidates[t];
                int qi = Math.Min(l.Count - 1, (int)(_p.SrcQuantile * l.Count));
                _srcEstimate[t] = l.Count == 0 ? double.PositiveInfinity : SourceWeight(_inst.Models[l[qi]]);
            }
            // Priorité = meilleure rentabilité entre "valeur pleine en direct" et "valeur/2 au moins cher".
            foreach (var m in feasible)
            {
                if (m.Value <= 0) continue;
                double noise = _p.Noise > 0 ? Math.Exp(_p.Noise * Gauss(rng)) : 1.0;
                double d = m.Value / DirectWeight(m);
                double h = m.Value / 2.0 / HalfWeight(m);
                prio[m.Id] = (Math.Max(d, h) * noise, d >= h);
            }
        }
        else
        {
            // Profit réduit (relaxation lagrangienne) : valeur moins le prix des ressources consommées.
            // Un besoin couvert par une source coûte le prix d'un "jeton" source du type.
            _srcCost = new double[_inst.Models.Length];
            var target = new double[_inst.Models.Length];
            foreach (var m in feasible)
            {
                double e = pr.Energy * EnergyW(m.Cost);
                double pf = m.AllReqOk && m.Value > 0 ? m.Value - e : double.NegativeInfinity;
                double ph = m.Value > 0 ? m.Value / 2.0 - e : double.NegativeInfinity;
                for (int k = 0; k < m.Types.Length; k++)
                {
                    int t = m.Types[k];
                    double tot = ReqCost(t, m.Lb[k]);
                    double free = _inst.FreeSupply[t] > 0 ? pr.Free[t] * Math.Max(m.Lb[k], 0) / _inst.FreeSupply[t] : double.PositiveInfinity;
                    pf -= tot + free;
                    double token = hasSource[t] ? pr.Token[t] : double.PositiveInfinity;
                    ph -= m.ReqOk(k) ? Math.Min(tot, token) : token;
                }
                double best = Math.Max(pf, ph);
                target[m.Id] = best;
                if (m.IsMono && m.AllReqOk && _p.Sources)
                {
                    double own = pr.Energy * EnergyW(m.Cost) + ReqCost(m.Types[0], m.Lb[0]);
                    _srcCost[m.Id] = own + Math.Max(0, best);
                    // Modèle plus utile comme source que comme cible : on ne l'entraîne pas pour lui-même.
                    if (lp != null ? lp.Xs[m.Id] >= 0.5 : pr.Token[m.Types[0]] - own > Math.Max(0, best)) continue;
                }
                if (m.Value <= 0 || double.IsNegativeInfinity(best)) continue;
                if (lp != null)
                {
                    // Guide LP : d'abord les modèles que la relaxation entraîne, dans le mode qu'elle a choisi.
                    double x = lp.Xf[m.Id] + lp.Xh[m.Id];
                    bool dir = x > 0.01 ? lp.Xf[m.Id] >= lp.Xh[m.Id] : pf >= ph;
                    prio[m.Id] = (x * _p.LpWeight * 1000 + best + _p.Noise * 100 * Gauss(rng), dir);
                }
                else prio[m.Id] = (best + _p.Noise * 100 * Gauss(rng), pf >= ph);
            }
            for (int t = 0; t < 4; t++)
                _sourceCandidates[t] = (lp != null
                    ? monos[t].OrderByDescending(m => Math.Round(lp.Xs[m.Id], 1)).ThenBy(m => _srcCost[m.Id])
                    : monos[t].OrderBy(m => _srcCost[m.Id])).Select(m => m.Id).ToList();
        }
        _order = prio.OrderByDescending(kv => kv.Value.score).Select(kv => (kv.Key, kv.Value.direct)).ToArray();
        if (lp != null && _p.ReserveSources) ReserveSourceDatasets(lp);
        InsertPass(null);
        if (_reserved != null)
        {
            // Réservations non utilisées : on rend les datasets et on refait une passe.
            for (int id = 0; id < _reserved.Length; id++)
                if (_reserved[id] != 0)
                {
                    long key = _reserved[id];
                    var d = _inst.Datasets[Pools.IdOf(key)];
                    _pools.Add(d.Type, d.Copy ? 1 : 0, key);
                    _reserved[id] = 0;
                }
            InsertPass(null);
        }
        return ToSolution();
    }

    static double Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

/// Recalcule le score exactement comme test_solution.py (et vérifie la validité).
public static class Checker
{
    public static (long score, string error) Evaluate(Instance inst, Solution sol)
    {
        var usedModels = new HashSet<int>();
        foreach (var (_, m) in sol.DataMappings) usedModels.Add(m);
        foreach (var (s, t) in sol.ModelMappings) { usedModels.Add(s); usedModels.Add(t); }
        var fills = usedModels.ToDictionary(i => i, _ => new long[4]);
        var defiled = usedModels.ToDictionary(i => i, _ => false);
        var dataSeen = new HashSet<int>();
        foreach (var (d, m) in sol.DataMappings)
        {
            if (!dataSeen.Add(d)) return (0, $"dataset {d} utilisé deux fois");
            var ds = inst.Datasets[d];
            if (!inst.Models[m].Types.Contains(ds.Type)) return (0, $"type invalide {d}->{m}");
            fills[m][ds.Type] += ds.Size;
            defiled[m] |= ds.Copy;
        }
        var sources = new HashSet<int>();
        long energy = 0, value = 0;
        foreach (var (s, t) in sol.ModelMappings)
        {
            if (!sources.Add(s)) return (0, $"source {s} utilisée deux fois");
            var sm = inst.Models[s];
            if (!sm.IsMono) return (0, $"source {s} non mono-type");
            long f = fills[s][sm.Types[0]];
            if (f < sm.Lb[0] || f > sm.Ub[0]) return (0, $"source {s} mal remplie");
            defiled[t] = true;
            fills[t][sm.Types[0]] = -1;
            energy += sm.Cost;
        }
        foreach (int id in usedModels)
        {
            if (sources.Contains(id)) continue;
            var m = inst.Models[id];
            for (int k = 0; k < m.Types.Length; k++)
            {
                long f = fills[id][m.Types[k]];
                if (f == -1) continue;
                if (f < m.Lb[k] || f > m.Ub[k]) return (0, $"modèle {id} mal rempli");
            }
            value += defiled[id] ? m.Value / 2 : m.Value;
            energy += m.Cost;
        }
        if (energy > inst.EnergyCap) return (0, "énergie dépassée");
        return (value, "");
    }
}

public static class Program
{
    // Variation multiplicative d'un paramètre, avec un plancher pour pouvoir quitter zéro.
    static double Perturb(double x, Random r) => (x + 0.01) * Math.Exp(0.4 * (r.NextDouble() * 2 - 1)) - 0.01 is var y && y > 0 ? y : 0;

    public static int Main(string[] args)
    {
        // Usage : IsogradIA <dossier datasets ou fichiers .json> [--out dossier] [--time secondes]
        string outDir = "solutions";
        double timeLimit = 30;
        bool useLp = true;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out") outDir = args[++i];
            else if (args[i] == "--no-lp") useLp = false;
            else if (args[i] == "--time") timeLimit = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            else if (Directory.Exists(args[i])) inputs.AddRange(Directory.GetFiles(args[i], "*.json").OrderBy(f => f));
            else inputs.Add(args[i]);
        }
        if (inputs.Count == 0) { Console.Error.WriteLine("Usage : IsogradIA <datasets...> [--out dossier]"); return 1; }
        Directory.CreateDirectory(outDir);

        foreach (var path in inputs)
        {
            var sw = Stopwatch.StartNew();
            var inst = Instance.Load(path);
            string name = Path.GetFileNameWithoutExtension(path);

            // Phase 1 : grille de paramètres.
            var configs = new List<Params>();
            var lambdas = new[] { 0.0, 0.1, 0.3, 1, 3, 10, 30 };
            foreach (double lf in lambdas)
                foreach (double lc in lambdas)
                    foreach (double q in new[] { 0.02, 0.15 })
                    {
                        configs.Add(new Params { LambdaFree = lf, LambdaCopy = lc, Sources = true, SrcQuantile = q });
                        if (q == 0.02) configs.Add(new Params { LambdaFree = lf, LambdaCopy = lc, Sources = false, SrcQuantile = q });
                    }
            foreach (bool src in new[] { true, false })
            {
                var prices = Prices.Compute(inst, src);
                foreach (double sc in new[] { 0.8, 0.9, 1.0, 1.1, 1.25 })
                    foreach (double tk in src ? new[] { 0.7, 1.0, 1.4 } : new[] { 1.0 })
                        configs.Add(new Params
                        {
                            Sources = src,
                            Prices = new Prices { Energy = prices.Energy * sc, Free = prices.Free, Total = prices.Total,
                                Token = prices.Token.Select(x => x * tk).ToArray() }
                        });
            }
            if (useLp)
            {
                var lsw = Stopwatch.StartNew();
                var lp = LpGuide.Solve(inst);
                Console.WriteLine($"{name,-12} borne LP = {lp.Bound:F0} ({lsw.Elapsed.TotalSeconds:F1}s)");
                if (Environment.GetEnvironmentVariable("ISOGRAD_VERBOSE") == "1")
                    Console.WriteLine($"   xf={lp.Xf.Sum():F0} xh={lp.Xh.Sum():F0} xs={lp.Xs.Sum():F0} prixE={lp.Duals.Energy:G4} total=[{string.Join(",", lp.Duals.Total.Select(x => x.ToString("G4")))}] libre=[{string.Join(",", lp.Duals.Free.Select(x => x.ToString("G4")))}] jeton=[{string.Join(",", lp.Duals.Token.Select(x => x.ToString("G4")))}]");
                foreach (bool reserve in new[] { false, true })
                    foreach (int look in new[] { 1, 10 })
                        foreach (int tol in new[] { -1, 100, 200, 400 })
                            foreach (int fp in reserve ? new[] { 0, 300, 3000 } : new[] { 300 })
                                configs.Add(new Params { Sources = true, Lp = lp, LpWeight = 1, SourceLookahead = look,
                                    WasteTolerance = Math.Max(tol, 0), MinWaste = tol >= 0, ReserveSources = reserve, FreePenalty = fp });
            }
            var results = new Solution[configs.Count];
            Parallel.For(0, configs.Count, i => results[i] = new Solver(inst, configs[i]).Run());
            if (Environment.GetEnvironmentVariable("ISOGRAD_VERBOSE") == "1")
                for (int i = 0; i < configs.Count; i++)
                    if (configs[i].Lp != null || configs[i].Prices != null) Console.WriteLine($"   {results[i].Score,10}  src={results[i].ModelMappings.Count} data={results[i].DataMappings.Count}  {results[i].Label}");
            int bi = Enumerable.Range(0, configs.Count).MaxBy(i => results[i].Score);
            var best = results[bi]; var bestP = configs[bi];
            long gridScore = best.Score;

            // Phase 2 : recherche aléatoire autour de la meilleure configuration, jusqu'à la limite de temps.
            var lockObj = new object();
            int seed = 1;
            Parallel.For(0, Environment.ProcessorCount, _ =>
            {
                while (sw.Elapsed.TotalSeconds < timeLimit * 0.4)
                {
                    Params p;
                    lock (lockObj)
                    {
                        var r = new Random(seed);
                        p = new Params
                        {
                            LambdaFree = Perturb(bestP.LambdaFree, r),
                            LambdaCopy = Perturb(bestP.LambdaCopy, r),
                            Sources = bestP.Sources,
                            SrcQuantile = Math.Clamp(bestP.SrcQuantile * Math.Exp(0.5 * (r.NextDouble() * 2 - 1)), 0.001, 0.9),
                            Noise = 0.1 * r.NextDouble() * r.NextDouble(),
                            Prices = bestP.Prices?.Scaled(r, 0.15),
                            Lp = bestP.Lp,
                            LpWeight = Math.Max(0.01, bestP.LpWeight * Math.Exp(0.5 * (r.NextDouble() * 2 - 1))),
                            SourceLookahead = bestP.SourceLookahead,
                            WasteTolerance = bestP.WasteTolerance,
                            MinWaste = bestP.MinWaste,
                            ReserveSources = bestP.ReserveSources,
                            FreePenalty = bestP.FreePenalty,
                            Seed = seed++
                        };
                    }
                    var sol = new Solver(inst, p).Run();
                    lock (lockObj)
                        if (sol.Score > best.Score) { best = sol; bestP = p; }
                }
            });

            // Phase 3 : recherche locale (détruire / reconstruire) depuis la meilleure configuration.
            long beforeLns = best.Score;
            int threads = Environment.ProcessorCount;
            var lns = new Solution[threads];
            Parallel.For(0, threads, i =>
            {
                var solver = new Solver(inst, bestP);
                solver.Run();
                solver.Improve(() => sw.Elapsed.TotalSeconds < timeLimit, new Random(1000 + i), 2 + 2 * i);
                lns[i] = solver.ToSolution();
            });
            foreach (var l in lns) if (l.Score > best.Score) best = l;
            Console.WriteLine($"{name,-12} recherche locale : {beforeLns} -> {best.Score}");

            var (check, err) = Checker.Evaluate(inst, best);
            string outPath = Path.Combine(outDir, name + "_submission.json");
            long previous = File.Exists(outPath) ? Checker.Evaluate(inst, Solution.FromJson(File.ReadAllText(outPath))).score : 0;
            string status = err != "" ? "ERREUR " + err : check > previous ? "écrit" : $"gardé l'ancien ({previous})";
            if (err == "" && check > previous) File.WriteAllText(outPath, best.ToJson());
            Console.WriteLine($"{name,-12} grille={gridScore,10} final={check,10} {status}  [{best.Label}]  {sw.Elapsed.TotalSeconds:F0}s ({seed - 1} essais aléatoires)");
        }
        return 0;
    }
}
