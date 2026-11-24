namespace TruckKun.Tower.Training;

internal static class AttackPanel {
    internal static readonly string[] Styles = ["screened-carrier", "siege-column", "split-pressure"];

    internal static Move? Pick(string style, Duel.PlanningView view) {
        string[] preference = style switch {
            "screened-carrier" when view.OrderedGates.Length == 0 &&
                view.Gold >= Duel.PriceOf("apc")!.Value + Duel.PriceOf("swarm")!.Value => ["swarm", "apc"],
            "screened-carrier" => ["apc", "runner", "swarm", "smart"],
            "siege-column" when view.OrderedGates.Length == 0 &&
                view.Gold >= Duel.PriceOf("giant")!.Value + Duel.PriceOf("ranged")!.Value => ["giant", "ranged"],
            "siege-column" => ["ranged", "armored", "giant", "swarm", "runner"],
            "split-pressure" => ["runner", "smart", "swarm", "giant"],
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        };
        foreach (var type in preference) {
            var choices = view.Purchases.Where(move => move.Action == type).ToArray();
            if (choices.Length == 0) continue;
            if (style != "split-pressure" && view.OrderedGates.Length > 0 &&
                choices.FirstOrDefault(move => move.Cell == view.OrderedGates[0]) is { } same) return same;
            return choices.MinBy(move => Risk(move.Cell!.Value) +
                (style == "split-pressure" ? 5 * view.OrderedGates.Count(gate => gate == move.Cell) : 0));
        }
        return null;

        double Risk(int gate) => view.Routes.First(paths => paths[0][0] == gate).Average(path => path.Sum(tile =>
            view.Buildings.Sum(building => building.Type switch {
                "mine" => building.Cell == tile ? 8 : 0,
                "spikes" => building.Cell == tile ? 2 : 0,
                _ => Duel.Covers(building.Type, building.Cell, tile) ? 1 : 0
            }) + (view.Fences.Any(fence => fence.Cell == tile) ? 3 : 0))) +
            view.Routes.First(paths => paths[0][0] == gate)[0].Length * 0.1;
    }
}
