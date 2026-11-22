#version 450
// A node is a disc, or a ring when its style sets a ring width. The outer edge and the ring's inner edge are
// anti-aliased: coverage ramps over one pixel (fwidth of the distance) and is multiplied into the alpha, which the
// pipeline blends with SrcAlpha / OneMinusSrcAlpha. Pixels fully outside the unit circle are discarded.
layout(location = 0) in vec2 inCorner;
layout(location = 1) in flat uint inStyle;
layout(location = 0) out vec4 outColour;

struct NodeStyleGpu {
    vec4 colour;
    float radius;
    float ringWidth;
    vec2 padding;
};

layout(std430, set = 0, binding = 0) readonly buffer NodeStyles {
    NodeStyleGpu styles[];
};

void main() {
    NodeStyleGpu style = styles[inStyle];
    float distance = length(inCorner);
    float edge = max(fwidth(distance), 1e-4);
    float coverage = 1.0 - smoothstep(1.0 - edge, 1.0 + edge, distance);
    if (style.ringWidth > 0.0) {
        float inner = 1.0 - style.ringWidth;
        coverage *= smoothstep(inner - edge, inner + edge, distance);
    }
    if (coverage <= 0.0) {
        discard;
    }
    outColour = vec4(style.colour.rgb, style.colour.a * coverage);
}
