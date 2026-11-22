// Procedural graph topologies, evaluated from the node index alone: nothing is stored.
// Included by every topology shader. topo: 0 uniform random, 1 clustered (64 gaussian blobs), 2 power-law hub and
// spoke (1024 hubs), 3 grid or lattice, 4 chain or ring (a closed curve; a helix-modulated loop in 3D).
// dim 2 forces z to 0.

layout(push_constant) uniform Push
{
    mat4 viewProj;
    uint topo;
    uint dim;
    uint n;       // total nodes
    uint stride;  // index step between sampled nodes (draw passes)
    float pointSize;
    uint a;       // pass specific
    uint b;
    uint c;
} pc;

const uint HUBS = 1024u;

uint hashu(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float rnd(uint i, uint salt)
{
    return float(hashu(i * 0x9E3779B1u + salt * 0x85EBCA6Bu + 0x165667B1u) >> 8) * (1.0 / 16777216.0);
}

float gauss(uint i, uint salt)
{
    return (rnd(i, salt) + rnd(i, salt + 1u) + rnd(i, salt + 2u) + rnd(i, salt + 3u) - 2.0) * 1.7320508;
}

uint hubOf(uint i)
{
    float u = rnd(i, 31u);
    return min(uint(float(HUBS) * u * u * u), HUBS - 1u);
}

vec3 hubPos(uint h)
{
    return (vec3(rnd(h, 21u), rnd(h, 22u), rnd(h, 23u)) * 2.0 - 1.0) * 0.8;
}

uint gridSide()
{
    return pc.dim == 2u ? uint(ceil(sqrt(float(pc.n)))) : uint(ceil(pow(float(pc.n), 1.0 / 3.0)));
}

vec3 nodePos(uint i)
{
    vec3 p;
    if (pc.topo == 0u)
    {
        p = vec3(rnd(i, 1u), rnd(i, 2u), rnd(i, 3u)) * 2.0 - 1.0;
    }
    else if (pc.topo == 1u)
    {
        uint blob = i & 63u;
        vec3 centre = (vec3(rnd(blob, 11u), rnd(blob, 12u), rnd(blob, 13u)) * 2.0 - 1.0) * 0.75;
        p = centre + 0.07 * vec3(gauss(i, 1u), gauss(i, 5u), gauss(i, 9u));
    }
    else if (pc.topo == 2u)
    {
        p = i < HUBS ? hubPos(i) : hubPos(hubOf(i)) + 0.04 * vec3(gauss(i, 1u), gauss(i, 5u), gauss(i, 9u));
    }
    else if (pc.topo == 3u)
    {
        uint side = gridSide();
        float inv = 2.0 / float(max(side, 2u) - 1u);
        uint x = i % side;
        uint y = (i / side) % side;
        uint z = pc.dim == 2u ? 0u : i / (side * side);
        p = vec3(float(x), float(y), float(z)) * inv - 1.0;
    }
    else
    {
        float angle = 6.2831853 * float(i) / float(pc.n);
        float wobble = 13.0 * angle;
        p = pc.dim == 2u
            ? 0.85 * vec3(cos(angle), sin(angle), 0.0)
            : vec3((0.7 + 0.2 * cos(wobble)) * cos(angle), (0.7 + 0.2 * cos(wobble)) * sin(angle), 0.3 * sin(wobble));
    }

    if (pc.dim == 2u)
    {
        p.z = 0.0;
    }

    return p;
}

// The other end of the edge that starts at node i (one outgoing edge per node).
uint partner(uint i)
{
    if (pc.topo == 0u)
    {
        return hashu(i ^ 0xABCDu) % pc.n;
    }
    if (pc.topo == 1u)
    {
        uint perBlob = max(pc.n >> 6, 1u);
        return min((hashu(i + 77u) % perBlob) * 64u + (i & 63u), pc.n - 1u);
    }
    if (pc.topo == 2u)
    {
        return i < HUBS ? (i + 1u) % HUBS : hubOf(i);
    }
    if (pc.topo == 3u)
    {
        uint side = gridSide();
        uint step = (hashu(i) & 1u) == 0u ? 1u : side;
        return min(i + step, pc.n - 1u);
    }
    return (i + 1u) % pc.n;
}

vec3 nodeColour(uint i)
{
    float h = rnd(i & 63u, 99u);
    return vec3(0.4 + 0.6 * h, 0.4 + 0.6 * fract(h * 7.0), 0.4 + 0.6 * fract(h * 13.0));
}
