#version 450
#extension GL_GOOGLE_include_directive : require
// Draws the decimation survivors of the combined frame: instance j is sampled point survivors[8 + j].
#include "topology.glsl"
layout(set = 0, binding = 8) readonly buffer Survivors { uint survivors[]; };
layout(location = 0) out vec4 vColor;
void main()
{
    uint i = min(survivors[8u + uint(gl_InstanceIndex)] * pc.stride, pc.n - 1u);
    gl_Position = pc.viewProj * vec4(nodePos(i), 1.0);
    gl_PointSize = pc.pointSize;
    vColor = vec4(nodeColour(i), 1.0);
}
