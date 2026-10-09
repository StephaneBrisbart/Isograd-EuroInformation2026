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
    public bool Feasible => Lb.Length > 0 && Lb.Zip(Ub).All(p => p.First <= p.Second && p.Second > 0);
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
        foreach (var d in inst.Datasets) inst.Supply[d.Type] += d.Size;
        return inst;
    }
}

public sealed class Solution
{
    public long Score;
    public string Label = "";
    public List<(int data, int model)> DataMappings = new();
    public List<(int source, int target)> ModelMappings = new();

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

public enum SourceMode { None, Fallback, Prefer }

public sealed class Params
{
    public double Lambda;           // poids des données dans le coût d'un modèle
    public SourceMode Sources;
    public bool FreeFirst = true;   // tenter d'abord un remplissage 100% libre de droits
    public override string ToString() => $"lambda={Lambda} sources={Sources} freeFirst={FreeFirst}";
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
    readonly List<(int data, int model)> _dataMap = new();
    readonly List<(int source, int target)> _modelMap = new();
    readonly List<int>[] _sourceCandidates = new List<int>[4];
    readonly int[] _sourcePtr = new int[4];

    public Solver(Instance inst, Params p)
    {
        _inst = inst; _p = p;
        _pools = new Pools(inst);
        _energyLeft = inst.EnergyCap;
        _used = new bool[inst.Models.Length];
    }

    double Weight(Model m)
    {
        double w = (double)m.Cost / _inst.EnergyCap;
        for (int k = 0; k < m.Types.Length; k++)
            w += _p.Lambda * Math.Max(m.Lb[k], 0) / Math.Max(1.0, _inst.Supply[m.Types[k]]);
        return w + 1e-15;
    }

    /// Remplit un besoin [lb, ub] du type t avec les pools indiqués (dans l'ordre de préférence).
    /// En cas d'échec, l'état est laissé tel quel : l'appelant fait le rollback via le journal.
    bool FillRequirement(int t, int lb, int ub, int[] classes, List<(int data, long key, int c)> taken)
    {
        if (ub < lb || ub <= 0) return false;
        int sum = 0;
        for (int iter = 0; iter < 1000; iter++)
        {
            int lo = lb - sum, hi = ub - sum;
            if (lo <= 0) return true;

            // Un seul dataset suffit-il pour finir ? On prend le plus petit qui convient.
            foreach (int c in classes)
            {
                long key = _pools.MinInRange(t, c, lo, hi);
                if (key != 0) { Take(t, c, key, taken); return true; }
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

    /// Essaie d'entraîner un modèle mono-type comme source de données. Renvoie l'id ou -1.
    int TakeSource(int t, long energyBudget, List<(int data, int model)> dataOut)
    {
        var list = _sourceCandidates[t];
        for (int i = _sourcePtr[t]; i < list.Count; i++)
        {
            var s = _inst.Models[list[i]];
            if (_used[s.Id]) { if (i == _sourcePtr[t]) _sourcePtr[t]++; continue; }
            if (s.Cost > energyBudget) continue;
            var taken = new List<(int, long, int)>();
            int mark = _journal.Count;
            // La valeur d'une source n'est pas comptée : le copyright n'a pas d'importance.
            if (FillRequirement(t, s.Lb[0], s.Ub[0], CopyFirst, taken))
            {
                _used[s.Id] = true;
                foreach (var (d, _, _) in taken) dataOut.Add((d, s.Id));
                return s.Id;
            }
            Rollback(mark);
            if (i >= _sourcePtr[t] + 50) break; // on ne cherche pas indéfiniment
        }
        return -1;
    }

    bool TryTrain(Model m, out long gained)
    {
        gained = 0;
        if (m.Cost > _energyLeft) return false;

        // 1) Tout en libre de droits, sans source : valeur pleine.
        if (_p.FreeFirst && _p.Sources != SourceMode.Prefer)
        {
            int mark = _journal.Count;
            var taken = new List<(int data, long key, int c)>();
            bool ok = true;
            for (int k = 0; k < m.Types.Length && ok; k++)
                ok = FillRequirement(m.Types[k], m.Lb[k], m.Ub[k], FreeOnly, taken);
            if (ok)
            {
                Commit(m, taken, new List<(int, int)>(), new List<(int, int)>());
                gained = m.Value;
                return true;
            }
            Rollback(mark);
        }

        // 2) Données mixtes et/ou sources : valeur divisée par deux (sauf si tout est libre).
        {
            int mark = _journal.Count;
            var markUsed = new List<int>();
            var taken = new List<(int data, long key, int c)>();
            var srcData = new List<(int, int)>();
            var srcMap = new List<(int, int)>();
            long budget = _energyLeft - m.Cost;
            bool ok = true;
            _used[m.Id] = true; // un modèle ne peut pas être sa propre source
            int[] classes = _p.FreeFirst ? CopyFirst : new[] { 0, 1 };
            for (int k = 0; k < m.Types.Length && ok; k++)
            {
                int t = m.Types[k];
                bool done = false;
                if (_p.Sources == SourceMode.Prefer)
                    done = TrySource(t, m, ref budget, srcData, srcMap, markUsed);
                if (!done)
                {
                    int jm = _journal.Count; int tm = taken.Count;
                    done = FillRequirement(t, m.Lb[k], m.Ub[k], classes, taken);
                    if (!done) { Rollback(jm); taken.RemoveRange(tm, taken.Count - tm); }
                }
                if (!done && _p.Sources == SourceMode.Fallback)
                    done = TrySource(t, m, ref budget, srcData, srcMap, markUsed);
                ok = done;
            }
            if (ok)
            {
                bool halved = srcMap.Count > 0 || taken.Any(x => x.c == 1);
                foreach (var (_, model) in srcMap) _energyLeft -= _inst.Models[model].Cost;
                Commit(m, taken, srcData, srcMap);
                gained = halved ? m.Value / 2 : m.Value;
                return true;
            }
            Rollback(mark);
            foreach (int id in markUsed) _used[id] = false;
            _used[m.Id] = false;
        }
        return false;
    }

    bool TrySource(int t, Model target, ref long budget, List<(int, int)> srcData, List<(int, int)> srcMap, List<int> markUsed)
    {
        int s = TakeSource(t, budget, srcData);
        if (s < 0) return false;
        budget -= _inst.Models[s].Cost;
        srcMap.Add((s, target.Id));
        markUsed.Add(s);
        return true;
    }

    void Commit(Model m, List<(int data, long key, int c)> taken, List<(int, int)> srcData, List<(int, int)> srcMap)
    {
        _used[m.Id] = true;
        _energyLeft -= m.Cost;
        foreach (var (d, _, _) in taken) _dataMap.Add((d, m.Id));
        _dataMap.AddRange(srcData);
        _modelMap.AddRange(srcMap);
        _journal.Clear();
    }

    public Solution Run()
    {
        var feasible = _inst.Models.Where(m => m.Feasible).ToArray();

        if (_p.Sources != SourceMode.None)
        {
            for (int t = 0; t < 4; t++)
                _sourceCandidates[t] = feasible.Where(m => m.IsMono && m.Types[0] == t)
                    .OrderBy(m => Weight(m) + 0.5 * m.Value / 1000.0 * 1e-4)
                    .Select(m => m.Id).ToList();
        }
        else for (int t = 0; t < 4; t++) _sourceCandidates[t] = new List<int>();

        var order = feasible.Where(m => m.Value > 0)
            .OrderByDescending(m => m.Value / Weight(m)).ToArray();

        long score = 0;
        foreach (var m in order)
        {
            if (_used[m.Id]) continue;
            if (TryTrain(m, out long g)) score += g;
        }
        return new Solution
        {
            Score = score, Label = _p.ToString(),
            DataMappings = new List<(int, int)>(_dataMap),
            ModelMappings = new List<(int, int)>(_modelMap)
        };
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
    public static int Main(string[] args)
    {
        // Usage : IsogradIA <dossier datasets ou fichiers .json> [--out dossier]
        string outDir = "solutions";
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out") outDir = args[++i];
            else if (Directory.Exists(args[i])) inputs.AddRange(Directory.GetFiles(args[i], "*.json").OrderBy(f => f));
            else inputs.Add(args[i]);
        }
        if (inputs.Count == 0) { Console.Error.WriteLine("Usage : IsogradIA <datasets...> [--out dossier]"); return 1; }
        Directory.CreateDirectory(outDir);

        var configs = new List<Params>();
        foreach (double lambda in new[] { 0.0, 0.1, 0.25, 0.5, 1, 2, 4, 8 })
            foreach (var src in new[] { SourceMode.None, SourceMode.Fallback, SourceMode.Prefer })
                configs.Add(new Params { Lambda = lambda, Sources = src });

        foreach (var path in inputs)
        {
            var sw = Stopwatch.StartNew();
            var inst = Instance.Load(path);
            var results = new Solution[configs.Count];
            Parallel.For(0, configs.Count, i => results[i] = new Solver(inst, configs[i]).Run());
            var best = results.OrderByDescending(r => r.Score).First();
            var (check, err) = Checker.Evaluate(inst, best);
            string name = Path.GetFileNameWithoutExtension(path);
            File.WriteAllText(Path.Combine(outDir, name + "_submission.json"), best.ToJson());
            Console.WriteLine($"{name,-12} score={best.Score,10} vérifié={check,10} {(err == "" ? "OK" : "ERREUR " + err)}  [{best.Label}]  {sw.Elapsed.TotalSeconds:F1}s");
        }
        return 0;
    }
}
