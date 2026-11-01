using System.Numerics;

namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// ForceAtlas2-style layout with Barnes-Hut repulsion, run in fixed ticks. Positions are stored x,y,z per node (z = 0
/// when <see cref="LayoutOptions.Dimensions"/> is 2). Pure C#: no renderer types and no dependencies.
/// </summary>
public sealed class ForceLayout
{
    private const double _minimumJitterEstimate = 0.05;
    private const double _maximumJitterFactor = 10;
    private const double _minimumSpeedEfficiency = 0.05;

    private readonly LayoutOptions _options;
    private readonly double[] _xyz;
    private readonly double[] _mass;
    private readonly double[] _force;
    private readonly double[] _previousForce;
    private readonly double[] _swing;
    private readonly int[] _edgeSource;
    private readonly int[] _edgeTarget;
    private readonly BarnesHutTree _tree = new();
    private double _globalSpeed = 1;
    private double _speedEfficiency = 1;

    /// <param name="graph">The graph to lay out.</param>
    /// <param name="options">Tuning and seed. Defaults when null.</param>
    /// <param name="warmStart">
    /// Previous positions in node order (packed x,y,z, length 3 * node count). Entries that are NaN or infinite
    /// get a seeded start position. Null means a cold start.
    /// </param>
    public ForceLayout(LayoutGraph graph, LayoutOptions? options = null, ReadOnlyMemory<float>? warmStart = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _options = options ?? new LayoutOptions();
        ValidateOptions(_options);

        var n = graph.NodeCount;
        _xyz = new double[3 * n];
        _mass = new double[n];
        _force = new double[3 * n];
        _previousForce = new double[3 * n];
        _swing = new double[n];

        _edgeSource = new int[graph.Edges.Count];
        _edgeTarget = new int[graph.Edges.Count];
        for (var k = 0; k < graph.Edges.Count; k++)
        {
            _edgeSource[k] = graph.Edges[k].Source;
            _edgeTarget[k] = graph.Edges[k].Target;
            _mass[graph.Edges[k].Source] += 1;
            _mass[graph.Edges[k].Target] += 1;
        }

        for (var i = 0; i < n; i++)
        {
            _mass[i] += 1; // ForceAtlas2 mass is degree + 1.
        }

        if (warmStart is { } previous && previous.Length != 3 * n)
        {
            throw new ArgumentException($"Warm start has {previous.Length} values; expected {3 * n} (x,y,z per node).", nameof(warmStart));
        }

        InitialisePositions(warmStart);
    }

    /// <summary>Number of nodes in the layout.</summary>
    public int NodeCount => _mass.Length;

    /// <summary>Number of ticks run so far.</summary>
    public long Ticks { get; private set; }

    /// <summary>Largest distance any node moved in the last tick. Zero before the first tick.</summary>
    public double LastMaxDisplacement { get; private set; }

    /// <summary>Runs <paramref name="ticks"/> ticks.</summary>
    public void Step(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (var t = 0; t < ticks; t++)
        {
            Tick();
        }
    }

    /// <summary>
    /// Runs ticks until the largest move in one tick is at or below <paramref name="tolerance"/>, or until
    /// <paramref name="maxTicks"/> ticks have run. Returns the number of ticks run by this call.
    /// </summary>
    public int RunUntilSettled(double tolerance, int maxTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTicks);
        var ran = 0;
        while (ran < maxTicks)
        {
            Tick();
            ran++;
            if (LastMaxDisplacement <= tolerance)
            {
                break;
            }
        }

        return ran;
    }

    /// <summary>Position of one node, by index in the graph's node order.</summary>
    public Vector3 GetPosition(int index)
    {
        return new Vector3((float)_xyz[3 * index], (float)_xyz[(3 * index) + 1], (float)_xyz[(3 * index) + 2]);
    }

    /// <summary>Copies all positions as packed x,y,z (stride 3), in node order.</summary>
    public float[] GetPositions()
    {
        var result = new float[_xyz.Length];
        for (var i = 0; i < _xyz.Length; i++)
        {
            result[i] = (float)_xyz[i];
        }

        return result;
    }

    private static void ValidateOptions(LayoutOptions options)
    {
        if (options.Dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Dimensions must be 2 or 3.");
        }

        RequirePositive(options.ScalingRatio, nameof(options.ScalingRatio));
        RequirePositive(options.Gravity, nameof(options.Gravity));
        RequirePositive(options.EdgeStrength, nameof(options.EdgeStrength));
        RequirePositive(options.JitterTolerance, nameof(options.JitterTolerance));
        RequirePositive(options.MaxDisplacement, nameof(options.MaxDisplacement));
        RequirePositive(options.InitialSpread, nameof(options.InitialSpread));
        if (!double.IsFinite(options.Theta) || options.Theta < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Theta must be a finite value of at least 0.");
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Must be a finite value above 0.");
        }
    }

    private void InitialisePositions(ReadOnlyMemory<float>? warmStart)
    {
        var n = _mass.Length;
        var random = new SeededRandom(_options.Seed);
        var spread = _options.InitialSpread * Math.Max(1, Math.Sqrt(n));
        var warm = warmStart ?? ReadOnlyMemory<float>.Empty;

        for (var i = 0; i < n; i++)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var seeded = ((random.NextDouble() * 2) - 1) * spread;
                var index = (3 * i) + axis;
                if (_options.Dimensions == 2 && axis == 2)
                {
                    seeded = 0;
                }

                var value = seeded;
                if (index < warm.Length && double.IsFinite(warm.Span[index]))
                {
                    value = warm.Span[index];
                }

                _xyz[index] = _options.Dimensions == 2 && axis == 2 ? 0 : value;
            }
        }
    }

    private void Tick()
    {
        var n = _mass.Length;
        Ticks++;
        if (n == 0)
        {
            LastMaxDisplacement = 0;
            return;
        }

        Array.Clear(_force);
        _tree.Build(_xyz, _mass);
        _tree.AccumulateRepulsion(_xyz, _mass, _options.ScalingRatio, _options.Theta, _force);
        AddGravityAndEdgeForces();

        // Adaptive global speed (ForceAtlas2): the swing is how much each force changed; traction is how steady it is.
        double totalSwing = 0;
        double totalTraction = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = _force[3 * i] - _previousForce[3 * i];
            var dy = _force[(3 * i) + 1] - _previousForce[(3 * i) + 1];
            var dz = _force[(3 * i) + 2] - _previousForce[(3 * i) + 2];
            var sx = _force[3 * i] + _previousForce[3 * i];
            var sy = _force[(3 * i) + 1] + _previousForce[(3 * i) + 1];
            var sz = _force[(3 * i) + 2] + _previousForce[(3 * i) + 2];
            _swing[i] = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var traction = Math.Sqrt((sx * sx) + (sy * sy) + (sz * sz)) / 2;
            totalSwing += _mass[i] * _swing[i];
            totalTraction += _mass[i] * traction;
        }

        UpdateGlobalSpeed(n, totalSwing, totalTraction);

        double maxMove = 0;
        for (var i = 0; i < n; i++)
        {
            var nodeSpeed = _globalSpeed / (1 + Math.Sqrt(_globalSpeed * _swing[i]));
            var mx = nodeSpeed * _force[3 * i];
            var my = nodeSpeed * _force[(3 * i) + 1];
            var mz = _options.Dimensions == 2 ? 0 : nodeSpeed * _force[(3 * i) + 2];
            var move = Math.Sqrt((mx * mx) + (my * my) + (mz * mz));
            if (move > _options.MaxDisplacement)
            {
                var scale = _options.MaxDisplacement / move;
                mx *= scale;
                my *= scale;
                mz *= scale;
                move = _options.MaxDisplacement;
            }

            _xyz[3 * i] += mx;
            _xyz[(3 * i) + 1] += my;
            _xyz[(3 * i) + 2] += mz;
            maxMove = Math.Max(maxMove, move);
        }

        Array.Copy(_force, _previousForce, _force.Length);
        LastMaxDisplacement = maxMove;
    }

    private void AddGravityAndEdgeForces()
    {
        var n = _mass.Length;
        for (var i = 0; i < n; i++)
        {
            var x = _xyz[3 * i];
            var y = _xyz[(3 * i) + 1];
            var z = _xyz[(3 * i) + 2];
            var distance = Math.Sqrt((x * x) + (y * y) + (z * z));
            if (distance > 0)
            {
                // Constant-magnitude pull toward the origin, scaled by mass (ForceAtlas2 without strong gravity).
                var pull = _options.Gravity * _mass[i] / distance;
                _force[3 * i] -= x * pull;
                _force[(3 * i) + 1] -= y * pull;
                _force[(3 * i) + 2] -= z * pull;
            }
        }

        for (var k = 0; k < _edgeSource.Length; k++)
        {
            var a = _edgeSource[k];
            var b = _edgeTarget[k];
            if (a == b)
            {
                continue;
            }

            var dx = (_xyz[3 * b] - _xyz[3 * a]) * _options.EdgeStrength;
            var dy = (_xyz[(3 * b) + 1] - _xyz[(3 * a) + 1]) * _options.EdgeStrength;
            var dz = (_xyz[(3 * b) + 2] - _xyz[(3 * a) + 2]) * _options.EdgeStrength;
            _force[3 * a] += dx;
            _force[(3 * a) + 1] += dy;
            _force[(3 * a) + 2] += dz;
            _force[3 * b] -= dx;
            _force[(3 * b) + 1] -= dy;
            _force[(3 * b) + 2] -= dz;
        }
    }

    private void UpdateGlobalSpeed(int n, double totalSwing, double totalTraction)
    {
        // Gephi-style adaptive speed for ForceAtlas2, with the jitter tolerance scaled by graph size.
        var estimatedOptimalJitter = _minimumJitterEstimate * Math.Sqrt(n);
        var minJitter = Math.Sqrt(estimatedOptimalJitter);
        var jitter = _options.JitterTolerance * Math.Max(minJitter, Math.Min(_maximumJitterFactor, estimatedOptimalJitter * totalTraction / ((double)n * n)));

        if (totalSwing > 2 * totalTraction)
        {
            if (_speedEfficiency > _minimumSpeedEfficiency)
            {
                _speedEfficiency *= 0.5;
            }

            jitter = Math.Max(jitter, _options.JitterTolerance);
        }

        var targetSpeed = totalSwing > 0 ? jitter * _speedEfficiency * totalTraction / totalSwing : _globalSpeed;

        if (totalSwing > jitter * totalTraction)
        {
            if (_speedEfficiency > _minimumSpeedEfficiency)
            {
                _speedEfficiency *= 0.7;
            }
        }
        else if (_globalSpeed < 1000)
        {
            _speedEfficiency *= 1.3;
        }

        var delta = targetSpeed - _globalSpeed;
        _globalSpeed += delta > 0 ? Math.Min(delta, 0.5 * _globalSpeed) : Math.Max(delta, -0.5 * _globalSpeed);
    }
}
