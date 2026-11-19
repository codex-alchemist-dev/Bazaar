#version 450
// A node is a disc, or a ring when its style sets a ring width. Pixels outside the unit circle are discarded.
// For a ring, pixels inside the inner radius (1 - ringWidth) are discarded too.
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
    if (distance > 1.0) {
        discard;
    }
    if (style.ringWidth > 0.0 && distance < 1.0 - style.ringWidth) {
        discard;
    }
    outColour = style.colour;
}
