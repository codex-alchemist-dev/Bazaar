#version 450
#extension GL_GOOGLE_include_directive : require
#include "topology.glsl"
layout(set = 0, binding = 1) readonly buffer Screen { uint screen[]; };
layout(location = 0) out vec4 outColor;
void main()
{
    uint x = min(uint(gl_FragCoord.x), pc.c - 1u);
    uint y = min(uint(gl_FragCoord.y), 719u);
    float count = float(screen[y * pc.c + x]);
    float v = log2(1.0 + count) / 14.0;
    outColor = vec4(v, 0.5 * v, 1.0 - v, 1.0);
}
