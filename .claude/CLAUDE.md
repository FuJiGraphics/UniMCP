# UniMCP

Unity Editor MCP (Model Context Protocol) SDK. Unity 에디터 기능(프리팹 검사·리네임·SerializedField 조작·프리뷰 렌더링 등)을 MCP 툴로 노출해 Claude Code 등 MCP 클라이언트가 AI로 Unity 에디터 워크플로우를 구동하도록 만드는 재사용 가능한 SDK.

---

## 프로젝트 메타

| 항목 | 값 |
|------|-----|
| 형태 | Unity UPM 패키지 + Python MCP 서버 + Claude Code 스킬 템플릿 |
| 원격 | https://github.com/FuJiGraphics/UniMCP |
| 라이선스 | MIT |
| 패키지 ID | `com.unimcp.core` |
| Python 패키지 | `unimcp` |
| 최소 Unity 버전 | Unity 6 (6000.0) |
| 최소 Python 버전 | 3.10 |

---

## 아키텍처

```
MCP 클라이언트 (Claude Code 등)
        │  stdio MCP
        ▼
Python MCP 서버  (Server~/)         ← 상태 없는 중계
        │  HTTP localhost:<port>
        ▼
Unity Editor 브릿지  (Editor/)      ← HttpListener + [McpTool] 레지스트리
        │
        ▼
AssetDatabase / PrefabUtility / SerializedObject
```

**왜 Python 서버를 외부에 두나** — Unity 내부에 MCP 서버를 직접 호스팅하면 스크립트 컴파일(도메인 리로드)마다 연결이 끊김. 외부 프로세스로 분리하면 Unity가 재시작돼도 서버는 유지.

---

## 디렉토리 구조

```
UniMCP/
├─ package.json                    UPM 메타
├─ Editor/                         Unity 에디터 C# 브릿지
│  └─ UniMCP.Editor.asmdef
├─ Server~/                        Python MCP 서버 (~ suffix로 Unity import 제외)
│  ├─ pyproject.toml
│  └─ src/unimcp/
├─ Samples~/ClaudeCode/            Package Manager Samples 탭에서 import
│  ├─ .mcp.json.template
│  └─ skills/prefab-inspector/
│     ├─ SKILL.md                  엔진 제공 (SDK가 관리)
│     └─ conventions.template.md   사용자 프로젝트가 복사 후 자기 규칙 작성
└─ Documentation~/getting-started.md
```

`~` suffix 디렉토리는 Unity가 패키지 import 시 자동 제외. 리포지토리에는 남음.

---

## 로드맵

### Phase 1 — 기반 (read-only)
- Unity `HttpListener` 브릿지 + 도메인 리로드 핸들러
- `[McpTool]` 어트리뷰트 자동 디스커버리 툴 레지스트리
- Python MCP 서버 stub (HTTP 중계)
- 툴: `prefab.list`, `prefab.inspect`, `script.read`, `convention.read`
- `.mcp.json` + Claude Code skill 템플릿
- **검증**: Claude Code에서 "`A_UI/` 프리팹 중 컨벤션 위반 알려줘" 질의 → AI가 툴 호출로 분석·응답

### Phase 2 — 쓰기 (리네임 핵심)
- `editor.commit_checkpoint(message)` — git 자동 스냅샷
- `prefab.rename(path, newName)` — GUID 유지, 참조 자동 갱신
- `script.rename_class(path, newName)` — 파일+클래스명 원자 변경
- `script.rename_field(...)` — 필드 리네임 + **`[FormerlySerializedAs]` 자동 삽입** (인스펙터 바인딩 보존)
- 모든 쓰기 툴 `dryRun: true` 지원

### Phase 3 — 의미 판단 강화
- `prefab.screenshot(path)` — `AssetPreview` 기반 PNG → base64 응답. 비전 지원 클라이언트가 버튼 레이블·팝업 구조 등을 **시각으로** 판단

### Phase 4 — UX 보강 (선택)
- 에디터 윈도우: 프리팹 드래그앤드롭 + "Send to Claude" 헬퍼
- Project Settings UI: 포트/자동 기동 토글

---

## 핵심 설계 결정

1. **MCP 방식 선택**
   - 대안: 결정론적 EditorWindow + ScriptableObject 규칙
   - 선택 이유: **의미적 판단**(이 프리팹이 팝업인지 셀인지, 버튼 레이블 의미) + **컨벤션 변화 대응**(마크다운만 수정하면 AI 행동 변경) 때문
   - 단순 기계적 룰만 필요했다면 MCP는 과잉 투자

2. **외부 Python 서버 (vs Unity in-process)**
   - 도메인 리로드로 연결 끊김 문제 회피
   - Python MCP SDK 성숙도 활용
   - C# in-process는 stdio 구현 번거로움 + .NET MCP SDK 빈약

3. **SDK 분리 (독립 레포)**
   - TeamBattle 프로젝트 내 프로토타입 아닌 처음부터 독립 레포로 시작
   - 다른 Unity 프로젝트에서도 재사용
   - 룰은 SDK 밖(사용자 프로젝트의 `.claude/`)에 보관

4. **`[FormerlySerializedAs]` 자동 삽입**
   - 필드 리네임 시 인스펙터 바인딩 보존을 위해 필수
   - Phase 2 초기엔 정규식 기반 (간단한 단일 필드 케이스만 커버)
   - partial class, 여러 attribute 병합, using 추가 등 복잡 케이스는 **수동 안내로 타협**
   - 안정화 단계에서 Roslyn 도입 검토

5. **conventions.md는 사용자 프로젝트 소유**
   - SDK는 엔진만 제공
   - 각 프로젝트가 `.claude/skills/prefab-inspector/conventions.md`에 자기 규칙 작성
   - 컨벤션 변경 = 마크다운만 수정 (코드 변경 없음)

6. **툴 확장성 (Phase 1 이후)**
   - `[McpTool("namespace.name")]` 어트리뷰트로 각 사용자 프로젝트가 커스텀 툴 추가 가능
   - 예: 프로젝트 A는 `stage.inspect` 같은 도메인 특화 툴 추가

---

## 안전장치

- 모든 쓰기 툴은 `dryRun: true` 지원 (Preview → Apply 흐름)
- 쓰기 전 `editor.commit_checkpoint` 호출로 git 스냅샷 (롤백 안전망)
- Unity Play Mode 중엔 쓰기 툴 거부
- 변경안은 사용자 승인 후에만 Apply

---

## 주요 기술 리스크

| 영역 | 리스크 | 대응 |
|------|--------|------|
| 도메인 리로드 | HttpListener 끊김 | `InitializeOnLoadMethod` Start, `AppDomain.DomainUnload` Stop, 재기동 자동화 |
| `[FormerlySerializedAs]` | partial class·attribute 병합 엣지 케이스 | 초기엔 정규식, 실패 시 수동 안내. 장기적으로 Roslyn |
| Nested Prefab / Variant | 리네임 시 부모 참조 | 탐색 후 동기 업데이트 |
| 포트 충돌 | 여러 Unity 인스턴스 | 인스턴스 디스커버리 파일(`~/.unimcp/instances.json`) 또는 ProjectSettings 포트 오버라이드 |
| OS 경로 차이 | Mac/Windows | 초반부터 `Path.Combine` / `Path.GetFullPath` 일관 사용 |

---

## 릴리즈 흐름

1. `package.json`의 `version` 업데이트 (semver)
2. git 태그: `git tag v0.x.y && git push origin v0.x.y`
3. (선택) GitHub Releases 노트 작성

사용자 설치 (Unity):
```
Packages/manifest.json
---
"com.unimcp.core": "https://github.com/FuJiGraphics/UniMCP.git#v0.0.1"
```

Python 서버는 별도 설치:
- 개발 중: `pip install -e Packages/com.unimcp.core/Server~`
- PyPI 공개 후: `uvx unimcp`

---

## 현재 상태 (2026-04-17)

- [x] 레포 초기화, 스캐폴딩 커밋(`0e14f85`)
- [x] UPM 레이아웃, Python 서버 스켈레톤, Claude Code 스킬 템플릿
- [x] `CLAUDE.md` 프로젝트 컨텍스트 기록
- [ ] Phase 1 — Unity 브릿지 + Python 서버 + read 툴 3종
- [ ] `v0.0.1` 태그
- [ ] PyPI `unimcp` 배포 (장기)

---

## 작업 규칙

- 커밋 메시지에 `Co-Authored-By: Claude ...` 라인 넣지 말 것
- 설계·구조·방향 결정에는 **독립 의견을 먼저** 제시한 뒤 진행. 사용자 요구를 즉시 따르기보다 트레이드오프·대안을 함께 제시
- 신규/수정 `.cs` 파일에 `#region` 남발 금지 (소규모·디버그 파일)
- 변경은 작은 단위 커밋으로 분할, 의미 있는 경계마다 푸시

---

## 참조 외부 자료

- MCP 스펙: https://spec.modelcontextprotocol.io/
- Python MCP SDK: https://github.com/modelcontextprotocol/python-sdk
- Unity UPM 문서: https://docs.unity3d.com/Manual/CustomPackages.html
- 참고 프로젝트: `justinpbarnett/unity-mcp` (Python+TCP), `CoderGamester/mcp-unity` (Node+WebSocket)
