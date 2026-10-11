namespace IsogradIA;

/// Recherche locale sur le graphe datasets -> besoins -> modèles (idée de Stéphane) :
/// on part d'une solution, on modifie progressivement les liaisons (déplacer ou échanger un dataset,
/// remplacer un modèle entraîné par un autre) et on garde ce qui améliore (recuit simulé).
///
/// Une "unité" = un modèle cible et ses sources (appariement fixe). Ses "bacs" = les besoins de la cible
/// remplis par des datasets + le besoin unique de chaque source. Une unité active rapporte sa valeur
/// (pleine, ou /2 si source ou copyright) quand tous ses bacs sont dans leur intervalle.
public sealed class GraphSearch
{
    readonly Instance _inst;
    readonly Random _rng;
    // Unités
    readonly List<int> _uTarget = new();
    readonly List<int[]> _uSources = new();
    readonly List<int[]> _uBins = new();
    readonly List<long> _uCost = new();
    bool[] _active = Array.Empty<bool>();
    int[] _failing = Array.Empty<int>();     // bacs hors intervalle
    int[] _copyItems = Array.Empty<int>();   // datasets sous copyright dans l'unité
    // Bacs
    readonly List<int> _bUnit = new(), _bType = new(), _bLb = new(), _bUb = new(), _bModel = new();
    long[] _bSum = Array.Empty<long>();
    // Datasets
    int[] _iBin = Array.Empty<int>();
    readonly int[][] _itemsOfType = new int[4][];
    // Bacs actifs par type (liste + position pour retrait O(1))
    readonly List<int>[] _activeBins = { new(), new(), new(), new() };
    int[] _posInActive = Array.Empty<int>();
    // Réserve : datasets libres, triés par taille, par type et par classe (0 libre, 1 copyright)
    readonly SortedSet<long>[,] _pool = new SortedSet<long>[4, 2];
    int[] _inUse = Array.Empty<int>();       // unité qui utilise le modèle (-1 sinon)
    long _energyLeft;
    double _obj;
    long _value;
    readonly List<int> _activeUnits = new();
    int[] _posInUnits = Array.Empty<int>();

    public double Lambda = 0.005, Wdef = 0.03;

    const long K = 1_000_000;
    long Key(int i) => (long)_inst.Datasets[i].Size * K + i;

    public GraphSearch(Instance inst, Solution start, int seed)
    {
        _inst = inst; _rng = new Random(seed);
        int nm = inst.Models.Length;
        var srcOf = start.ModelMappings.GroupBy(x => x.target).ToDictionary(g => g.Key, g => g.Select(x => x.source).ToArray());
        var isSrc = start.ModelMappings.Select(x => x.source).ToHashSet();
        var trained = start.DataMappings.Select(x => x.model).Concat(start.ModelMappings.Select(x => x.target))
            .Where(m => !isSrc.Contains(m)).Distinct().ToList();
        var startUnits = new List<int>();
        foreach (int t in trained) startUnits.Add(AddUnit(t, srcOf.TryGetValue(t, out var s) ? s : Array.Empty<int>()));
        var usedModels = trained.Concat(isSrc).ToHashSet();
        // Candidats : unités "données seulement" pour tous les autres modèles faisables.
        foreach (var m in inst.Models)
            if (!usedModels.Contains(m.Id) && m.Value > 0 && m.AllReqOk && !m.SourceOnly) AddUnit(m.Id, Array.Empty<int>());

        int nu = _uTarget.Count, nb = _bUnit.Count;
        _active = new bool[nu]; _failing = new int[nu]; _copyItems = new int[nu]; _posInUnits = new int[nu];
        _bSum = new long[nb]; _posInActive = Enumerable.Repeat(-1, nb).ToArray();
        _inUse = Enumerable.Repeat(-1, nm).ToArray();
        _iBin = Enumerable.Repeat(-1, inst.Datasets.Length).ToArray();
        for (int t = 0; t < 4; t++)
        {
            _itemsOfType[t] = inst.Datasets.Where(d => d.Type == t).Select(d => d.Id).ToArray();
            _pool[t, 0] = new SortedSet<long>(); _pool[t, 1] = new SortedSet<long>();
        }
        foreach (var d in inst.Datasets) _pool[d.Type, d.Copy ? 1 : 0].Add(Key(d.Id));
        _energyLeft = inst.EnergyCap;

        // Activation des unités de départ avec leurs datasets exacts.
        var binOf = new Dictionary<(int model, int type), int>();
        for (int b = 0; b < nb; b++) binOf[(_bModel[b], _bType[b])] = b;
        foreach (int u in startUnits) Activate(u);
        foreach (var (d, m) in start.DataMappings)
            if (binOf.TryGetValue((m, inst.Datasets[d].Type), out int b) && _active[_bUnit[b]]) PlaceFromPool(d, b);
        _obj = ComputeObjective();
    }

    int AddUnit(int target, int[] sources)
    {
        int u = _uTarget.Count;
        var m = _inst.Models[target];
        var srcTypes = sources.Select(s => _inst.Models[s].Types[0]).ToHashSet();
        var bins = new List<int>();
        for (int k = 0; k < m.Types.Length; k++)
            if (!srcTypes.Contains(m.Types[k])) bins.Add(NewBin(u, target, k));
        foreach (int s in sources) bins.Add(NewBin(u, s, 0));
        _uTarget.Add(target); _uSources.Add(sources); _uBins.Add(bins.ToArray());
        _uCost.Add(m.Cost + sources.Sum(s => _inst.Models[s].Cost));
        return u;
    }

    int NewBin(int u, int model, int k)
    {
        var m = _inst.Models[model];
        _bUnit.Add(u); _bModel.Add(model); _bType.Add(m.Types[k]); _bLb.Add(m.Lb[k]); _bUb.Add(m.Ub[k]);
        return _bUnit.Count - 1;
    }

    bool BinOk(int b) => _bSum[b] >= _bLb[b] && _bSum[b] <= _bUb[b];
    long UnitValue(int u)
    {
        if (!_active[u] || _failing[u] > 0) return 0;
        long v = _inst.Models[_uTarget[u]].Value;
        return _uSources[u].Length > 0 || _copyItems[u] > 0 ? v / 2 : v;
    }
    double BinPen(int b)
    {
        long s = _bSum[b];
        return s < _bLb[b] ? Wdef * (_bLb[b] - s) : s > _bUb[b] ? Wdef * (s - _bUb[b]) + Lambda * (_bUb[b] - _bLb[b]) : Lambda * (s - _bLb[b]);
    }

    double ComputeObjective()
    {
        double o = 0; _value = 0;
        foreach (int u in _activeUnits)
        {
            long v = UnitValue(u); _value += v; o += v;
            foreach (int b in _uBins[u]) o -= BinPen(b);
        }
        return o;
    }

    void Activate(int u)
    {
        _active[u] = true;
        _energyLeft -= _uCost[u];
        _inUse[_uTarget[u]] = u; foreach (int s in _uSources[u]) _inUse[s] = u;
        foreach (int b in _uBins[u]) { int t = _bType[b]; _posInActive[b] = _activeBins[t].Count; _activeBins[t].Add(b); }
        _failing[u] = _uBins[u].Count(b => !BinOk(b));
        _posInUnits[u] = _activeUnits.Count; _activeUnits.Add(u);
    }

    void Deactivate(int u)
    {
        foreach (int b in _uBins[u])
        {
            foreach (int i in ItemsIn(b)) ToPool(i);
            int t = _bType[b], p = _posInActive[b], last = _activeBins[t][^1];
            _activeBins[t][p] = last; _posInActive[last] = p; _activeBins[t].RemoveAt(_activeBins[t].Count - 1); _posInActive[b] = -1;
        }
        _active[u] = false;
        _energyLeft += _uCost[u];
        _inUse[_uTarget[u]] = -1; foreach (int s in _uSources[u]) _inUse[s] = -1;
        int pu = _posInUnits[u], lu = _activeUnits[^1];
        _activeUnits[pu] = lu; _posInUnits[lu] = pu; _activeUnits.RemoveAt(_activeUnits.Count - 1);
    }

    // Contenu des bacs : liste par bac, maintenue à jour.
    readonly Dictionary<int, List<int>> _content = new();
    List<int> ItemsIn(int b) => _content.TryGetValue(b, out var l) ? l.ToList() : new List<int>();

    /// Déplace un dataset (de son bac ou de la réserve) vers un bac (ou la réserve si b = -1),
    /// en tenant à jour sommes, compteurs d'échecs et de copyright.
    void Move(int i, int b)
    {
        int from = _iBin[i];
        var d = _inst.Datasets[i];
        if (from == b) return;
        if (from >= 0)
        {
            int u = _bUnit[from]; bool ok0 = BinOk(from);
            _bSum[from] -= d.Size; _content[from].Remove(i);
            if (d.Copy) _copyItems[u]--;
            bool ok1 = BinOk(from); if (ok0 != ok1) _failing[u] += ok1 ? -1 : 1;
        }
        else _pool[d.Type, d.Copy ? 1 : 0].Remove(Key(i));
        if (b >= 0)
        {
            int u = _bUnit[b]; bool ok0 = BinOk(b);
            _bSum[b] += d.Size;
            if (!_content.TryGetValue(b, out var l)) _content[b] = l = new List<int>();
            l.Add(i);
            if (d.Copy) _copyItems[u]++;
            bool ok1 = BinOk(b); if (ok0 != ok1) _failing[u] += ok1 ? -1 : 1;
        }
        else _pool[d.Type, d.Copy ? 1 : 0].Add(Key(i));
        _iBin[i] = b;
    }
    void ToPool(int i) => Move(i, -1);
    void PlaceFromPool(int i, int b) => Move(i, b);

    /// Variation de l'objectif liée aux unités touchées (calcul avant/après).
    double LocalObj(IEnumerable<int> units)
    {
        double o = 0;
        foreach (int u in units.Distinct())
        {
            if (!_active[u]) continue;
            o += UnitValue(u);
            foreach (int b in _uBins[u]) o -= BinPen(b);
        }
        return o;
    }

    /// Remplit un bac depuis la réserve (gros d'abord puis complément), libres d'abord si l'unité vise la valeur pleine.
    void GreedyFill(int b, bool preferFree)
    {
        int t = _bType[b];
        var classes = preferFree ? new[] { 0, 1 } : new[] { 1, 0 };
        foreach (int c in classes)
        {
            while (_bSum[b] < _bLb[b])
            {
                long room = _bUb[b] - _bSum[b];
                if (room <= 0) break;
                var view = _pool[t, c].GetViewBetween(0, (room + 1) * K - 1);
                if (view.Count == 0) break;
                long need = _bLb[b] - _bSum[b];
                // Plus petit dataset qui suffit, sinon le plus gros qui rentre.
                var fit = _pool[t, c].GetViewBetween(need * K, (room + 1) * K - 1);
                long key = fit.Count > 0 ? fit.Min : view.Max;
                Move((int)(key % K), b);
            }
            if (_bSum[b] >= _bLb[b]) break;
        }
    }

    public long Value => _value;

    int[][]? _monos;

    void SetSource(int u, int idx, int b, int s)
    {
        int old = _uSources[u][idx];
        long dc = _inst.Models[s].Cost - _inst.Models[old].Cost;
        _energyLeft -= dc; _uCost[u] += dc;
        _inUse[old] = -1; _inUse[s] = u;
        _uSources[u][idx] = s;
        var m = _inst.Models[s];
        _bModel[b] = s; _bLb[b] = m.Lb[0]; _bUb[b] = m.Ub[0];
        _failing[u] = _uBins[u].Count(x => !BinOk(x));
    }

    int PickMono(int t)
    {
        _monos ??= Enumerable.Range(0, 4).Select(tt => _inst.Models.Where(m => m.IsMono && m.AllReqOk && !m.SourceOnly && m.Types[0] == tt).Select(m => m.Id).ToArray()).ToArray();
        var monos = _monos[t];
        if (monos.Length == 0) return -1;
        int best = -1;
        for (int tries = 0; tries < 8; tries++)
        {
            int c = monos[_rng.Next(monos.Length)];
            if (_inUse[c] >= 0) continue;
            if (best < 0 || _inst.Models[c].Lb[0] < _inst.Models[best].Lb[0]) best = c;
        }
        return best;
    }

    void SetBin(int u, int b, int model, int k)
    {
        var m = _inst.Models[model];
        _bModel[b] = model; _bLb[b] = m.Lb[k]; _bUb[b] = m.Ub[k];
        _failing[u] = _uBins[u].Count(x => !BinOk(x));
    }

    void AddSource(int u, int s) { _uSources[u] = _uSources[u].Append(s).ToArray(); _inUse[s] = u; long c = _inst.Models[s].Cost; _uCost[u] += c; _energyLeft -= c; }
    void RemoveSource(int u, int s) { _uSources[u] = _uSources[u].Where(x => x != s).ToArray(); _inUse[s] = -1; long c = _inst.Models[s].Cost; _uCost[u] -= c; _energyLeft += c; }

    /// Un besoin de la cible, rempli par des datasets, passe à une source (libère des données, coûte de l'énergie).
    void Sourcify(double temp)
    {
        if (_activeUnits.Count == 0) return;
        int u = _activeUnits[_rng.Next(_activeUnits.Count)];
        int tg = _uTarget[u];
        var own = _uBins[u].Where(x => _bModel[x] == tg).ToArray();
        if (own.Length == 0) return;
        int b = own[_rng.Next(own.Length)], t = _bType[b];
        int s = PickMono(t);
        if (s < 0 || _inst.Models[s].Cost > _energyLeft) return;
        int k = Array.IndexOf(_inst.Models[tg].Types, t);
        double before = LocalObj(new[] { u });
        var saved = ItemsIn(b);
        foreach (int i in saved) Move(i, -1);
        AddSource(u, s); SetBin(u, b, s, 0);
        GreedyFill(b, false);
        double d = LocalObj(new[] { u }) - before;
        if (_failing[u] == 0 && (d >= 0 || _rng.NextDouble() < Math.Exp(d / temp))) { _obj += d; return; }
        foreach (int i in ItemsIn(b)) Move(i, -1);
        RemoveSource(u, s); SetBin(u, b, tg, k);
        foreach (int i in saved) Move(i, b);
    }

    /// Une source est retirée : le besoin correspondant de la cible est rempli directement par des datasets.
    void Desourcify(double temp)
    {
        if (_activeUnits.Count == 0) return;
        int u = _activeUnits[_rng.Next(_activeUnits.Count)];
        if (_uSources[u].Length == 0) return;
        int s = _uSources[u][_rng.Next(_uSources[u].Length)];
        int tg = _uTarget[u], t = _inst.Models[s].Types[0];
        int k = Array.IndexOf(_inst.Models[tg].Types, t);
        if (k < 0 || !_inst.Models[tg].ReqOk(k)) return;
        int b = _uBins[u].First(x => _bModel[x] == s);
        double before = LocalObj(new[] { u });
        var saved = ItemsIn(b);
        foreach (int i in saved) Move(i, -1);
        RemoveSource(u, s); SetBin(u, b, tg, k);
        GreedyFill(b, _uSources[u].Length == 0);
        double d = LocalObj(new[] { u }) - before;
        if (_failing[u] == 0 && (d >= 0 || _rng.NextDouble() < Math.Exp(d / temp))) { _obj += d; return; }
        foreach (int i in ItemsIn(b)) Move(i, -1);
        if (_inUse[s] >= 0) { foreach (int i in saved) Move(i, -1); }
        AddSource(u, s); SetBin(u, b, s, 0);
        foreach (int i in saved) Move(i, b);
    }

    /// Remplace une source d'une unité par un autre modèle mono-type libre du même type (souvent plus petit).
    void ReSource(double temp)
    {
        _monos ??= Enumerable.Range(0, 4).Select(t => _inst.Models.Where(m => m.IsMono && m.AllReqOk && !m.SourceOnly && m.Types[0] == t).Select(m => m.Id).ToArray()).ToArray();
        if (_activeUnits.Count == 0) return;
        int u = _activeUnits[_rng.Next(_activeUnits.Count)];
        var srcs = _uSources[u];
        if (srcs.Length == 0) return;
        int idx = _rng.Next(srcs.Length), sOld = srcs[idx];
        int t = _inst.Models[sOld].Types[0];
        var monos = _monos[t];
        if (monos.Length == 0) return;
        int sNew = -1;
        for (int tries = 0; tries < 8; tries++)
        {
            int c = monos[_rng.Next(monos.Length)];
            if (_inUse[c] >= 0) continue;
            if (sNew < 0 || _inst.Models[c].Lb[0] < _inst.Models[sNew].Lb[0]) sNew = c;
        }
        if (sNew < 0 || _inst.Models[sNew].Cost - _inst.Models[sOld].Cost > _energyLeft) return;
        int b = _uBins[u].First(x => _bModel[x] == sOld && _bUnit[x] == u && _bType[x] == t);
        double before = LocalObj(new[] { u });
        var saved = ItemsIn(b);
        foreach (int i in saved) Move(i, -1);
        SetSource(u, idx, b, sNew);
        GreedyFill(b, false);
        double d = LocalObj(new[] { u }) - before;
        if (_failing[u] == 0 && (d >= 0 || _rng.NextDouble() < Math.Exp(d / temp))) { _obj += d; return; }
        foreach (int i in ItemsIn(b)) Move(i, -1);
        SetSource(u, idx, b, sOld);
        foreach (int i in saved) Move(i, b);
    }

    /// Recuit : renvoie la meilleure solution réalisable trouvée (unités satisfaites seulement).
    public Solution Run(long iterations, double t0, double t1, Func<bool> keepGoing, Action<string>? log = null)
    {
        long bestValue = _value; Solution best = ToSolution();
        var failingBins = new List<int>();
        int nu = _uTarget.Count;
        for (long it = 0; it < iterations && keepGoing(); it++)
        {
            double temp = t0 * Math.Pow(t1 / t0, (double)it / iterations);
            int r = _rng.Next(100);
            if ((it & 4095) == 0)
            {
                failingBins.Clear();
                foreach (int u in _activeUnits) foreach (int b in _uBins[u]) if (!BinOk(b)) failingBins.Add(b);
            }
            if (r < 80)
            {
                // Déplacement / échange d'un dataset.
                int t = _rng.Next(4);
                var items = _itemsOfType[t];
                if (items.Length == 0 || _activeBins[t].Count == 0) continue;
                int i = items[_rng.Next(items.Length)];
                int from = _iBin[i];
                if (r < 40)
                {
                    int to = failingBins.Count > 0 && _rng.Next(2) == 0 ? failingBins[_rng.Next(failingBins.Count)] : _activeBins[t][_rng.Next(_activeBins[t].Count)];
                    if (_bType[to] != t || to == from || _posInActive[to] < 0) continue;
                    if (_rng.Next(5) == 0) to = -1;
                    if (to == from) continue;
                    var us = new List<int>(2); if (from >= 0) us.Add(_bUnit[from]); if (to >= 0) us.Add(_bUnit[to]);
                    double before = LocalObj(us);
                    Move(i, to);
                    double d = LocalObj(us) - before;
                    if (d >= 0 || _rng.NextDouble() < Math.Exp(d / temp)) { _obj += d; }
                    else Move(i, from);
                }
                else
                {
                    int j = items[_rng.Next(items.Length)];
                    int bj = _iBin[j];
                    if (bj == from) continue;
                    var us = new List<int>(2); if (from >= 0) us.Add(_bUnit[from]); if (bj >= 0) us.Add(_bUnit[bj]);
                    double before = LocalObj(us);
                    Move(i, -1); Move(j, from); Move(i, bj);
                    double d = LocalObj(us) - before;
                    if (d >= 0 || _rng.NextDouble() < Math.Exp(d / temp)) { _obj += d; }
                    else { Move(j, -1); Move(i, from); Move(j, bj); }
                }
            }
            else if (r < 88) ReSource(temp);
            else if (r < 91) Sourcify(temp);
            else if (r < 94) Desourcify(temp);
            else
            {
                // Remplacer une unité active par une unité candidate (ou seulement en ajouter / retirer une).
                int uOut = r < 98 && _activeUnits.Count > 0 ? _activeUnits[_rng.Next(_activeUnits.Count)] : -1;
                int uIn = -1;
                for (int tries = 0; tries < 8; tries++)
                {
                    int c = _rng.Next(nu);
                    if (_active[c] || c == uOut) continue;
                    bool free = _inUse[_uTarget[c]] < 0 || _inUse[_uTarget[c]] == uOut;
                    foreach (int s in _uSources[c]) free &= _inUse[s] < 0 || _inUse[s] == uOut;
                    if (!free) continue;
                    if (uIn < 0 || _inst.Models[_uTarget[c]].Value * (double)_uCost[uIn] > _inst.Models[_uTarget[uIn]].Value * (double)_uCost[c]) uIn = c;
                }
                if (uIn < 0 && uOut < 0) continue;
                if (uIn >= 0 && _uCost[uIn] > _energyLeft + (uOut >= 0 ? _uCost[uOut] : 0)) continue;
                double before = 0; if (uOut >= 0) before = LocalObj(new[] { uOut });
                var saved = uOut >= 0 ? _uBins[uOut].Select(b => (b, ItemsIn(b))).ToArray() : Array.Empty<(int, List<int>)>();
                if (uOut >= 0) Deactivate(uOut);
                if (uIn >= 0)
                {
                    Activate(uIn);
                    bool pf = _uSources[uIn].Length == 0;
                    foreach (int b in _uBins[uIn].OrderByDescending(b => _bLb[b])) GreedyFill(b, pf);
                }
                double after = uIn >= 0 ? LocalObj(new[] { uIn }) : 0;
                double d = after - before;
                bool filled = uIn < 0 || _failing[uIn] == 0;
                if (filled && (d >= 0 || _rng.NextDouble() < Math.Exp(d / temp))) { _obj += d; }
                else
                {
                    if (uIn >= 0) Deactivate(uIn);
                    if (uOut >= 0)
                    {
                        Activate(uOut);
                        foreach (var (b, l) in saved) foreach (int i in l) Move(i, b);
                    }
                }
            }
            if ((it & 1023) == 0)
            {
                long v = 0; foreach (int u in _activeUnits) v += UnitValue(u);
                _value = v;
                if (v > bestValue) { bestValue = v; best = ToSolution(); }
            }
            if (log != null && it % 20_000_000 == 0) log($"   graphe it={it / 1_000_000}M valeur={_value} meilleure={bestValue} actives={_activeUnits.Count} T={temp:G3}");
        }
        long fv = 0; foreach (int u in _activeUnits) fv += UnitValue(u);
        if (fv > bestValue) best = ToSolution();
        return best;
    }

    /// Solution des unités actives et satisfaites (les autres sont simplement laissées de côté).
    public Solution ToSolution()
    {
        var sol = new Solution();
        foreach (int u in _activeUnits)
        {
            if (_failing[u] > 0) continue;
            foreach (int b in _uBins[u]) foreach (int i in ItemsIn(b)) sol.DataMappings.Add((i, _bModel[b]));
            foreach (int s in _uSources[u]) sol.ModelMappings.Add((s, _uTarget[u]));
        }
        sol.Score = _activeUnits.Sum(UnitValue);
        return sol;
    }
}
