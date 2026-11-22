#version 450
// Tile decimation, pass 2 (doc constellation-scale-and-lod, section 10.11). Workgroup g handles indirect command g written by the tile cull pass.
// The winner of each cell appends its instance index to the survivor list (survivors[1] is the instance count of the indirect draw).
// Compile: glslc --target-env=vulkan1.3 -O -fshader-stage=comp tiledecimate_select.comp.glsl -o tiledecimate_select.comp.spv
layout(local_size_x = 256) in;
layout(push_constant) uniform Push { mat4 viewProj; uint cellsX; uint cellsY; uint pad0; uint pad1; } pc;
layout(set = 0, binding = 0) readonly buffer Cmds { uint cmds[]; };          // tile cull buffer: word 0 draw count, commands from word 4
layout(set = 0, binding = 1) readonly buffer Positions { vec4 positions[]; }; // xyz per instance, w unused
layout(set = 0, binding = 2) buffer Cells { uint cells[]; };
layout(set = 0, binding = 3) buffer Survivors { uint survivors[]; };         // 0..3 VkDrawIndirectCommand, 4 candidate counter, survivors from word 8

void main()
{
    uint g = gl_WorkGroupID.x;
    if (g >= cmds[0]) { return; }
    uint count = cmds[4u + g * 4u + 1u];
    uint first = cmds[4u + g * 4u + 3u];
    for (uint j = gl_LocalInvocationID.x; j < count; j += 256u)
    {
        uint idx = first + j;
        vec4 clip = pc.viewProj * vec4(positions[idx].xyz, 1.0);
        if (clip.w <= 0.0) { continue; }
        vec2 ndc = clip.xy / clip.w;
        if (abs(ndc.x) >= 1.0 || abs(ndc.y) >= 1.0) { continue; }
        uint x = min(uint((ndc.x * 0.5 + 0.5) * float(pc.cellsX)), pc.cellsX - 1u);
        uint y = min(uint((ndc.y * 0.5 + 0.5) * float(pc.cellsY)), pc.cellsY - 1u);
        if (cells[y * pc.cellsX + x] == idx + 1u)
        {
            uint slot = atomicAdd(survivors[1], 1u);
            survivors[8u + slot] = idx;
        }
    }
}
