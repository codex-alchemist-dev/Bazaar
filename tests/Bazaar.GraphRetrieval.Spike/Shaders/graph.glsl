// S28 graph kernels: CSR graph, level-synchronous breadth-first search with a visited bitset (doc 64 section 7.2, kernel 3).
layout(set = 0, binding = 0) readonly buffer Offsets { uint offsets[]; };
layout(set = 0, binding = 1) readonly buffer Cols { uint cols[]; };
layout(set = 0, binding = 2) readonly buffer Kinds { uint kinds[]; };    // four edge kinds per uint
layout(set = 0, binding = 3) buffer Visited { uint visited[]; };
layout(set = 0, binding = 4) buffer Reached { uint reached[]; };         // concatenated levels; seeds first
layout(set = 0, binding = 5) buffer State { uint state[]; };             // 0 level start, 1 level end, 2 counter, 3..5 dispatch args, 6 overflow, 8.. level ends
layout(set = 0, binding = 6) readonly buffer Seeds { uint seeds[]; };
layout(set = 0, binding = 7) buffer Check { uint check[]; };             // per level: sum, xor of node ids
layout(push_constant) uniform P { uint kindMask; uint cap; uint level; uint seedCount; uint seedOffset; } p;
