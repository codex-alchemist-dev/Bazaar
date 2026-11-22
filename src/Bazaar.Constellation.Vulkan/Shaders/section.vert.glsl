#version 450
// Instanced quad for one Constellation section rectangle. Per-vertex: a unit-square corner in -1..1.
// Per-instance: min x, min y, max x, max y (world space), then colour r, g, b, a (SectionPacking layout).
layout(location = 0) in vec2 inCorner;
layout(location = 1) in vec4 inBounds;
layout(location = 2) in vec4 inColour;

layout(location = 0) out vec4 outColour;

// Maps world space to clip space: clip = (world - offset) * scale. Same push block as the node pipeline.
layout(push_constant) uniform View {
    vec2 offset;
    vec2 scale;
} view;

void main() {
    vec2 t = inCorner * 0.5 + 0.5;
    vec2 world = mix(inBounds.xy, inBounds.zw, t);
    gl_Position = vec4((world - view.offset) * view.scale, 0.0, 1.0);
    outColour = inColour;
}
