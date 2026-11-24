#version 450
// Instanced quad for one Constellation node. Per-vertex: a unit-square corner.
// Per-instance: x, y, style index (NodeInstanceBuffer layout; z is not stored), and the icon rectangle (NodeIconBuffer).
// The style index selects an entry of the style table in set 0, binding 0.
layout(location = 0) in vec2 inCorner;
layout(location = 1) in vec2 inCenter;
layout(location = 2) in float inStyle;
// Atlas rectangle u0, v0, u1, v1 of the node's icon glyph; all zeros means no icon (plain dot or ring).
layout(location = 3) in vec4 inIcon;

layout(location = 0) out vec2 outCorner;
layout(location = 1) out flat uint outStyle;
layout(location = 2) out flat vec4 outIcon;

// One style as NodeStylePacking writes it: colour, radius, ring width, and two floats of std430 padding.
struct NodeStyleGpu {
    vec4 colour;
    float radius;
    float ringWidth;
    vec2 padding;
};

layout(std430, set = 0, binding = 0) readonly buffer NodeStyles {
    NodeStyleGpu styles[];
};

// Maps world space to clip space: clip = (world - offset) * scale.
layout(push_constant) uniform View {
    vec2 offset;
    vec2 scale;
} view;

void main() {
    uint index = uint(inStyle + 0.5);
    NodeStyleGpu style = styles[index];
    vec2 world = inCenter + inCorner * style.radius;
    gl_Position = vec4((world - view.offset) * view.scale, 0.0, 1.0);
    outCorner = inCorner;
    outStyle = index;
    outIcon = inIcon;
}
