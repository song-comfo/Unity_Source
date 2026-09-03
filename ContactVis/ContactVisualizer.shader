Shader "Custom/ContactVisualizer"
{
    // Contact/penetration visualizer for arbitrary PAIRS of meshes.
    // Each object's material receives the OTHER object's SDF and its OWN SDF
    // (set per-material by ContactPenetrationVisualizer.cs).
    // All inside/outside decisions come from flood-filled, surface-smoothed SDFs,
    // so broken imported normals / non-watertight meshes behave correctly.
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.75, 0.75, 0.78, 1)
        _ContactColor0 ("Contact 0% (Touch)", Color) = (0.15, 0.35, 1.0, 1)
        _ContactColor1 ("Contact 25%", Color) = (0.0, 0.85, 0.9, 1)
        _ContactColor2 ("Contact 50%", Color) = (0.1, 1.0, 0.2, 1)
        _ContactColor3 ("Contact 75%", Color) = (1.0, 0.95, 0.1, 1)
        _ContactColor4 ("Contact 100% (Deep)", Color) = (1.0, 0.05, 0.05, 1)
        _MaxPenetration ("Max Penetration (m)", Range(0.001, 0.5)) = 0.08
        _HeatGamma ("Heat Gamma (gradation spread)", Range(0.3, 1.0)) = 0.55
        [Toggle] _AdaptiveMax ("Adapt Scale To Local Thickness", Float) = 1
        _AdaptiveFloor ("Adaptive Floor (m) - thin shells never scale below this", Range(0.004, 0.1)) = 0.03
        [Toggle] _UseFacingGate ("Facing Gate (reject same-facing)", Float) = 1
        _ProbeDepth ("Burial Probe Depth (m)", Range(0.0, 0.3)) = 0.18
        [IntRange] _ProbeSteps ("Burial Probe Steps", Range(1, 48)) = 40
        _AttributionSlack ("Attribution Slack (x self voxel)", Range(0.5, 4.0)) = 1.0
        _FacingThreshold ("Facing Gate (max same-facing dot)", Range(0.0, 0.9)) = 0.25
        [HideInInspector] _DebugMode ("Debug Mode", Float) = 0
        [HideInInspector] _CV_OtherSDF ("Other SDF (set by script)", 3D) = "black" {}
        [HideInInspector] _CV_OtherBoxCenter ("Other Box Center", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CV_OtherBoxSize ("Other Box Size", Vector) = (1, 1, 1, 0)
        [HideInInspector] _CV_OtherDistScale ("Other Dist Scale", Float) = 1
        [HideInInspector] _CV_OtherValid ("Other Valid", Float) = 0
        [HideInInspector] _CV_OtherTol ("Other Voxel Size (m)", Float) = 0.01
        [HideInInspector] _CV_SelfSDF ("Self SDF (set by script)", 3D) = "black" {}
        [HideInInspector] _CV_SelfBoxCenter ("Self Box Center", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CV_SelfBoxSize ("Self Box Size", Vector) = (1, 1, 1, 0)
        [HideInInspector] _CV_SelfDistScale ("Self Dist Scale", Float) = 1
        [HideInInspector] _CV_SelfValid ("Self Valid", Float) = 0
        [HideInInspector] _CV_SelfTol ("Self Voxel Size (m)", Float) = 0.01
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ContactColor0;
                float4 _ContactColor1;
                float4 _ContactColor2;
                float4 _ContactColor3;
                float4 _ContactColor4;
                float _MaxPenetration;
                float _HeatGamma;
                float _AdaptiveMax;
                float _AdaptiveFloor;
                float _UseFacingGate;
                float _ProbeDepth;
                float _ProbeSteps;
                float _AttributionSlack;
                float _FacingThreshold;
                float _DebugMode;
                float4x4 _CV_OtherWorldToLocal;
                float4 _CV_OtherBoxCenter;
                float4 _CV_OtherBoxSize;
                float _CV_OtherDistScale;
                float _CV_OtherValid;
                float _CV_OtherTol;
                float4x4 _CV_SelfWorldToLocal;
                float4 _CV_SelfBoxCenter;
                float4 _CV_SelfBoxSize;
                float _CV_SelfDistScale;
                float _CV_SelfValid;
                float _CV_SelfTol;
            CBUFFER_END

            TEXTURE3D(_CV_OtherSDF);
            SAMPLER(sampler_CV_OtherSDF);
            TEXTURE3D(_CV_SelfSDF);
            SAMPLER(sampler_CV_SelfSDF);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            float SampleOther(float3 pw)
            {
                if (_CV_OtherValid < 0.5) return 1e5;
                float3 lp = mul(_CV_OtherWorldToLocal, float4(pw, 1)).xyz;
                float3 uvw = (lp - _CV_OtherBoxCenter.xyz) / _CV_OtherBoxSize.xyz + 0.5;
                if (any(uvw < 0.001) || any(uvw > 0.999)) return 1e5;
                float d = SAMPLE_TEXTURE3D(_CV_OtherSDF, sampler_CV_OtherSDF, uvw).r;
                return d * _CV_OtherDistScale;
            }

            float SampleSelf(float3 pw)
            {
                if (_CV_SelfValid < 0.5) return -1e5;
                float3 lp = mul(_CV_SelfWorldToLocal, float4(pw, 1)).xyz;
                float3 uvw = (lp - _CV_SelfBoxCenter.xyz) / _CV_SelfBoxSize.xyz + 0.5;
                if (any(uvw < 0.001) || any(uvw > 0.999)) return 1e5;
                float d = SAMPLE_TEXTURE3D(_CV_SelfSDF, sampler_CV_SelfSDF, uvw).r;
                return d * _CV_SelfDistScale;
            }

            float3 SelfOutward(float3 pw)
            {
                if (_CV_SelfValid < 0.5) return float3(0, 0, 0);
                float e = max(_CV_SelfTol, 0.003);
                float dx1 = SampleSelf(pw + float3(e, 0, 0));
                float dx0 = SampleSelf(pw - float3(e, 0, 0));
                float dy1 = SampleSelf(pw + float3(0, e, 0));
                float dy0 = SampleSelf(pw - float3(0, e, 0));
                float dz1 = SampleSelf(pw + float3(0, 0, e));
                float dz0 = SampleSelf(pw - float3(0, 0, e));
                if (max(dx1, max(dx0, max(dy1, max(dy0, max(dz1, dz0))))) > 1e4)
                    return float3(0, 0, 0);
                float3 g = float3(dx1 - dx0, dy1 - dy0, dz1 - dz0);
                float len = length(g);
                return len < 1e-5 ? float3(0, 0, 0) : g / len;
            }

            float3 OtherOutward(float3 pw)
            {
                float e = max(_CV_OtherTol, 0.003);
                float dx1 = SampleOther(pw + float3(e, 0, 0));
                float dx0 = SampleOther(pw - float3(e, 0, 0));
                float dy1 = SampleOther(pw + float3(0, e, 0));
                float dy0 = SampleOther(pw - float3(0, e, 0));
                float dz1 = SampleOther(pw + float3(0, 0, e));
                float dz0 = SampleOther(pw - float3(0, 0, e));
                if (max(dx1, max(dx0, max(dy1, max(dy0, max(dz1, dz0))))) > 1e4)
                    return float3(0, 0, 0);
                float3 g = float3(dx1 - dx0, dy1 - dy0, dz1 - dz0);
                float len = length(g);
                return len < 1e-5 ? float3(0, 0, 0) : g / len;
            }

            bool FacingOK(float3 pw, float3 selfOut)
            {
                if (_UseFacingGate < 0.5) return true;
                if (dot(selfOut, selfOut) < 0.5) return true;
                float3 go = OtherOutward(pw);
                if (dot(go, go) < 0.5) return true;
                // Reject only clearly SAME-facing contact (e.g., back of a thin hand seen
                // through the palm). Opposing AND perpendicular contact both pass, so
                // seat plates / backrests meeting the body at ~90 degrees still color.
                return dot(selfOut, go) < _FacingThreshold;
            }

            // Distance from pw along 'dir' until the OTHER volume ends, with sub-step
            // interpolation at the crossing (removes step-quantization banding).
            float OtherExitDistance(float3 pw, float3 dir)
            {
                float tol = max(_CV_OtherTol, 0.003);
                float maxT = max(_ProbeDepth, 0.02);
                const int N = 24;
                float prevT = 0;
                float prevD = SampleOther(pw);
                [loop]
                for (int i = 1; i <= N; i++)
                {
                    float t = maxT * ((float)i / (float)N);
                    float d = SampleOther(pw + dir * t);
                    if (d > tol)
                    {
                        float f = saturate((tol - prevD) / max(d - prevD, 1e-5));
                        return lerp(prevT, t, f);
                    }
                    prevT = t; prevD = d;
                }
                return 1e5;
            }

            float Penetration(float3 pw, float3 nw, float3 selfOut,
                              out float thickness, out float otherChord, out float dbgBack)
            {
                thickness = max(_ProbeDepth, 0.001);
                otherChord = 1e5;
                dbgBack = 0;

                float pen = 0;
                float d0 = SampleOther(pw);
                if (d0 < 0 && FacingOK(pw, selfOut))
                {
                    float align = saturate(-dot(selfOut, OtherOutward(pw)));
                    float tBack = OtherExitDistance(pw, -selfOut);
                    float tFwd = OtherExitDistance(pw, selfOut);
                    float wDir = smoothstep(_CV_OtherTol * 1.5, _CV_OtherTol * 4.0, -d0);
                    // Plain SDF depth is the honest measure for a THICK other object
                    // (a torso against a seat or backrest). The directional depth
                    // (how far behind me the other volume ends) only matters when this
                    // point is deeply ENGULFED in a thin other object (a mouse swallowed
                    // by the palm): there -d0 saturates at half the object's chord and
                    // underreports. Blend by the depth/chord ratio - a smooth weight, so
                    // no jagged on/off boundaries.
                    pen = -d0;
                    dbgBack = (tBack < 1e4) ? tBack : 0;
                    if (tBack < 1e4 && tFwd < 1e4)
                    {
                        otherChord = tBack + tFwd;
                        float wBury = smoothstep(0.15, 0.30, -d0 / max(otherChord, 1e-4));
                        float dirPen = max(-d0, tBack * align * wDir);
                        pen = lerp(-d0, dirPen, wBury);
                    }
                }

                int steps = (int)_ProbeSteps;
                if (_ProbeDepth > 0.0001 && steps > 0)
                {
                    // The mesh surface can sit a few voxels OUTSIDE the smoothed self
                    // SDF surface (tufted bumps get ironed flat). Start the walk from
                    // the SDF surface, not the mesh: otherwise the very first step
                    // reads "already exited" and thickness collapses to the floor.
                    float sd0 = max(SampleSelf(pw), 0);
                    if (sd0 > 1e4) sd0 = 0; // out of self box: treat as on-surface
                    float exitTol = sd0 + _CV_SelfTol;
                    float prevT2 = 0;
                    float prevSd = sd0;
                    [loop]
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = _ProbeDepth * ((float)i / (float)steps);
                        float3 q = pw - nw * t;

                        float sd = SampleSelf(q);
                        if (sd > exitTol)
                        {
                            // sub-step refinement of the self-exit -> smooth local thickness
                            float f = saturate((exitTol - prevSd) / max(sd - prevSd, 1e-5));
                            thickness = max(lerp(prevT2, t, f) - sd0, _ProbeDepth / max((float)steps, 1.0));
                            break;
                        }
                        prevT2 = t; prevSd = sd;
                        if (sd >= 0) continue;
                        // Attribute only points whose nearest self surface is the entry
                        // surface (distance walked inside ~= distance back to surface).
                        if (t - sd0 + sd > _CV_SelfTol * _AttributionSlack) continue;

                        float d = SampleOther(q);
                        if (d < 0 && (t - d) > pen && FacingOK(q, selfOut))
                            pen = t - d;
                    }
                }
                return pen;
            }

            float3 HeatColor(float t)
            {
                float seg = saturate(t) * 4.0;
                if (seg < 1.0) return lerp(_ContactColor0.rgb, _ContactColor1.rgb, seg);
                if (seg < 2.0) return lerp(_ContactColor1.rgb, _ContactColor2.rgb, seg - 1.0);
                if (seg < 3.0) return lerp(_ContactColor2.rgb, _ContactColor3.rgb, seg - 2.0);
                return lerp(_ContactColor3.rgb, _ContactColor4.rgb, seg - 3.0);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 n = normalize(input.normalWS);

                float3 selfOut = SelfOutward(input.positionWS);
                // Flip only when the mesh normal CLEARLY opposes the SDF outward direction
                // (broken imported normals) - the coarse gradient wobbles tens of degrees.
                if (dot(selfOut, selfOut) > 0.5 && dot(n, selfOut) < -0.4)
                    n = -n;

                float thickness, otherChord, dbgBack;
                // Direction for rays/facing: the smooth (orientation-corrected) mesh
                // normal. The coarse self-SDF gradient is only good for the flip test.
                float pen = Penetration(input.positionWS, n, n, thickness, otherChord, dbgBack);

                // ---- Debug visualization (temporary; _DebugMode 0 = off) ----
                if (_DebugMode > 0.5)
                {
                    if (pen <= 0) return half4(0.1, 0.1, 0.12, 1);
                    float v = 0;
                    if (_DebugMode < 1.5)      v = pen;        // 1: raw pen
                    else if (_DebugMode < 2.5) v = thickness;  // 2: self thickness
                    else if (_DebugMode < 3.5) v = dbgBack;    // 3: tBack
                    else                       v = min(otherChord, 0.3); // 4: other chord
                    return half4(HeatColor(saturate(v / 0.16)), 1); // linear, 0..160mm
                }

                Light mainLight = GetMainLight();
                // Light with the RAW mesh normal; two-sided so interiors don't go black.
                float3 nLight = normalize(input.normalWS);
                float ndl = abs(dot(nLight, mainLight.direction)) * 0.7 + 0.3;
                float3 baseLit = _BaseColor.rgb * mainLight.color.rgb * ndl;

                if (pen <= 0)
                    return half4(baseLit, 1);

                float maxPen = max(_MaxPenetration, 1e-5);
                if (_AdaptiveMax > 0.5)
                {
                    // Local thickness / chord shrink the red-saturation scale for thin
                    // parts, but never below the adaptive floor: an upholstery shell a
                    // few mm thin (open-bottomed seat cushions read as sheets in the
                    // SDF) must not collapse the whole scale into instant saturation.
                    float scale = min(thickness, otherChord);
                    maxPen = min(maxPen, max(scale, max(_AdaptiveFloor, 0.004)));
                }

                float t = saturate(pen / maxPen);
                t = pow(t, _HeatGamma); // spread the low range for richer gradation
                float3 contact = HeatColor(t);

                float border = smoothstep(0.0008, 0.006, pen); // feathered edge
                float3 col = lerp(baseLit, contact, border);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; };

            V DepthVert(A i)
            {
                V o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }
            half DepthFrag(V i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; };

            V ShadowVert(A i)
            {
                V o;
                float3 positionWS = TransformObjectToWorld(i.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                return o;
            }
            half4 ShadowFrag(V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}