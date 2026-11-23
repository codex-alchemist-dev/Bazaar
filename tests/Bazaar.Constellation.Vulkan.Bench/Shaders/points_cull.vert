#version 450
#extension GL_GOOGLE_include_directive : require
// Draws the points of the tiles the cull pass kept. gl_InstanceIndex = firstInstance + instance = slot in the sorted list.
#include "topology.glsl"
layout(set = 0, binding = 4) readonly buffer Sorted { uint sorted[]; };
layout(location = 0) out vec4 vColor;
void main()
{
    uint i = min(sorted[gl_InstanceIndex] * pc.stride, pc.n - 1u);
    gl_Position = pc.viewProj * vec4(nodePos(i), 1.0);
    gl_PointSize = pc.pointSize;
    vColor = vec4(nodeColour(i), 1.0);
}
