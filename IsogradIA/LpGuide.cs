using Google.OrTools.LinearSolver;

namespace IsogradIA;

/// Relaxation linéaire du problème (résolue avec GLOP / OR-Tools).
/// Pour chaque modèle : xf = valeur pleine, xh = valeur/2, xs = source ; y = besoin couvert par un jeton source.
/// Contraintes (normalisées) : énergie, volume libre par type, volume total par type, jetons par type.
/// La solution sert de guide à la construction gloutonne, et ses duaux donnent les prix des ressources.
public sealed class LpGuide
{
    public double[] Xf = Array.Empty<double>();
    public double[] Xh = Array.Empty<double>();
    public double[] Xs = Array.Empty<double>();
    public bool[][] TokenReq = Array.Empty<bool[]>();
    public Prices Duals = new();
    public double Bound;

    /// integer : résolution exacte en nombres entiers (SCIP) avec limite de temps ; les duaux viennent alors
    /// de la relaxation. volFactor / freeFactor : marge sur les volumes pour absorber le surplus réel.
    public static LpGuide Solve(Instance inst, bool integer = false, double timeLimitSec = 60, double volFactor = 1.0, double freeFactor = 1.0)
    {
        Prices? lpDuals = integer ? Solve(inst).Duals : null;
        var solver = Google.OrTools.LinearSolver.Solver.CreateSolver(integer ? "SCIP" : "GLOP");
        if (integer) solver.SetTimeLimit((long)(timeLimitSec * 1000));
        Variable NewVar() => integer ? solver.MakeIntVar(0, 1, "") : solver.MakeNumVar(0, 1, "");
        int n = inst.Models.Length;
        var g = new LpGuide
        {
            Xf = new double[n], Xh = new double[n], Xs = new double[n],
            TokenReq = inst.Models.Select(m => new bool[m.Lb.Length]).ToArray()
        };
        var hasSrc = new bool[4];
        foreach (var m in inst.Models) if (m.IsMono && m.AllReqOk) hasSrc[m.Types[0]] = true;

        var energy = solver.MakeConstraint(double.NegativeInfinity, integer ? 1.0 - 1e-12 : 1.0);
        var free = Enumerable.Range(0, 4).Select(_ => solver.MakeConstraint(double.NegativeInfinity, freeFactor)).ToArray();
        var total = Enumerable.Range(0, 4).Select(_ => solver.MakeConstraint(double.NegativeInfinity, volFactor)).ToArray();
        var token = Enumerable.Range(0, 4).Select(_ => solver.MakeConstraint(double.NegativeInfinity, 0.0)).ToArray();
        // Contraintes de Hall par seuil de taille : les besoins de borne sup <= x ne peuvent être remplis
        // qu'avec des datasets de taille <= x (granularité ignorée par les contraintes de volume).
        bool hall = Environment.GetEnvironmentVariable("ISOGRAD_HALL") == "1";
        var hx = new int[4][]; var hTot = new Constraint[4][]; var hFree = new Constraint[4][];
        var hSupT = new double[4][]; var hSupF = new double[4][];
        if (hall)
            for (int t = 0; t < 4; t++)
            {
                var sz = inst.Datasets.Where(d => d.Type == t).Select(d => d.Size).OrderBy(x => x).ToArray();
                if (sz.Length == 0) { hx[t] = Array.Empty<int>(); hTot[t] = hFree[t] = Array.Empty<Constraint>(); hSupT[t] = hSupF[t] = Array.Empty<double>(); continue; }
                hx[t] = Enumerable.Range(1, 40).Select(q => sz[Math.Min(sz.Length - 1, (int)(sz.Length * Math.Pow(q / 40.0, 2)))]).Distinct().ToArray();
                hSupT[t] = hx[t].Select(x => (double)inst.Datasets.Where(d => d.Type == t && d.Size <= x).Sum(d => (long)d.Size)).ToArray();
                hSupF[t] = hx[t].Select(x => (double)inst.Datasets.Where(d => d.Type == t && !d.Copy && d.Size <= x).Sum(d => (long)d.Size)).ToArray();
                hTot[t] = hSupT[t].Select(_ => solver.MakeConstraint(double.NegativeInfinity, volFactor)).ToArray();
                hFree[t] = hSupF[t].Select(_ => solver.MakeConstraint(double.NegativeInfinity, freeFactor)).ToArray();
            }
        void Hall(Variable v, int t, int lb, int ub, double sign, bool freeToo)
        {
            if (!hall) return;
            for (int q = 0; q < hx[t].Length; q++)
            {
                if (ub > hx[t][q]) continue;
                if (hSupT[t][q] > 0) hTot[t][q].SetCoefficient(v, hTot[t][q].GetCoefficient(v) + sign * lb / hSupT[t][q]);
                else if (sign > 0) hTot[t][q].SetCoefficient(v, hTot[t][q].GetCoefficient(v) + 2);
                if (!freeToo) continue;
                if (hSupF[t][q] > 0) hFree[t][q].SetCoefficient(v, hFree[t][q].GetCoefficient(v) + sign * lb / hSupF[t][q]);
                else hFree[t][q].SetCoefficient(v, hFree[t][q].GetCoefficient(v) + 2);
            }
        }
        var obj = solver.Objective();
        obj.SetMaximization();

        var vf = new Variable?[n]; var vh = new Variable?[n]; var vs = new Variable?[n];
        var vy = new Variable?[n][];
        foreach (var m in inst.Models)
        {
            if (m.Lb.Length == 0) continue;
            double e = (double)m.Cost / inst.EnergyCap;
            bool srcOk = Enumerable.Range(0, m.Lb.Length).All(k => m.ReqOk(k) || hasSrc[m.Types[k]]);
            var pick = solver.MakeConstraint(double.NegativeInfinity, 1.0);
            vy[m.Id] = new Variable?[m.Lb.Length];

            if (m.AllReqOk && m.Value > 0 && m.Types.All(t => inst.FreeSupply[t] > 0))
            {
                var x = vf[m.Id] = NewVar();
                obj.SetCoefficient(x, m.Value); pick.SetCoefficient(x, 1); energy.SetCoefficient(x, e);
                for (int k = 0; k < m.Lb.Length; k++)
                {
                    int t = m.Types[k];
                    free[t].SetCoefficient(x, free[t].GetCoefficient(x) + (double)m.Lb[k] / inst.FreeSupply[t]);
                    total[t].SetCoefficient(x, total[t].GetCoefficient(x) + (double)m.Lb[k] / inst.Supply[t]);
                    Hall(x, t, m.Lb[k], m.Ub[k], 1, true);
                }
            }
            if (m.Value > 0 && srcOk)
            {
                var x = vh[m.Id] = NewVar();
                obj.SetCoefficient(x, m.Value / 2.0); pick.SetCoefficient(x, 1); energy.SetCoefficient(x, e);
                for (int k = 0; k < m.Lb.Length; k++)
                {
                    int t = m.Types[k];
                    if (!m.ReqOk(k)) { token[t].SetCoefficient(x, token[t].GetCoefficient(x) + 1); continue; }
                    total[t].SetCoefficient(x, total[t].GetCoefficient(x) + (double)m.Lb[k] / inst.Supply[t]);
                    Hall(x, t, m.Lb[k], m.Ub[k], 1, false);
                    if (!hasSrc[t]) continue;
                    var y = vy[m.Id][k] = NewVar();
                    token[t].SetCoefficient(y, 1);
                    total[t].SetCoefficient(y, -(double)m.Lb[k] / inst.Supply[t]);
                    Hall(y, t, m.Lb[k], m.Ub[k], -1, false);
                    var link = solver.MakeConstraint(double.NegativeInfinity, 0);
                    link.SetCoefficient(y, 1); link.SetCoefficient(x, -1);
                }
            }
            if (m.IsMono && m.AllReqOk)
            {
                int t = m.Types[0];
                var x = vs[m.Id] = NewVar();
                pick.SetCoefficient(x, 1); energy.SetCoefficient(x, e);
                total[t].SetCoefficient(x, total[t].GetCoefficient(x) + (double)m.Lb[0] / inst.Supply[t]);
                Hall(x, t, m.Lb[0], m.Ub[0], 1, false);
                token[t].SetCoefficient(x, -1);
            }
        }

        var status = solver.Solve();
        if (status != Google.OrTools.LinearSolver.Solver.ResultStatus.OPTIMAL && status != Google.OrTools.LinearSolver.Solver.ResultStatus.FEASIBLE)
            throw new InvalidOperationException("LP non résolu : " + status);
        g.Bound = obj.Value();
        for (int i = 0; i < n; i++)
        {
            g.Xf[i] = vf[i]?.SolutionValue() ?? 0;
            g.Xh[i] = vh[i]?.SolutionValue() ?? 0;
            g.Xs[i] = vs[i]?.SolutionValue() ?? 0;
            if (vy[i] == null) continue;
            for (int k = 0; k < vy[i]!.Length; k++)
                g.TokenReq[i][k] = !inst.Models[i].ReqOk(k) ||
                    (vy[i]![k] is { } y && g.Xh[i] > 1e-6 && y.SolutionValue() >= 0.5 * g.Xh[i]);
        }
        if (lpDuals != null) { g.Duals = lpDuals; return g; }
        g.Duals = new Prices
        {
            Energy = Math.Abs(energy.DualValue()),
            Free = free.Select(c => Math.Abs(c.DualValue())).ToArray(),
            Total = total.Select(c => Math.Abs(c.DualValue())).ToArray(),
            Token = token.Select((c, t) => hasSrc[t] ? Math.Abs(c.DualValue()) : 1e18).ToArray()
        };
        return g;
    }
}
