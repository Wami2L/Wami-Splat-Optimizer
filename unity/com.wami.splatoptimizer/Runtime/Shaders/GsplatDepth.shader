// Pre-pasada de profundidad para Gaussian Splatting (Wami Splat Optimizer).
// Se dibuja en la fase opaca (cola AlphaTest): solo escriben profundidad los núcleos de los splats casi opacos,
// empujada hacia atrás a lo largo del rayo (_DepthBiasRel relativo + _DepthBiasAbs metros). Después, el pase de color
// (ZTest LEqual, ZWrite Off) descarta por early-Z los fragmentos de lo que queda detrás: oclusión a nivel de píxel
// para el overdraw de interiores (bancos, pilares, el propio grosor de las superficies).
// Recorre los splats de delante hacia atrás (orden inverso al de mezcla) para aprovechar el early-Z también aquí.

Shader "Gsplat/Depth"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="AlphaTest"
        }

        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require compute
            #pragma multi_compile UNCOMPRESSED SPARK
            #define SH_BANDS_0

            #include "UnityCG.cginc"
            #include "Gsplat.hlsl"
            #ifdef UNCOMPRESSED
            #include "GsplatUncompressed.hlsl"
            #endif
            #ifdef SPARK
            #include "GsplatSpark.hlsl"
            #endif

            int _SplatCount;
            int _SplatInstanceSize;
            float4x4 _MATRIX_M;
            float _ScaleFactor;
            float _DepthAlpha;
            float _DepthBiasRel;
            float _DepthBiasAbs;
            StructuredBuffer<uint> _OrderBuffer;

            struct appdata
            {
                float4 vertex : POSITION;
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instanceID : SV_InstanceID;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            bool InitSource(appdata v, out SplatSource source)
            {
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                source.order = v.instanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #else
                source.order = unity_InstanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #endif

                if (source.order >= _SplatCount)
                    return false;

                source.id = _OrderBuffer[_SplatCount - 1 - source.order];   // de delante hacia atrás
                source.cornerUV = float2(v.vertex.x, v.vertex.y) * _ScaleFactor;
                return true;
            }

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float alpha : TEXCOORD1;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = discardVec;

                SplatSource source;
                if (!InitSource(v, source))
                    return o;

                SplatCenter center;
                SplatCorner corner;
                float4 color;
                if (!InitSplatData(source, mul(UNITY_MATRIX_V, _MATRIX_M), center, corner, color))
                    return o;
                if (!SplatKitAdjust(corner, color))
                    return o;
                if (color.a < _DepthAlpha)          // los splats poco opacos no ocultan nada
                    return o;

                ClipCorner(corner, color.w);

                // Empujar el centro hacia atrás a lo largo del rayo: misma posición en pantalla, más profundo.
                float d = -center.view.z;
                float k = (d * (1.0 + _DepthBiasRel) + _DepthBiasAbs) / max(d, 1e-4);
                float4 proj = mul(UNITY_MATRIX_P, float4(center.view * k, 1.0));
                float s = proj.w / center.proj.w;   // el tamaño en píxeles del quad no cambia
                o.vertex = proj + float4(corner.offset.x * s, _ProjectionParams.x * corner.offset.y * s, 0, 0);
                o.uv = corner.uv;
                o.alpha = color.a;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float A = dot(i.uv, i.uv);
                if (A > 1.0) discard;
                if (exp(-A * 4.0) * i.alpha < _DepthAlpha) discard;   // solo el núcleo casi opaco
                return 0;
            }
            ENDHLSL
        }
    }
}
