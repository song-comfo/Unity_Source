# Contact Visualization Toolkit

인체 모델과 제품 모델 사이의 **접촉/침투 영역을 히트맵으로 시각화**하고,
**침투 깊이·접촉 면적·압력·압박도**를 수치로 측정하는 Unity 툴킷입니다.

- 임의의 메쉬 쌍 지원 — 법선 뒤집힘, 비수밀(non-watertight) 메쉬, 멀티 서브메쉬,
  밑면이 뚫린 가구 셸 등 불량/복잡 임포트에 강건
- SkinnedMesh 포즈(armature) 또는 상대 위치 변경 시 자동 리베이크
- 침투 깊이에 비례한 5단계 히트맵 (파랑 → 청록 → 초록 → 노랑 → 빨강), 양쪽 오브젝트 모두 색칠

---

## 1. 공유 파일 구성 (`Assets/ContactVis/`)

| 파일 | 설명 |
|---|---|
| `ContactPenetrationVisualizer.cs` | 핵심 컴포넌트 — SDF 베이크, 접촉 쌍 관리, 자동 리베이크 |
| `ContactDepthReporter.cs` | 수치 측정 — 침투 깊이(최대/평균/최소), 접촉 면적, 압력, 압박도. 콘솔 로그 + Inspector 표시 |
| `ContactVisualizer.shader` | 히트맵 셰이더 (URP 전용) |
| `Contact_Human.mat` | 인체용 머티리얼 (Facing Gate ON) |
| `Contact_Product.mat` | 제품용 머티리얼 (Facing Gate OFF) |
| `README.md` | 본 문서 |

> **.meta 파일을 반드시 함께 복사하세요** (머티리얼↔셰이더 연결 유지).
> meta 없이 받았다면 아래 트러블슈팅의 '머티리얼이 분홍색' 항목 참고.

---

## 2. 요구 사항

| 항목 | 요구 |
|---|---|
| Unity | 6.x (URP 17.x 기준 개발) |
| 렌더 파이프라인 | **URP 필수** — 셰이더가 URP 전용 |
| 패키지 1 | **Visual Effect Graph** (`com.unity.visualeffectgraph`) — SDF 베이커. 없으면 컴파일 에러 |
| 패키지 2 | **glTFast** (`com.unity.cloud.gltfast`) — GLB 모델 임포트용 (GLB를 쓸 경우) |

---

## 3. 적용 방법

### Step 1 — 프로젝트 준비
1. **URP 템플릿**으로 프로젝트 생성 (또는 기존 URP 프로젝트)
2. Package Manager에서 **Visual Effect Graph**, **glTFast** 설치 (모델 임포트 전에 먼저!)

### Step 2 — 파일 배치
1. `Assets/ContactVis/`를 생성하고 cs, shader, mat 파일들 복사 (meta 포함)
2. 모델(.glb 등)을 `Assets/`에 넣고 컴파일 에러가 없는지 확인

### Step 3 — 모델 임포트 확인
1. 각 모델 Inspector에서 **Read/Write Enabled 체크** (수치 측정이 버텍스를 읽습니다)
2. **스케일 확인**: 시스템 단위는 미터 — 인체 1.6~1.9m 수준인지 확인

### Step 4 — 접촉 쌍 구성 (핵심)
1. 인체와 제품 모델을 씬에 배치
2. 빈 GameObject(예: `ContactPair`) 생성 → **ContactPenetrationVisualizer** 추가
3. 머티리얼 할당: 인체의 모든 Renderer → `Contact_Human`, 제품의 모든 Renderer → `Contact_Product`
4. 컴포넌트 설정:

   | 필드 | 값 |
   |---|---|
   | Object A → Root / Material | 인체 루트 / `Contact_Human` |
   | Object A → Sdf Max Resolution | 256 |
   | Object B → Root / Material | 제품 루트 / `Contact_Product` |
   | Object B → Sdf Max Resolution | 256 |
   | Object B → Surface Band Voxels | 케이블 등 얇은 열린 튜브가 있으면 2.0 |
   | Focus Margin | 0.3 (기본값 유지 권장) — 상대 물체 주변에 SDF 해상도 집중 |

5. 컴포넌트 우클릭 → **Rebake SDFs** (수 초 소요)
6. 두 모델을 겹치면 양쪽 표면에 히트맵이 나타남. 포즈/위치 변경 시 정지 후 ~0.5초 뒤 자동 리베이크

### Step 5 — 수치 측정
1. 같은 GameObject에 **ContactDepthReporter** 추가, `Pair`에 위 컴포넌트 지정
2. 기본 0.5초 주기 자동 측정, **값이 변할 때만** 콘솔에 로그:
   `[ContactVis] 침투 깊이: 최대/평균/최소 mm | 접촉 면적 cm² | 압력 kPa | 압박도 N`
3. Inspector의 Last Measurement 섹션에 항상 표시, 우클릭 → Measure Now 로 즉시 측정

   | 지표 | 정의 |
   |---|---|
   | 침투 깊이 | 접촉 중인 인체 버텍스들의 깊이 통계 (mm) |
   | 접촉 면적 | 접촉 버텍스의 대표 면적 적분, 인체 표면 기준 (cm²) |
   | 압력 | p = k × depth (k = `Pressure KPa Per Mm`, 기본 1.5, 잠정 수식) |
   | 압박도 | Σ pᵢ × aᵢ = 총 접촉 하중 (N, 잠정 수식) |

### Step 6 — 여러 쌍 동시 사용
- 쌍마다 (1) ContactPenetrationVisualizer + Reporter 하나, (2) **전용 머티리얼 2개** 필요.
  머티리얼을 복제(Ctrl+D)해서 쌍마다 따로 할당 — 쌍끼리 머티리얼 공유 금지.

---

## 4. 주요 파라미터 (머티리얼)

| 파라미터 | 기본 | 설명 |
|---|---|---|
| Max Penetration | 0.08 m | 빨강으로 포화되는 절대 침투 기준 |
| Adapt Scale To Local Thickness | ON | 얇은 부위는 국소 두께 기준으로 스케일 → 완전 관통 시 빨강 |
| **Adaptive Floor** | 0.03 m | 적응형 스케일의 하한. 밑면이 뚫린 가구 셸처럼 SDF상 종잇장이 되는 형상이 즉시 포화되는 것 방지. **의자 등 가구 제품 0.06 / 마우스 등 소형 제품 0.03 권장** |
| Facing Gate | 인체 ON / 제품 OFF | 같은 방향(same-facing) 접촉만 차단 — 얇은 부위 반대편 오염 방지. 기본값 유지 권장 |
| Burial Probe Depth | 0.18 m | 표면 아래 파묻힌 물체 감지 깊이 |
| Contact 0%~100% | 파랑~빨강 | 5단계 히트맵 색상 (자유 변경) |

---

## 5. 트러블슈팅

| 증상 | 해결 |
|---|---|
| .glb가 임포트되지 않음 | glTFast 설치 확인 후 해당 파일 Reimport |
| 머티리얼이 분홍색 | 셰이더 연결 끊김 → 머티리얼의 Shader를 `Custom/ContactVisualizer`로 재지정, Facing Gate를 인체 ON/제품 OFF로 |
| 히트맵이 아예 안 나옴 | ① Rebake SDFs ② 머티리얼이 컴포넌트 Material 필드와 렌더러 양쪽에 연결됐는지 ③ 실제로 겹쳐 있는지 확인 |
| `Mesh is not readable` | Import Settings → Read/Write Enabled 체크 후 Reimport |
| 케이블·얇은 판 감지 안 됨 | 해당 오브젝트 Surface Band Voxels = 2.0 |
| 접촉 영역이 전부 빨강으로 포화 | 제품 머티리얼의 Adaptive Floor를 0.06으로 (가구류), 또는 Max Penetration 상향 |
| 특정 부품(쿠션 등)만 접촉이 안 잡힘 | 구버전 증상 — 멀티 서브메쉬 미지원이 원인이었고 현재 버전은 전 서브메쉬를 베이크함. 최신 스크립트인지 확인 |
| 포즈 변경 후 히트맵 이상 | 자동 리베이크 대기(정지 후 ~0.5초) 또는 수동 Rebake SDFs |
| 씬 재오픈 시 히트맵 없음 | 정상 — SDF는 런타임 데이터, 컴포넌트 활성화 시 자동 재베이크(수 초) |

---

## 6. 동작 원리 요약

1. **SDF 베이크**: 오브젝트의 모든 자식 메쉬·모든 서브메쉬를 합쳐 VFX Graph `MeshToSDFBaker`로 3D 거리장 생성. 상대 오브젝트 주변(포즈 반영 실측 bounds + margin)에만 국소 베이크해 해상도 집중
2. **부호 재구성 (2단계 플러드필)**: 베이커 부호를 신뢰하지 않고 복셀 플러드필로 내부/외부 재판정.
   1단계는 표면에서 충분히 떨어진 '확실히 열린 공간'만 침수시켜 작은 구멍으로 빈 껍데기 속에
   새어 들어가는 것을 방지, 2단계는 표면 근처를 제한 깊이만큼만 채움
   → 법선 뒤집힘·비수밀·이중 셸·열린 튜브·구멍 뚫린 가구에서 안정 동작
3. **히트맵 셰이더**: 각 표면 픽셀이 상대 SDF를 샘플링해 침투 깊이를 색으로 표현.
   표면 아래 레이마칭으로 파묻힌 물체도 감지, 자기 볼륨 제한·최근접 귀속·대면 게이트로 오염 차단.
   두꺼운 몸통에는 순수 SDF 깊이, 얇은 물체에 파묻힌 경우에만 방향성 깊이를 혼합
4. **수치 측정**: 버텍스 단위 CPU 샘플링으로 깊이 통계·면적 적분·압력(잠정 모델) 산출
