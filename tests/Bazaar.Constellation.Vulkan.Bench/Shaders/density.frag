#version 450

// Shades each screen pixel by the log of its cell's node count.
layout(set = 0, binding = 1) readonly buffer Density
{
    uint cells[];
};

layout(push_constant) uniform Push
{
    uint count;
    uint cellsX;
    uint cellsY;
    uint pad;
    vec4 transform;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main()
{
    uint cx = min(uint(vUv.x * float(pc.cellsX)), pc.cellsX - 1u);
    uint cy = min(uint(vUv.y * float(pc.cellsY)), pc.cellsY - 1u);
    float count = float(cells[cy * pc.cellsX + cx]);
    float v = log(1.0 + count) / 10.0;
    outColor = vec4(v, 0.5 * v, 1.0 - v, 1.0);
}
