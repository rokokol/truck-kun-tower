namespace TruckKun.Tower.Training;

// The trainer only names the network: Bots/BotBrain.cs holds the pass and is linked into both
// assemblies, so a trained weight vector means the same thing to a bot in the game
internal static class NeuralDefense {
    internal static double[] Create(Random random) => BotBrain.Create(random);

    internal static Move? Pick(Duel.PlanningView view, double[] weights, int side = Duel.Defender) =>
        BotBrain.Pick(view, weights, side);
}
