// S28 threshold selection shared declarations. Keys: monotonic uint of the float score; two histogram passes of 12 bits each find the
// key of the (kPrime)-th largest score per query without sorting.
layout(push_constant) uniform P { uint rows; uint rowsStride; uint kPrime; uint candBase; } p;
layout(set = 0, binding = 0) readonly buffer Scores { float scores[]; };
layout(set = 0, binding = 1) buffer Hist1 { uint hist1[]; };
layout(set = 0, binding = 2) buffer Hist2 { uint hist2[]; };
layout(set = 0, binding = 3) buffer Params { uint params[]; };   // per query: b1, above1, threshold key, flag (1 = fewer rows than kPrime)
layout(set = 0, binding = 4) buffer Cand { uint cand[]; };       // per query: count, then (row, score bits) pairs
const uint CAP = 256u;
const uint CAND_STRIDE = 1u + 2u * CAP;
uint keyOf(float f)
{
    uint u = floatBitsToUint(f);
    return (u & 0x80000000u) != 0u ? ~u : (u | 0x80000000u);
}
