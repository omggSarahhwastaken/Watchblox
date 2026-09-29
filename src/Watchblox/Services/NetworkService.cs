using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Watchblox.Models;

namespace Watchblox.Services
{
    public class NetworkNode
    {
        public long UserId;
        public string DisplayName = "";
        public string Username = "";
        public PresenceType Status;
        public double X, Y;   // force-layout coordinates (world units)
        public int Degree;    // number of connections in the graph
    }

    public class NetworkGraph
    {
        public List<NetworkNode> Nodes = new List<NetworkNode>();
        public List<(int A, int B)> Edges = new List<(int, int)>();
    }

    /// <summary>
    /// Builds the mutual-friend network: one node per friend, one edge per
    /// pair of friends who have each other added. Friend lists come from the
    /// MutualService cache (1h TTL) so repeat builds are instant; fresh
    /// fetches are throttled to a few requests per second with progress and
    /// cancellation. Layout is a Fruchterman-Reingold force simulation run
    /// on a background thread.
    /// </summary>
    public class NetworkService
    {
        private readonly MutualService _mutuals;
        public NetworkService(MutualService mutuals) { _mutuals = mutuals; }

        public async Task<NetworkGraph> BuildAsync(
            List<FriendEntry> friends,
            IProgress<(int done, int total)> progress,
            CancellationToken ct)
        {
            var graph = new NetworkGraph();
            foreach (var f in friends)
                graph.Nodes.Add(new NetworkNode
                {
                    UserId = f.UserId,
                    DisplayName = f.DisplayName,
                    Username = f.Username,
                    Status = f.Status
                });

            var sets = new Dictionary<long, HashSet<long>>(graph.Nodes.Count);
            int done = 0;
            foreach (var n in graph.Nodes)
            {
                ct.ThrowIfCancellationRequested();
                bool cached = _mutuals.IsCached(n.UserId);
                sets[n.UserId] = await _mutuals.GetFriendIdsAsync(n.UserId);
                if (!cached)
                    await Task.Delay(200, ct); // ~5 fresh fetches/sec max
                progress?.Report((++done, graph.Nodes.Count));
            }

            int count = graph.Nodes.Count;
            for (int i = 0; i < count; i++)
            {
                var setA = sets[graph.Nodes[i].UserId];
                long idA = graph.Nodes[i].UserId;
                for (int j = i + 1; j < count; j++)
                {
                    long idB = graph.Nodes[j].UserId;
                    // Roblox friendship is symmetric; check both sides anyway
                    // in case one side's cached list is stale.
                    if (setA.Contains(idB) || sets[idB].Contains(idA))
                        graph.Edges.Add((i, j));
                }
                if ((i & 63) == 0) ct.ThrowIfCancellationRequested();
            }
            foreach (var (a, b) in graph.Edges)
            {
                graph.Nodes[a].Degree++;
                graph.Nodes[b].Degree++;
            }

            Layout(graph, ct);
            return graph;
        }

        private static void Layout(NetworkGraph g, CancellationToken ct)
        {
            int n = g.Nodes.Count;
            if (n == 0) return;
            if (n == 1) { g.Nodes[0].X = 500; g.Nodes[0].Y = 500; return; }

            var rnd = new Random(12345);
            foreach (var nd in g.Nodes)
            {
                nd.X = rnd.NextDouble() * 1000;
                nd.Y = rnd.NextDouble() * 1000;
            }

            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            foreach (var (a, b) in g.Edges) { adj[a].Add(b); adj[b].Add(a); }

            double k = Math.Sqrt(1000.0 * 1000.0 / n);
            double k2 = k * k;
            int iterations = Math.Clamp(30000 / n, 40, 250);
            double temp = 100.0;
            double cool = Math.Pow(0.05 / temp, 1.0 / iterations);

            var dx = new double[n];
            var dy = new double[n];
            for (int it = 0; it < iterations; it++)
            {
                if ((it & 7) == 0) ct.ThrowIfCancellationRequested();
                Array.Clear(dx, 0, n);
                Array.Clear(dy, 0, n);

                // repulsion between every pair
                for (int i = 0; i < n; i++)
                {
                    var ni = g.Nodes[i];
                    for (int j = i + 1; j < n; j++)
                    {
                        var nj = g.Nodes[j];
                        double ddx = ni.X - nj.X, ddy = ni.Y - nj.Y;
                        double dist = Math.Sqrt(ddx * ddx + ddy * ddy) + 0.01;
                        double f = k2 / dist;
                        double fx = f * ddx / dist, fy = f * ddy / dist;
                        dx[i] += fx; dy[i] += fy;
                        dx[j] -= fx; dy[j] -= fy;
                    }
                }
                // attraction along edges
                foreach (var (a, b) in g.Edges)
                {
                    var na = g.Nodes[a]; var nb = g.Nodes[b];
                    double ddx = na.X - nb.X, ddy = na.Y - nb.Y;
                    double dist = Math.Sqrt(ddx * ddx + ddy * ddy) + 0.01;
                    double f = dist * dist / k;
                    double fx = f * ddx / dist, fy = f * ddy / dist;
                    dx[a] -= fx; dy[a] -= fy;
                    dx[b] += fx; dy[b] += fy;
                }
                // weak gravity toward the center keeps loners from drifting off
                for (int i = 0; i < n; i++)
                {
                    var nd = g.Nodes[i];
                    dx[i] -= (nd.X - 500) * 0.02;
                    dy[i] -= (nd.Y - 500) * 0.02;
                }
                // apply displacement, capped by temperature
                for (int i = 0; i < n; i++)
                {
                    var nd = g.Nodes[i];
                    double ddx = dx[i], ddy = dy[i];
                    double disp = Math.Sqrt(ddx * ddx + ddy * ddy);
                    if (disp > 0.001)
                    {
                        double limited = Math.Min(disp, temp);
                        nd.X += ddx / disp * limited;
                        nd.Y += ddy / disp * limited;
                    }
                }
                temp *= cool;
            }
        }
    }
}
