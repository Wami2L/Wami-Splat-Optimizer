// SplatKit — sort keys for the radix sort (KEY_FLOAT, ascending = drawn first = farthest first).
// SPDX-License-Identifier: MIT
//
// _SortKeyParams: x = mode (0 = view axis, as the original gsplat; 1 = radial distance: rotating the head does not
//                 change the order, so VR only re-sorts when the head moves)
//                 y = 1 for 16-bit keys (2 radix passes instead of 4), z = min distance, w = 1 / (max - min).
// 16-bit keys are written as a normal positive float (exponent of 2.0) whose low 16 bits hold the quantized key:
// the sort's FloatToUint keeps those bits, so the first two 8-bit passes sort exactly and the other two are skipped.

#ifndef SPLATKIT_SORT_KEYS
#define SPLATKIT_SORT_KEYS

float4 _SortKeyParams;

float SortKey(float3 viewPos)
{
    float d = _SortKeyParams.x > 0.5 ? length(viewPos) : -viewPos.z;
    if (_SortKeyParams.y < 0.5)
        return -d;
    uint q = (uint)(saturate((d - _SortKeyParams.z) * _SortKeyParams.w) * 65535.0 + 0.5);
    return asfloat(0x40000000u | (65535u - q));
}

#endif
