#version 450
// Instanced quad for one Constellation node. Per-vertex: a unit-square corner.
// Per-instance: x, y, radius, r, g, b, a (layout of NodeInstanceBuffer; z is not stored).
layout(location = 0) in vec2 inCorner;
layout(location = 1) in vec2 inCenter;
layout(location = 2) in float inRadius;
layout(location = 3) in vec4 inColor;

layout(location = 0) out vec2 outCorner;
layout(location = 1) out vec4 outColor;

// Maps world space to clip space: clip = (world - offset) * scale.
layout(push_constant) uniform View {
    vec2 offset;
    vec2 scale;
} view;

void main() {
    vec2 world = inCenter + inCorner * inRadius;
    gl_Position = vec4((world - view.offset) * view.scale, 0.0, 1.0);
    outCorner = inCorner;
    outColor = inColor;
}
