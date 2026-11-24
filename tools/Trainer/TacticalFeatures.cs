namespace TruckKun.Tower.Training;

internal sealed class TacticalFeatures {
    readonly Duel.PlanningView view;
    readonly int side;
    readonly double[] fire = new double[Maze.Width * Maze.Height];
    readonly double[] armorFire = new double[Maze.Width * Maze.Height];
    readonly int[] sources = new int[Maze.Width * Maze.Height];
    readonly HashSet<int> frost = [];
    readonly Dictionary<int, Duel.BuildingView> traps;
    readonly Dictionary<int, Duel.Fence> fences;
    readonly Duel.BuildingView[] towers;

    internal TacticalFeatures(Duel.PlanningView view, int side) {
        this.view = view;
        this.side = side;
        towers = view.Buildings.Where(b => b.Type is not ("mine" or "spikes")).ToArray();
        traps = view.Buildings.Where(b => b.Type is "mine" or "spikes").ToDictionary(b => b.Cell);
        fences = view.Fences.ToDictionary(f => f.Cell);
        foreach (var tower in towers) {
            var (damage, interval) = Duel.Stats(tower.Type);
            for (int cell = 0; cell < fire.Length; cell++) {
                if (!Duel.Covers(tower.Type, tower.Cell, cell)) continue;
                if (damage == 0) frost.Add(cell);
                else {
                    fire[cell] += (double)damage / interval;
                    armorFire[cell] += (double)Duel.EffectiveDamage("armored", damage) / interval;
                    sources[cell]++;
                }
            }
        }
    }

    internal double Score(string type, int site, Func<string, double> weight) => side == Duel.Defender
        ? Placement(type, site, weight) : Order(type, site, weight);

    double Placement(string type, int site, Func<string, double> weight) {
        if (type is "mine" or "spikes") {
            double exposure = view.Routes.Average(gate => gate.Average(path => {
                int index = Array.IndexOf(path, site);
                if (index < 0) return 0.0;
                return type == "mine" ? 1 : 0.5 + fire[site] / 3;
            }));
            return weight("trapTiming") * exposure;
        }
        if (type == "fence") {
            double delay = view.Routes.Average(gate => gate.Average(path => {
                int index = Array.IndexOf(path, site);
                return index <= 0 ? 0 : Math.Min(4, fire[path[index - 1]] * (frost.Contains(path[index - 1]) ? 1.5 : 1));
            }));
            return weight("barrierTiming") * delay / 2;
        }
        var (damage, interval) = Duel.Stats(type);
        double coverage = 0, crossfire = 0;
        foreach (var gate in view.Routes) foreach (var path in gate) {
            foreach (int cell in path) {
                if (!Duel.Covers(type, site, cell)) continue;
                double importance = 1 + 3.0 / path.Length;
                coverage += importance * (damage == 0 ? fire[cell] * 0.5 : (double)damage / interval) / gate.Length;
                crossfire += (damage == 0 ? fire[cell] : Math.Min(2, sources[cell]) + (frost.Contains(cell) ? 0.75 : 0)) / gate.Length;
            }
        }
        return weight("damagePerCoin") * coverage / (view.Routes.Length * Duel.PriceOf(type)!.Value) +
            weight("crossfire") * crossfire / (view.Routes.Length * 9);
    }

    double Order(string type, int site, Func<string, double> weight) {
        var paths = view.Routes.First(gate => gate[0][0] == site);
        if (type == "smart") paths = [paths.MinBy(path => path.Length)!];
        double sumRisk = 0, worst = 0, siege = 0;
        double stepTicks = Duel.MoveTicks(type);
        var threat = type == "armored" ? armorFire : fire;
        int health = Duel.UnitHealth(type, view.Wave);
        foreach (var path in paths) {
            double damage = 0, ticks = 0, firstContact = 0;
            bool metTarget = false;
            for (int i = 0; i < path.Length; i++) {
                int cell = path[i];
                if (fences.TryGetValue(cell, out var fence) && type != "apc" && i > 0) {
                    double wait = Math.Ceiling((double)fence.Hp / Duel.FenceDamage(type)) * stepTicks;
                    ticks += wait;
                    damage += wait * threat[path[i - 1]];
                }
                if (!metTarget && type is ("ranged" or "armored") && towers.Any(tower => type == "ranged"
                    ? Duel.Covers(type, cell, tower.Cell) : Adjacent(cell, tower.Cell))) {
                    firstContact = Math.Max(0, 1 - damage / health) / (1 + ticks / 14);
                    metTarget = true;
                }
                double dwell = stepTicks * (frost.Contains(cell) ? 2 : 1);
                ticks += dwell;
                damage += dwell * threat[cell];
                if (traps.TryGetValue(cell, out var trap)) {
                    if (trap.Type == "mine") damage += health;
                    else if (type != "apc") damage += Duel.EffectiveDamage(type, Duel.SpikeDamage);
                }
            }
            double risk = Math.Min(4, damage / health);
            sumRisk += risk;
            worst = Math.Max(worst, risk);
            siege += firstContact;
        }
        return -weight("routeRisk") * (sumRisk / paths.Length + 0.35 * worst) +
            weight("siegeArrival") * siege / paths.Length;
    }

    static bool Adjacent(int a, int b) => Math.Abs(a % Maze.Width - b % Maze.Width) +
        Math.Abs(a / Maze.Width - b / Maze.Width) == 1;
}
