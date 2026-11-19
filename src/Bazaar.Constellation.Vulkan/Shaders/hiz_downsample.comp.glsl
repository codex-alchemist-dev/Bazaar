#version 450
// Hi-Z downsample for reversed-Z depth (1 near, 0 far): one mip of the pyramid from the mip above it.
// Each output texel is the MIN (the farthest surface) over the source texels it covers, so an occlusion test against
// it is conservative: a tile is culled only when it lies behind everything in the footprint.
// Source: the depth attachment (first dispatch) or the previous pyramid mip. Destination: the next pyramid mip (r32f).
// Odd source sizes: the last output column or row also covers the one extra source texel, so no texel is dropped.
// Footprint is always 3x3 with the third column or row repeating the second unless the size is odd and the texel is last.
layout(local_size_x = 8, local_size_y = 8) in;

layout(set = 0, binding = 0) uniform texture2D uSrc;
layout(set = 0, binding = 1, r32f) uniform writeonly image2D uDst;

layout(push_constant) uniform Push {
    ivec2 srcSize;
    ivec2 dstSize;
} pc;

void main() {
    ivec2 d = ivec2(gl_GlobalInvocationID.xy);
    if (d.x >= pc.dstSize.x || d.y >= pc.dstSize.y) {
        return;
    }

    ivec2 s = d * 2;
    ivec2 maxCoord = pc.srcSize - 1;
    bool extraX = (pc.srcSize.x & 1) == 1 && d.x == pc.dstSize.x - 1;
    bool extraY = (pc.srcSize.y & 1) == 1 && d.y == pc.dstSize.y - 1;
    int cols[3] = int[3](s.x, s.x + 1, extraX ? s.x + 2 : s.x + 1);
    int rows[3] = int[3](s.y, s.y + 1, extraY ? s.y + 2 : s.y + 1);

    float m = texelFetch(uSrc, ivec2(min(cols[0], maxCoord.x), min(rows[0], maxCoord.y)), 0).r;
    for (int y = 0; y < 3; y++) {
        for (int x = 0; x < 3; x++) {
            m = min(m, texelFetch(uSrc, ivec2(min(cols[x], maxCoord.x), min(rows[y], maxCoord.y)), 0).r);
        }
    }

    imageStore(uDst, d, vec4(m, 0.0, 0.0, 0.0));
}
