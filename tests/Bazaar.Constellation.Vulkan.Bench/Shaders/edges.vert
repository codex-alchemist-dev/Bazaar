#version 450
#extension GL_GOOGLE_include_directive : require
#include "topology.glsl"
layout(location = 0) out vec4 vColor;
void main()
{
    uint e = uint(gl_VertexIndex) >> 1;
    uint i = min(e * pc.stride, pc.n - 1u);
    uint node = (uint(gl_VertexIndex) & 1u) == 0u ? i : partner(i);
    gl_Position = pc.viewProj * vec4(nodePos(node), 1.0);
    vColor = vec4(nodeColour(i), 0.25);
}
