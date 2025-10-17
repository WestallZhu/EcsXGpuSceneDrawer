
using System.Runtime.CompilerServices;
using static Unity.Mathematics.math;
namespace Unity.Mathematics
{
    public static class mathEx
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public float3x4 TRS(float3 position, quaternion rotation, float scale)
        {
            float3x3 r = float3x3(rotation);
            return float3x4(r.c0 * scale,
                r.c1 * scale,
                r.c2 * scale, position);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 transform(float3x4 a, float3 b)
        {
            return (a.c0 * b.x + a.c1 * b.y + a.c2 * b.z + a.c3).xyz;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float determinant(float3x4 m)
        {
            float4 c0 = new float4(m.c0, 0);
            float4 c1 = new float4(m.c1, 0);
            float4 c2 = new float4(m.c2, 0);
            float4 c3 = new float4(m.c3, 1);

            float m00 = c1.y * (c2.z * c3.w - c2.w * c3.z) - c2.y * (c1.z * c3.w - c1.w * c3.z) + c3.y * (c1.z * c2.w - c1.w * c2.z);
            float m01 = c0.y * (c2.z * c3.w - c2.w * c3.z) - c2.y * (c0.z * c3.w - c0.w * c3.z) + c3.y * (c0.z * c2.w - c0.w * c2.z);
            float m02 = c0.y * (c1.z * c3.w - c1.w * c3.z) - c1.y * (c0.z * c3.w - c0.w * c3.z) + c3.y * (c0.z * c1.w - c0.w * c1.z);
            float m03 = c0.y * (c1.z * c2.w - c1.w * c2.z) - c1.y * (c0.z * c2.w - c0.w * c2.z) + c2.y * (c0.z * c1.w - c0.w * c1.z);

            return c0.x * m00 - c1.x * m01 + c2.x * m02 - c3.x * m03;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3x4 mul(float3x4 a, float3x4 b)
        {
            return float3x4(
                a.c0 * b.c0.x + a.c1 * b.c0.y + a.c2 * b.c0.z + a.c3 * 0,
                a.c0 * b.c1.x + a.c1 * b.c1.y + a.c2 * b.c1.z + a.c3 * 0,
                a.c0 * b.c2.x + a.c1 * b.c2.y + a.c2 * b.c2.z + a.c3 * 0,
                a.c0 * b.c3.x + a.c1 * b.c3.y + a.c2 * b.c3.z + a.c3 * 1);
        }


    }
}

