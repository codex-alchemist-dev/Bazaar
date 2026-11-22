#version 450
// A section is a flat, alpha-blended filled rectangle; the blend state does the transparency.
layout(location = 0) in vec4 inColour;
layout(location = 0) out vec4 outColour;

void main() {
    outColour = inColour;
}
