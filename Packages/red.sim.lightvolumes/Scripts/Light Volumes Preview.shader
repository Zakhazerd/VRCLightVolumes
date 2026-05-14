Shader "Hidden/LightVolumesPreview" {

    SubShader {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }

        Pass {
            ZWrite On
            ZTest LEqual
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX3D(_PreviewTexture0);
            UNITY_DECLARE_TEX3D(_PreviewTexture1);
            UNITY_DECLARE_TEX3D(_PreviewTexture2);

            float4x4 _PreviewLocalToWorld;
            float4 _PreviewResolution;
            int _PreviewVoxelCount;
            float _PreviewVoxelRadius;
            int _PreviewCardsPerInstance;
            int _PreviewInstancesPerDrawCall;
            int _PreviewDrawCallId;
            int _PreviewHasTextureData;
            float4 _PreviewColor;
            float4 _PreviewCorrection;
            float4 _PreviewRotation;
            int _PreviewIsRotated;
            float4 _PreviewSortAxis0;
            float4 _PreviewSortAxis1;
            float4 _PreviewSortAxis2;
            float4 _PreviewSortFlip;
            float4 _PreviewCameraRight;
            float4 _PreviewCameraUp;
            float4 _PreviewCameraPosition;

            struct Attributes {
                float4 posOS : POSITION;
                float2 cardData : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings {
                float4 posCS : SV_Position;
                float2 uv : TEXCOORD0;
                float3 centerWS : TEXCOORD1;
                float3 L0 : TEXCOORD2;
                float3 L1r : TEXCOORD3;
                float3 L1g : TEXCOORD4;
                float3 L1b : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Returns the current Unity instance id, or zero when instancing is disabled.
            uint GetInstanceID() {
                #if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
                    return unity_InstanceID;
                #else
                    return 0u;
                #endif
            }

            // Traverses one axis from high to low coordinate when requested.
            uint ApplySortFlip(uint coord, uint dim, float flip) {
                return flip > 0.5 ? dim - 1u - coord : coord;
            }

            // Converts a front-to-back card id into a volume voxel coordinate.
            float3 DecodeVoxelCoord(uint cardId) {
                uint dim0 = (uint)max(_PreviewSortAxis0.w, 1.0);
                uint dim1 = (uint)max(_PreviewSortAxis1.w, 1.0);
                uint dim2 = (uint)max(_PreviewSortAxis2.w, 1.0);

                uint coord0 = cardId % dim0;
                uint rest = cardId / dim0;
                uint coord1 = rest % dim1;
                uint coord2 = min(rest / dim1, dim2 - 1u);

                coord0 = ApplySortFlip(coord0, dim0, _PreviewSortFlip.x);
                coord1 = ApplySortFlip(coord1, dim1, _PreviewSortFlip.y);
                coord2 = ApplySortFlip(coord2, dim2, _PreviewSortFlip.z);

                return _PreviewSortAxis0.xyz * (float)coord0 + _PreviewSortAxis1.xyz * (float)coord1 + _PreviewSortAxis2.xyz * (float)coord2;
            }

            // Rotates a vector by a quaternion.
            float3 MultiplyVectorByQuaternion(float3 v, float4 q) {
                float3 t = 2.0 * cross(q.xyz, v);
                return v + q.w * t + cross(q.xyz, t);
            }

            // Evaluates first-order spherical harmonics.
            float EvaluateSH(float L0, float3 L1, float3 normalWS) {
                return L0 + dot(L1, normalWS);
            }

            // Applies the same first-order SH dering used by atlas generation.
            float3 DeringSingleSH(float L0, float3 L1) {
                L1 *= 0.5;
                float L1Length = length(L1);
                if (L1Length > 1e-6 && L0 > 0.0) L1 *= min(L0 / L1Length, 1.13);
                return L1;
            }

            // Applies the same vector color correction used by atlas generation.
            float3 CorrectVector(float3 value) {
                float valueLength = length(value);
                if (valueLength <= 1e-6) return float3(0.0, 0.0, 0.0);

                float range = max(_PreviewCorrection.y - _PreviewCorrection.x, 1e-6);
                float correctedLength = max(((valueLength - _PreviewCorrection.x) / range) * _PreviewCorrection.z, 0.0);
                return value * (correctedLength / valueLength);
            }

            // Samples the three Light Volume textures at voxel center UVW.
            void SamplePreviewSH(float3 uvw, out float3 L0, out float3 L1r, out float3 L1g, out float3 L1b) {
                if (_PreviewHasTextureData == 0) {
                    L0 = float3(1.0, 1.0, 1.0);
                    L1r = float3(0.0, 0.0, 0.0);
                    L1g = float3(0.0, 0.0, 0.0);
                    L1b = float3(0.0, 0.0, 0.0);
                    return;
                }

                float4 tex0 = UNITY_SAMPLE_TEX3D_LOD(_PreviewTexture0, uvw, 0);
                float4 tex1 = UNITY_SAMPLE_TEX3D_LOD(_PreviewTexture1, uvw, 0);
                float4 tex2 = UNITY_SAMPLE_TEX3D_LOD(_PreviewTexture2, uvw, 0);

                float3 l0 = tex0.rgb;
                float3 l1r = float3(tex1.r, tex2.r, tex0.a);
                float3 l1g = float3(tex1.g, tex2.g, tex1.a);
                float3 l1b = float3(tex1.b, tex2.b, tex2.a);

                l1r = DeringSingleSH(l0.r, l1r);
                l1g = DeringSingleSH(l0.g, l1g);
                l1b = DeringSingleSH(l0.b, l1b);

                l0 = CorrectVector(l0);
                l1r = CorrectVector(l1r);
                l1g = CorrectVector(l1g);
                l1b = CorrectVector(l1b);

                L0 = l0 * _PreviewColor.rgb;
                L1r = l1r * _PreviewColor.r;
                L1g = l1g * _PreviewColor.g;
                L1b = l1b * _PreviewColor.b;

                if (_PreviewIsRotated != 0) {
                    L1r = MultiplyVectorByQuaternion(L1r, _PreviewRotation);
                    L1g = MultiplyVectorByQuaternion(L1g, _PreviewRotation);
                    L1b = MultiplyVectorByQuaternion(L1b, _PreviewRotation);
                }
            }

            Varyings vert(Attributes v) {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(Varyings, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                uint meshCardId = (uint)floor(v.cardData.x + 0.5);
                uint drawInstanceId = (uint)max(_PreviewDrawCallId, 0) * (uint)max(_PreviewInstancesPerDrawCall, 1) + GetInstanceID();
                uint cardId = drawInstanceId * (uint)max(_PreviewCardsPerInstance, 1) + meshCardId;
                uint voxelCount = (uint)max(_PreviewVoxelCount, 0);

                if (cardId >= voxelCount) {
                    o.posCS = float4(0.0, 0.0, 0.0, -1.0);
                    return o;
                }

                float3 voxelCoord = DecodeVoxelCoord(cardId);
                float3 resolution = max(_PreviewResolution.xyz, float3(1.0, 1.0, 1.0));
                float3 uvw = (voxelCoord + 0.5) / resolution;
                float3 localPos = uvw - 0.5;
                float3 centerWS = mul(_PreviewLocalToWorld, float4(localPos, 1.0)).xyz;

                float2 disc = v.posOS.xy;
                float2 uv = disc * 0.5 + 0.5;
                float3 cameraRightWS = normalize(_PreviewCameraRight.xyz);
                float3 cameraUpWS = normalize(_PreviewCameraUp.xyz);
                float3 posWS = centerWS + (cameraRightWS * disc.x + cameraUpWS * disc.y) * _PreviewVoxelRadius;

                o.posCS = UnityObjectToClipPos(float4(posWS, 1.0));
                o.uv = uv;
                o.centerWS = centerWS;
                SamplePreviewSH(uvw, o.L0, o.L1r, o.L1g, o.L1b);
                return o;
            }

            half4 frag(Varyings i) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 disc = i.uv * 2.0 - 1.0;
                float radiusSq = dot(disc, disc);
                clip(1.0 - radiusSq);

                half sphereZ = (half)sqrt(saturate(1.0 - radiusSq));
                half3 cameraRightWS = (half3)normalize(_PreviewCameraRight.xyz);
                half3 cameraUpWS = (half3)normalize(_PreviewCameraUp.xyz);
                half3 viewDirWS = (half3)normalize(_PreviewCameraPosition.xyz - i.centerWS);
                half3 normalWS = normalize(cameraRightWS * (half)disc.x + cameraUpWS * (half)disc.y + viewDirWS * sphereZ);

                // Unbaked volumes are shown as neutral shaded cards.
                if (_PreviewHasTextureData == 0) {
                    half shade = 0.28 + 0.72 * sphereZ;
                    return half4((half3)i.L0 * shade, 1.0);
                }

                // Baked volumes show evaluated SH only, without editor ambient or fill light.
                float3 normalWSFloat = (float3)normalWS;
                float3 color = float3(EvaluateSH(i.L0.r, i.L1r, normalWSFloat), EvaluateSH(i.L0.g, i.L1g, normalWSFloat), EvaluateSH(i.L0.b, i.L1b, normalWSFloat));
                return half4((half3)max(color, float3(0.0, 0.0, 0.0)), 1.0);
            }
            ENDCG
        }
    }
}
