#version 450
#extension GL_GOOGLE_include_directive : require
#include "topology.glsl"
layout(location = 0) out vec4 vColor;
void main()
{
    uint i = min(uint(gl_InstanceIndex) * pc.stride, pc.n - 1u);
    gl_Position = pc.viewProj * vec4(nodePos(i), 1.0);
    gl_PointSize = pc.pointSize;
    vColor = vec4(nodeColour(i), 1.0);
}
