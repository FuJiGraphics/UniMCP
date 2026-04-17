# UniMCP Tools

UniMCP가 제공하는 Unity 프로젝트 조작·분석 도구 모음. 스킬과 에이전트는 **여기 등록된 도구를 우선 사용**하고, 즉석 스크립트 작성은 지양한다.

## 사용 원칙

- 새 작업을 시작하기 전에 이 README 를 읽고 기존 도구 활용 가능성을 확인한다
- 도구가 없으면 UniMCP 관리자에게 추가 요청 (스킬 폴더 내 임시 스크립트 금지)
- 도구 호출 전 경로 탐색: `../UniMCP/Tools~/` → `Packages/com.unimcp.core/Tools~/` → Glob `**/Tools~/<name>`

## 도구 목록

### apply_convention.js (권장 — 단일 호출)

프리팹 하나에 UI 네이밍 컨벤션을 끝까지 적용. 분석 + 규칙 계산 + 리네임 + nested override + 파일 리네임까지 원샷.

**사용:**
```bash
node ../UniMCP/Tools~/apply_convention.js <prefab_path>
```

**출력:** JSON 리포트 (root / children / nested / file_renamed)

**용도:** 스킬에서 호출하면 0.3~1초에 전부 완료. Sonnet 같은 LLM 이 여러 툴 오케스트레이션 할 필요 없음.

---

### analyze_prefab.js (저수준 — 개별 툴 조합 시)

Unity 프리팹 YAML 을 파싱해 루트·자식 구조와 부착 컴포넌트를 JSON 으로 출력.

**사용:**
```bash
node ../UniMCP/Tools~/analyze_prefab.js <path/to/file.prefab>
```

**출력:**
```json
{
  "root_name": "PopupReceived",
  "root_components": ["PopupReceivedView"],
  "children": [
    {"fileID": "123", "name": "BTN_Confirm", "components": ["Button", "Image"]},
    ...
  ]
}
```

**용도:**
- 프리팹 컨벤션 검사 (루트 이름 vs 클래스명)
- 자식 GameObject 네이밍 검사 (Button → BTN_, Image → IMG_ 등)
- 부착 컴포넌트로 UI 역할 파악
- Nested 프리팹 인스턴스 식별 + source 정보 추출 (source_root_file_id, source_root_components 포함)

### rename_nested.py

Nested 프리팹 인스턴스의 `m_Name` 을 **부모 프리팹의 m_Modifications override** 로 변경. 원본 프리팹 파일은 건드리지 않음.

**사용:**
```bash
node ../UniMCP/Tools~/rename_nested.js \
  <parent_prefab.prefab> <instance_id> <source_root_file_id> <source_guid> <new_name>
```

`analyze_prefab.py` 의 `nested_prefabs[]` 에서 필요한 인자 전부 얻을 수 있음:
- `instance_id` — 그대로 사용
- `source_root_file_id` — 그대로 사용  
- `source_guid` — 그대로 사용
- `new_name` — 컨벤션에 맞게 계산

**용도:** nested 프리팹을 현재 프리팹 범위에서만 리네임. 같은 source 프리팹을 쓰는 다른 곳은 영향 없음.

---

## 도구 추가 시 가이드

1. `Tools~/<name>.py` 에 스크립트 추가
2. 이 README 에 블록 추가 (사용법·출력 포맷·용도)
3. 실행 파일이면 shebang (`#!/usr/bin/env python3`) 포함
4. 입출력 규격은 JSON 우선 (파이프라인 연결 쉬움)
