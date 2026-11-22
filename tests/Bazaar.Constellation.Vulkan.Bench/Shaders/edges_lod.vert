#version 450
#extension GL_GOOGLE_include_directive : require
// Edge pass with a screen-length level of detail. Both endpoints are projected; an edge whose projected length is
// below min pixels (pc.a, float bits) or above max pixels (pc.b, float bits), or that crosses behind the camera,
// is collapsed to a point outside the clip volume so it costs no rasterisation.
#include "topology.glsl"
layout(location = 0) out vec4 vColor;
void main()
{
    uint e = uint(gl_VertexIndex) >> 1;
    uint i = min(e * pc.stride, pc.n - 1u);
    vec4 a = pc.viewProj * vec4(nodePos(i), 1.0);
    vec4 b = pc.viewProj * vec4(nodePos(partner(i)), 1.0);
    bool visible = a.w > 0.0 && b.w > 0.0;
    float len = visible ? length((a.xy / a.w - b.xy / b.w) * 0.5 * vec2(1280.0, 720.0)) : 0.0;
    bool keep = visible && len >= uintBitsToFloat(pc.a) && len <= uintBitsToFloat(pc.b);
    gl_Position = keep ? (((uint(gl_VertexIndex) & 1u) == 0u) ? a : b) : vec4(2.0, 2.0, 2.0, 1.0);
    vColor = vec4(nodeColour(i), 0.25);
}
