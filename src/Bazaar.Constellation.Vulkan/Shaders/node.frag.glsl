#version 450
// A node is a disc, or a ring when its style sets a ring width. Pixels outside the unit circle are discarded.
// For a ring, pixels inside the inner radius (1 - ringWidth) are discarded too.
// When the instance carries an icon rectangle, the node is the glyph instead: the atlas holds a signed distance field
// (128/255 is the edge, above is inside), and a smoothstep around the edge gives antialiased coverage at any scale.
layout(location = 0) in vec2 inCorner;
layout(location = 1) in flat uint inStyle;
layout(location = 2) in flat vec4 inIcon;
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

layout(set = 0, binding = 1) uniform sampler2D iconAtlas;

void main() {
    NodeStyleGpu style = styles[inStyle];
    // Sampled before the branch so the derivative is taken in uniform control flow; a no-icon node samples texel 0 and ignores it.
    vec2 uv = mix(inIcon.xy, inIcon.zw, inCorner * 0.5 + 0.5);
    float field = texture(iconAtlas, uv).r;
    float width = max(fwidth(field), 1.0 / 255.0);
    if (inIcon.z > inIcon.x && inIcon.w > inIcon.y) {
        float edge = 128.0 / 255.0;
        float coverage = smoothstep(edge - width, edge + width, field);
        if (coverage <= 0.0) {
            discard;
        }
        outColour = vec4(style.colour.rgb, style.colour.a * coverage);
        return;
    }
    float distance = length(inCorner);
    if (distance > 1.0) {
        discard;
    }
    if (style.ringWidth > 0.0 && distance < 1.0 - style.ringWidth) {
        discard;
    }
    outColour = style.colour;
}
