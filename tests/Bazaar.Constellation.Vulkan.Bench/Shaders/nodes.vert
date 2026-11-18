#version 450

// One instance per node. nodes[i] = (x, y, cluster, state), positions in [-1, 1].
layout(set = 0, binding = 0) readonly buffer Nodes
{
    vec4 nodes[];
};

layout(push_constant) uniform Push
{
    vec4 transform; // xy scale, zw offset
    vec4 quad;      // x half-size in clip space
} pc;

layout(location = 0) out vec3 vColor;

const vec2 corners[6] = vec2[](
    vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0),
    vec2(-1.0, 1.0), vec2(1.0, -1.0), vec2(1.0, 1.0));

void main()
{
    vec4 n = nodes[gl_InstanceIndex];
    vec2 p = n.xy * pc.transform.xy + pc.transform.zw;
    gl_Position = vec4(p + corners[gl_VertexIndex] * pc.quad.x, 0.0, 1.0);
    float h = fract(sin(n.z * 12.9898) * 43758.5453);
    vColor = vec3(h, fract(h * 7.0), fract(h * 13.0)) * 0.6 + 0.4;
}
