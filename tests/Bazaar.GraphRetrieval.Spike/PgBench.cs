using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npgsql;

namespace Bazaar.GraphRetrieval.Spike;

/// <summary>Exact pgvector arm (sequential scan, cosine distance over halfvec), the reference of doc 64 section 13.2 arm 1.</summary>
internal static class PgBench
{
    public const string Header = "arm,dim,n,k,clients,reps,e2e_p50_ms,e2e_p99_ms,server_p50_ms,server_p99_ms,qps,recall_mean,recall_min,note";

    public static int Run(string csvPath, bool quick)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        string connection = Environment.GetEnvironmentVariable("S28_PG") ?? "Host=127.0.0.1;Port=34050;Username=postgres;Password=s28;Database=postgres;Maximum Pool Size=40;Timeout=60;Command Timeout=600";
        var csv = new List<string> { Header };
        void Emit(string arm, int dim, long n, int k, int clients, IReadOnlyList<double> wall, IReadOnlyList<double> server, double qps, IReadOnlyList<double> recall, string note)
        {
            string line = string.Join(',', arm, dim, n, k, clients, wall.Count, TopKTools.Percentile(wall, 0.5).ToString("F3", ci), TopKTools.Percentile(wall, 0.99).ToString("F3", ci),
                TopKTools.Percentile(server, 0.5).ToString("F3", ci), TopKTools.Percentile(server, 0.99).ToString("F3", ci), qps.ToString("F1", ci), recall.Count == 0 ? "NA" : recall.Average().ToString("F4", ci), recall.Count == 0 ? "NA" : recall.Min().ToString("F4", ci), note);
            csv.Add(line);
            File.WriteAllLines(csvPath, csv);
            Console.WriteLine(line);
        }

        (int Dim, long N)[] configs = quick ? [(1024, 10_000), (1024, 100_000)] : [(1024, 10_000), (1024, 100_000), (1024, 1_000_000), (768, 100_000), (768, 1_000_000), (1536, 100_000)];
        foreach ((int dim, long n) in configs)
        {
            var data = new VectorData(n, dim);
            float[][] pool = [.. Enumerable.Range(0, 32).Select(data.PlantedQuery)];
            TopK[] exact = new CpuVectorEngine(data, Environment.ProcessorCount).Search(pool, 50);
            string table = $"s28_{dim}_{n}";
            using var admin = new NpgsqlConnection(connection);
            admin.Open();
            Exec(admin, "CREATE EXTENSION IF NOT EXISTS vector");
            bool exists = Scalar(admin, $"SELECT to_regclass('{table}') IS NOT NULL") is true;
            var load = Stopwatch.StartNew();
            if (!exists)
            {
                Exec(admin, $"CREATE TABLE {table} (id int PRIMARY KEY, emb halfvec({dim}))");
                // A halfvec above about 1000 dimensions exceeds the 2 kB TOAST threshold; without PLAIN storage every row is compressed or moved out of line and an exact scan pays to detoast it.
                Exec(admin, $"ALTER TABLE {table} ALTER COLUMN emb SET STORAGE PLAIN");
                using (var writer = admin.BeginTextImport($"COPY {table} (id, emb) FROM STDIN"))
                {
                    var sb = new StringBuilder();
                    for (long row = 0; row < n; row++)
                    {
                        sb.Clear();
                        sb.Append(row).Append('\t').Append('[');
                        ReadOnlySpan<float> v = data.Row(row);
                        for (int d = 0; d < dim; d++)
                        {
                            if (d > 0)
                            {
                                sb.Append(',');
                            }

                            sb.Append(v[d].ToString("R", ci));
                        }

                        sb.Append("]\n");
                        writer.Write(sb.ToString());
                    }
                }

                Exec(admin, $"VACUUM ANALYZE {table}");
            }

            string size = Scalar(admin, $"SELECT pg_size_pretty(pg_total_relation_size('{table}'))")?.ToString() ?? "?";
            Console.WriteLine($"-- {table} loaded in {load.Elapsed.TotalSeconds:F0}s, {size}");

            foreach (int k in new[] { 10, 50 })
            {
                foreach (int clients in new[] { 1, 8 })
                {
                    var wall = new List<double>();
                    var server = new List<double>();
                    var recall = new List<double>();
                    int perClient = quick ? 8 : (n >= 1_000_000 ? 12 : 48);
                    var watch = Stopwatch.StartNew();
                    object gate = new();
                    // one warm-up pass per client
                    Parallel.For(0, clients, new ParallelOptions { MaxDegreeOfParallelism = clients }, c =>
                    {
                        using var conn = new NpgsqlConnection(connection);
                        conn.Open();
                        Exec(conn, "SET enable_indexscan = off; SET enable_bitmapscan = off; SET max_parallel_workers_per_gather = " + (clients == 1 ? 8 : 2).ToString(ci));
                        for (int rep = -2; rep < perClient; rep++)
                        {
                            int q = (rep + 2 + (c * 5)) % pool.Length;
                            var timer = Stopwatch.StartNew();
                            long[] ids = Query(conn, table, pool[q], k);
                            double ms = timer.Elapsed.TotalMilliseconds;
                            double serverMs = rep >= 0 ? ServerTime(conn, table, pool[q], k) : 0;
                            if (rep == -2)
                            {
                                watch.Restart();
                            }

                            if (rep >= 0)
                            {
                                var truth = new HashSet<long>(exact[q].Rows[..k]);
                                double r = ids.Count(truth.Contains) / (double)k;
                                lock (gate)
                                {
                                    wall.Add(ms);
                                    server.Add(serverMs);
                                    recall.Add(r);
                                }
                            }
                        }
                    });
                    double qps = wall.Count / Math.Max(1e-9, watch.Elapsed.TotalSeconds);
                    Emit("pgvector-exact", dim, n, k, clients, wall, server, qps, recall, "halfvec cosine, sequential scan; 8 workers per query alone, 2 per query with 8 clients");
                }
            }
        }

        return 0;
    }

    /// <summary>Server-side execution time of the same query from EXPLAIN ANALYZE (no per-node timing), in milliseconds: the number without the client path.</summary>
    private static double ServerTime(NpgsqlConnection conn, string table, float[] query, int k)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < query.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(query[i].ToString("R", CultureInfo.InvariantCulture));
        }

        sb.Append(']');
        using var cmd = new NpgsqlCommand($"EXPLAIN (ANALYZE, TIMING OFF, SUMMARY ON) SELECT id FROM {table} ORDER BY emb <=> @q::halfvec LIMIT {k}", conn);
        cmd.Parameters.AddWithValue("q", sb.ToString());
        using NpgsqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string line = reader.GetString(0);
            if (line.StartsWith("Execution Time:", StringComparison.Ordinal))
            {
                return double.Parse(line["Execution Time:".Length..].Replace("ms", string.Empty).Trim(), CultureInfo.InvariantCulture);
            }
        }

        return double.NaN;
    }

    private static long[] Query(NpgsqlConnection conn, string table, float[] query, int k)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < query.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(query[i].ToString("R", CultureInfo.InvariantCulture));
        }

        sb.Append(']');
        using var cmd = new NpgsqlCommand($"SELECT id FROM {table} ORDER BY emb <=> @q::halfvec LIMIT {k}", conn);
        cmd.Parameters.AddWithValue("q", sb.ToString());
        var ids = new List<long>(k);
        using NpgsqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return [.. ids];
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 3600 };
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        return cmd.ExecuteScalar();
    }
}
