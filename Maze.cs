internal sealed class Maze {
    public const int Width = 15, Height = 11;
    const int Keep = 5 * Width + 7;
    static readonly (int X, int Y)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    readonly HashSet<int> corridor;
    public Dictionary<int, int[]> Routes { get; }
    public Dictionary<int, int[][]> RouteOptions { get; }
    public int[] FenceCells { get; }

    public Maze() {
        while (true) {
            var routes = GenerateRoutes();
            var paths = routes.Values.ToArray();
            int[] users = new int[Width * Height];
            for (int gate = 0; gate < paths.Length; gate++)
                foreach (int cell in paths[gate]) users[cell] |= 1 << gate;
            if (!paths.All(path => IsWinding(path, users))) continue;
            if (AddDetours(paths, users) is not {} alternates) continue;

            Routes = routes;
            RouteOptions = alternates.ToDictionary(options => options[0][0]);
            corridor = alternates.SelectMany(options => options).SelectMany(path => path).ToHashSet();
            FenceCells = corridor.Where(CanFence).Order().ToArray();
            break;
        }
    }

    internal static int[] PickRoute(int[][] options, Func<double> roll) {
        for (int step = 1; options.Length > 1; step++) {
            int next = options[0][step];
            if (options.All(path => path[step] == next)) continue;

            // Count each exit once, even when a fork farther down that exit adds more paths
            var branches = options.GroupBy(path => path[step]).Select(group => (
                Paths: group.ToArray(), Weight: 1.0 / (group.Min(path => path.Length) - step)
            )).ToArray();
            double pick = roll() * branches.Sum(branch => branch.Weight);
            options = branches[^1].Paths;
            foreach (var branch in branches) {
                pick -= branch.Weight;
                if (pick >= 0) continue;
                options = branch.Paths;
                break;
            }
        }
        return options[0];
    }

    static bool IsWinding(int[] path, int[] users) {
        if (path.Length > 39) return false;
        int turns = 0, separate = 0;
        for (int i = 0; i < path.Length; i++) {
            if (i >= 2 && path[i] - path[i - 1] != path[i - 1] - path[i - 2]) turns++;
            if (users[path[i]] == users[path[0]]) separate++;
        }
        return turns >= 4 && separate >= 6;
    }

    readonly record struct Detour(int Start, int Gates, int[] Interior);

    static int[][][]? AddDetours(int[][] paths, int[] users) {
        List<Detour> choices = [];
        HashSet<int> seen = [];
        foreach (var path in paths)
            for (int i = 1; i + 2 < path.Length; i += 2) {
                int start = path[i], mid = path[i + 1], end = path[i + 2];
                if (users[start] != users[mid] || !seen.Add(start)) continue;
                int step = mid - start;
                foreach (int sign in new[] { -1, 1 })
                    for (int depth = 2; depth <= 4; depth += 2) {
                        int dx = Math.Abs(step) == Width ? sign * depth : 0;
                        int dy = Math.Abs(step) == 1 ? sign * depth : 0;
                        int x = start % Width + dx, y = start / Width + dy;
                        if (x <= 0 || x >= Width - 1 || y <= 0 || y >= Height - 1) continue;
                        int across = (dx + dy * Width) / depth;
                        int[] cells = new int[2 * depth + 1];
                        for (int d = 1; d <= depth; d++) {
                            cells[d - 1] = start + d * across;
                            cells[cells.Length - d] = end + d * across;
                        }
                        cells[depth] = mid + depth * across;
                        if (cells.All(cell => users[cell] == 0)) choices.Add(new(start, users[start], cells));
                    }
            }

        var shuffled = choices.ToArray();
        Random.Shared.Shuffle(shuffled);
        var occupied = new HashSet<int>();
        var selected = new List<Detour>();
        if (!Cover((1 << paths.Length) - 1)) return null;
        var extra = shuffled.FirstOrDefault(d => Fits(d) && selected.All(s => s.Start != d.Start));
        if (extra.Interior is null) return null;
        selected.Add(extra);

        return paths.Select((path, gate) => {
            List<int[]> variants = [path];
            foreach (var detour in selected.Where(d => (d.Gates & (1 << gate)) != 0)) {
                var alternatives = variants.Select(route => {
                    int split = Array.IndexOf(route, detour.Start);
                    return (int[])[..route.AsSpan(0, split + 1), ..detour.Interior, ..route.AsSpan(split + 2)];
                }).ToArray();
                variants.AddRange(alternatives);
            }
            return variants.ToArray();
        }).ToArray();

        bool Fits(Detour detour) => !detour.Interior.Any(occupied.Contains);
        bool Cover(int remaining) {
            if (remaining == 0) return true;
            int first = remaining & -remaining;
            foreach (var detour in shuffled) {
                if ((detour.Gates & first) == 0 || (detour.Gates & remaining) != detour.Gates || !Fits(detour)) continue;
                occupied.UnionWith(detour.Interior);
                selected.Add(detour);
                if (Cover(remaining ^ detour.Gates)) return true;
                selected.RemoveAt(selected.Count - 1);
                occupied.ExceptWith(detour.Interior);
            }
            return false;
        }
    }

    static Dictionary<int, int[]> GenerateRoutes() {
        Dictionary<int, int[]> routes = new();
        int[] parent = new int[Width * Height];
        Array.Fill(parent, -1);
        parent[Keep] = Keep;

        List<(int From, int To)> frontier = [];
        AddFrontier(Keep);
        while (frontier.Count > 0) {
            int index = Random.Shared.Next(4) == 0 ? frontier.Count - 1 : Random.Shared.Next(frontier.Count);
            var (from, to) = frontier[index];
            frontier.RemoveAt(index);
            if (parent[to] != -1) continue;

            int passage = (from + to) / 2;
            parent[to] = passage;
            parent[passage] = from;
            AddFrontier(to);
        }

        int leftY = Random.Shared.Next(2) == 0 ? 3 : 7;
        int[] gateColumns = [3, 5, 9, 11];
        AddRoute(leftY * Width, leftY * Width + 1);
        int top = gateColumns[Random.Shared.Next(gateColumns.Length)];
        AddRoute(top, top + Width);
        int bottom = (Height - 1) * Width + gateColumns[Random.Shared.Next(gateColumns.Length)];
        AddRoute(bottom, bottom - Width);
        return routes;

        void AddFrontier(int cell) {
            foreach (var (dx, dy) in Directions) {
                int x = cell % Width + 2 * dx, y = cell / Width + 2 * dy;
                if (x > 0 && x < Width - 1 && y > 0 && y < Height - 1 && parent[y * Width + x] == -1)
                    frontier.Add((cell, y * Width + x));
            }
        }

        void AddRoute(int gate, int first) {
            List<int> path = [gate];
            for (int cell = first; cell != Keep; cell = parent[cell]) path.Add(cell);
            path.Add(Keep);
            routes.Add(gate, path.ToArray());
        }
    }

    public bool CanFence(int cell) => cell != Keep && !Routes.ContainsKey(cell) && corridor.Contains(cell);

    public bool CanBuild(int cell) {
        if ((uint)cell >= Width * Height || corridor.Contains(cell)) return false;
        return Directions.Any(d => {
            int x = cell % Width + d.X, y = cell / Width + d.Y;
            return (uint)x < Width && (uint)y < Height && corridor.Contains(y * Width + x);
        });
    }
}
