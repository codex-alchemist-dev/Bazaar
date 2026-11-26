#version 450
// Tile cull (doc constellation-scale-and-lod, section 10.11). One thread per tile: frustum test of the tile's bounding box,
// then one VkDrawIndirectCommand {vertexCount, tile instance count, 0, first instance} appended for each surviving tile.
// Set 0 binding 0: tiles, two vec4 each (min.xyz + firstInstance bits, max.xyz + instanceCount bits).
// Set 0 binding 1: word 0 draw count, 1 frustum-visible tiles, 2 instances to draw, 3 non-empty tiles; commands from byte 16.
// Compile: glslc --target-env=vulkan1.3 -O tilecull.comp.glsl -fshader-stage=comp -o tilecull.comp.spv
layout(local_size_x = 256) in;
layout(push_constant) uniform Push
{
    mat4 viewProj;      // System.Numerics matrix bytes as pushed by the host (row vector convention)
    uint tileCount;
    uint vertexCount;   // vertices per draw: 1 for points, 6 for quads
    uint pad0;
    uint pad1;
} pc;
layout(set = 0, binding = 0) readonly buffer Tiles { vec4 tiles[]; };
layout(set = 0, binding = 1) buffer Cmds { uint cmds[]; };

void main()
{
    uint t = gl_GlobalInvocationID.x;
    if (t >= pc.tileCount) { return; }
    vec4 lo = tiles[t * 2u];
    vec4 hi = tiles[t * 2u + 1u];
    uint count = floatBitsToUint(hi.w);
    if (count == 0u) { return; }
    atomicAdd(cmds[3], 1u);
    uint allOutside = 63u;
    for (uint c = 0u; c < 8u; c++)
    {
        vec3 corner = vec3((c & 1u) != 0u ? hi.x : lo.x, (c & 2u) != 0u ? hi.y : lo.y, (c & 4u) != 0u ? hi.z : lo.z);
        vec4 q = pc.viewProj * vec4(corner, 1.0);
        allOutside &= (q.x < -q.w ? 1u : 0u) | (q.x > q.w ? 2u : 0u) | (q.y < -q.w ? 4u : 0u) | (q.y > q.w ? 8u : 0u) | (q.z < 0.0 ? 16u : 0u) | (q.z > q.w ? 32u : 0u);
    }
    if (allOutside != 0u) { return; }
    atomicAdd(cmds[1], 1u);
    uint slot = atomicAdd(cmds[0], 1u);
    atomicAdd(cmds[2], count);
    cmds[4u + slot * 4u + 0u] = pc.vertexCount;
    cmds[4u + slot * 4u + 1u] = count;
    cmds[4u + slot * 4u + 2u] = 0u;
    cmds[4u + slot * 4u + 3u] = floatBitsToUint(lo.w);
}
