// GPU-driven tile culling path (doc constellation-scale-and-lod, section 10.10). Set 0 of the cull pipeline layout.
// The sampled points are binned once into 4096 tiles (2D 64x64, 3D 16x16x16 over [-1,1]); each frame a compute pass tests every
// tile and writes one indirect draw command per visible tile.
const uint TILES = 4096u;
layout(set = 0, binding = 0) buffer TileCount { uint tileCount[]; };
layout(set = 0, binding = 1) buffer TileOffset { uint tileOffset[]; };
layout(set = 0, binding = 2) buffer TileCursor { uint tileCursor[]; };
layout(set = 0, binding = 3) buffer TileBounds { uint tileBounds[]; };   // per tile: min xyz then max xyz as ordered-uint float keys
layout(set = 0, binding = 4) buffer Sorted { uint sorted[]; };           // sample indices grouped by tile
layout(set = 0, binding = 5) buffer Cmds { uint cmds[]; };               // word 0 draw count, 1 frustum-visible tiles, 2 instances, 3 non-empty tiles; commands from byte 16
layout(set = 0, binding = 6) buffer HiZ { float hiz[]; };                // depth pyramid, level 0 is 640x360, min of 2x2 per level
layout(set = 0, binding = 7) buffer Cells { uint cells[]; };                // decimation grid, 640x360 (one cell per 2x2 pixels)
layout(set = 0, binding = 8) buffer Survivors { uint survivors[]; };      // words 0..3 VkDrawIndirectCommand, 4 candidate counter, survivors from word 8

uint tileOf(vec3 p)
{
    if (pc.dim == 2u)
    {
        uvec2 g = uvec2(clamp((p.xy * 0.5 + 0.5) * 64.0, 0.0, 63.0));
        return g.y * 64u + g.x;
    }
    uvec3 g = uvec3(clamp((p * 0.5 + 0.5) * 16.0, 0.0, 15.0));
    return g.z * 256u + g.y * 16u + g.x;
}

uint fkey(float f)
{
    uint u = floatBitsToUint(f);
    return (u & 0x80000000u) != 0u ? ~u : (u | 0x80000000u);
}

float ukey(uint k)
{
    uint u = (k & 0x80000000u) != 0u ? (k & 0x7fffffffu) : ~k;
    return uintBitsToFloat(u);
}

void hizLevel(uint l, out uint off, out uint w, out uint h)
{
    off = 0u; w = 640u; h = 360u;
    for (uint i = 0u; i < l; i++) { off += w * h; w = (w + 1u) / 2u; h = (h + 1u) / 2u; }
}
