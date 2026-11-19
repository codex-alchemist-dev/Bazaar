#version 450
// Filled disc: pixels outside the unit circle are discarded.
layout(location = 0) in vec2 inCorner;
layout(location = 1) in vec4 inColor;
layout(location = 0) out vec4 outColor;

void main() {
    if (dot(inCorner, inCorner) > 1.0) {
        discard;
    }
    outColor = inColor;
}
