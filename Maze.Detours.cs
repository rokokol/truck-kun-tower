internal sealed partial class Maze {
    const int WideCheckLimit = 4096, WideStartLimit = 16;
    static readonly (int Span, int Depth)[] DetourShapes = [(3, 3), (3, 4), (4, 3), (2, 2), (2, 4)];

    sealed record Loop(int Gates, int[] Spine, int[] Bypass, int[] Ground) {
        public bool Wide => Ground.Length >= 4;
    }

    static IEnumerable<Loop> FindLoops(int[][] paths, int[] users) {
        HashSet<(int Start, int End, int Depth, int Sign)> seen = [];
        foreach (var path in paths)
            for (int i = 1; i < path.Length - 1; i++)
                foreach (var (span, depth) in DetourShapes) {
                    if (i + span >= path.Length || (span == 2 && i % 2 == 0)) continue;
                    int start = path[i], end = path[i + span], step = path[i + 1] - start;
                    var spine = path[i..(i + span + 1)];
                    if (spine.Where((cell, j) => cell != start + j * step ||
                        (j < span && users[cell] != users[start])).Any()) continue;
                    foreach (int sign in new[] { -1, 1 }) {
                        if (!seen.Add((start, end, depth, sign))) continue;
                        int dx = Math.Abs(step) == Width ? sign : 0;
                        int dy = Math.Abs(step) == 1 ? sign : 0;
                        if (!Inner(start, dx, dy, depth) || !Inner(end, dx, dy, depth)) continue;
                        int across = dx + dy * Width;
                        int[] bypass = [.. Enumerable.Range(1, depth).Select(d => start + d * across),
                            .. Enumerable.Range(1, span).Select(s => start + depth * across + s * step),
                            .. Enumerable.Range(1, depth - 1).Reverse().Select(d => end + d * across)];
                        int[] ground = (from along in Enumerable.Range(1, span - 1)
                                        from aside in Enumerable.Range(1, depth - 1)
                                        select start + along * step + aside * across).ToArray();
                        if (bypass.All(cell => users[cell] == 0) && ground.All(cell => users[cell] == 0))
                            yield return new(users[start], spine, bypass, ground);
                    }
                }

        static bool Inner(int cell, int dx, int dy, int depth) {
            int x = cell % Width + dx * depth, y = cell / Width + dy * depth;
            return x > 0 && x < Width - 1 && y > 0 && y < Height - 1;
        }
    }

    static int[][][]? WideDetours(int[][] paths, int[] users, Random random) {
        var candidates = FindLoops(paths, users).ToArray();
        random.Shuffle(candidates);
        candidates = candidates.OrderByDescending(loop => loop.Wide).ToArray();
        List<Loop> selected = [];
        Loop[] best = [];
        int[] uses = new int[paths.Length];
        int checks = 0;
        foreach (var first in candidates.TakeWhile(loop => loop.Wide).Take(WideStartLimit)) {
            if (checks >= WideCheckLimit) break;
            Add(first);
            if (Complete() && selected.Count(loop => loop.Wide) > best.Count(loop => loop.Wide))
                best = selected.ToArray();
            if (best.Count(loop => loop.Wide) >= 2) break;
            while (selected.Count > 0) RemoveLast();
        }
        return best.Length == 0 ? null : paths.Select((path, gate) => Variants(path, gate, best)).ToArray();

        void Add(Loop loop) {
            selected.Add(loop);
            for (int gate = 0; gate < uses.Length; gate++) if (Serves(loop, gate)) uses[gate]++;
        }
        void RemoveLast() {
            var loop = selected[^1];
            selected.RemoveAt(selected.Count - 1);
            for (int gate = 0; gate < uses.Length; gate++) if (Serves(loop, gate)) uses[gate]--;
        }
        bool Complete() {
            int gap = Array.IndexOf(uses, 0);
            if (gap < 0 && uses.Contains(2)) return true;
            foreach (var loop in candidates) {
                if (checks++ >= WideCheckLimit) return false;
                if (gap >= 0 && !Serves(loop, gap)) continue;
                if (Enumerable.Range(0, uses.Length).Any(gate => uses[gate] == 2 && Serves(loop, gate))) continue;
                if (selected.Any(other => !Compatible(loop, other))) continue;
                Add(loop);
                if (Complete()) return true;
                RemoveLast();
            }
            return false;
        }
    }

    static bool Serves(Loop loop, int gate) => (loop.Gates & (1 << gate)) != 0;

    static bool Compatible(Loop a, Loop b) =>
        !a.Spine.SkipLast(1).Intersect(b.Spine.SkipLast(1)).Any() &&
        !a.Bypass.Intersect(b.Bypass).Any() &&
        !a.Bypass.Intersect(b.Ground).Any() && !b.Bypass.Intersect(a.Ground).Any();

    static int[][] Variants(int[] path, int gate, Loop[] selected) {
        var replacements = selected.Where(loop => Serves(loop, gate))
            .Select(loop => (Loop: loop, Start: Array.IndexOf(path, loop.Spine[0])))
            .OrderBy(item => item.Start).ToArray();
        int count = 1 << replacements.Length;
        int[][] options = new int[count][];
        options[0] = path;
        for (int mask = 1; mask < count; mask++) {
            List<int> route = [];
            int from = 0;
            for (int i = 0; i < replacements.Length; i++) {
                if ((mask & (1 << i)) == 0) continue;
                var (loop, start) = replacements[i];
                route.AddRange(path.AsSpan(from, start + 1 - from));
                route.AddRange(loop.Bypass);
                from = start + loop.Spine.Length - 1;
            }
            route.AddRange(path.AsSpan(from));
            options[mask] = route.ToArray();
        }
        return options;
    }
}
