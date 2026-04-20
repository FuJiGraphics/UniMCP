using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniMCP.Editor.Logging;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.PrefabHook
{
    /// <summary>
    /// UniMCP 패키지 내장 스킬. 프롬프트는 메모리에만 보유하되 Claude Code 슬래시 커맨드 해석을 위해 유저-글로벌 `~/.claude/skills` 에 설치.
    /// builtin-prefab-gen 은 ""분석자"": 이미지 → TreeSpec JSON 출력만 담당. 실제 프리팹 생성은 결정론적 C# TreeGenerator 가 처리
    /// </summary>
    [InitializeOnLoad]
    public static class BuiltinSkills
    {
        public const string PrefabGenSkillName    = "builtin-prefab-gen";
        public const string UiConventionSkillName = "builtin-ui-convention";
        public const string PrefabReviewSkillName = "builtin-prefab-review";

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        /// <summary>
        /// 유저-글로벌 `~/.claude/skills`. Claude Code 슬래시 커맨드는 프로젝트-로컬 뿐 아니라 이 경로도 해석한다.
        /// 빌트인 스킬은 패키지 소유이므로 유저 프로젝트 경계 밖인 여기에 설치한다
        /// </summary>
        private static string SkillsRoot => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".claude", "skills");

        private static string ProjectSkillsRoot => Path.Combine(ProjectRoot, ".claude", "skills");

        static BuiltinSkills()
        {
            EditorApplication.delayCall += EnsureInstalled;
        }

        public static UniMcpSkill GetPrefabGenSkill() =>
            new() { name = PrefabGenSkillName, prompt = PrefabGenPrompt };

        public static UniMcpSkill GetUiConventionSkill() =>
            new() { name = UiConventionSkillName, prompt = UiConventionPrompt };

        public static UniMcpSkill GetPrefabReviewSkill() =>
            new() { name = PrefabReviewSkillName, prompt = PrefabReviewPrompt };

        public static List<UniMcpSkill> GetAll() =>
            new() { GetPrefabGenSkill(), GetUiConventionSkill(), GetPrefabReviewSkill() };

        public static bool IsBuiltin(string skillName) =>
            skillName == PrefabGenSkillName ||
            skillName == UiConventionSkillName ||
            skillName == PrefabReviewSkillName;

        private static void EnsureInstalled()
        {
            try
            {
                Directory.CreateDirectory(SkillsRoot);
                WriteSkill(PrefabGenSkillName, "SKILL.md", PrefabGenPrompt);
                WriteSkill(UiConventionSkillName, "SKILL.md", UiConventionPrompt);
                WriteSkill(PrefabReviewSkillName, "SKILL.md", PrefabReviewPrompt);

                CleanupProjectInstall();
            }
            catch (System.Exception e)
            {
                UniMcpLogger.Warn("builtin 스킬 설치 실패: " + e.Message);
            }
        }

        private static void CleanupProjectInstall()
        {
            if (!Directory.Exists(ProjectSkillsRoot)) return;

            string[] projectLocalBuiltinDirs =
            {
                "unimcp-" + PrefabGenSkillName,
                "unimcp-" + UiConventionSkillName,
                "unimcp-" + PrefabReviewSkillName,
                "unimcp-builtin-image-analyze",
                "unimcp-UI-컨벤션-조정-Agent",
            };

            foreach (var dirName in projectLocalBuiltinDirs)
            {
                var dir = Path.Combine(ProjectSkillsRoot, dirName);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    UniMcpLogger.Info($"프로젝트 로컬 빌트인 스킬 제거: {dir}");
                }
                catch (System.Exception e)
                {
                    UniMcpLogger.Warn($"프로젝트 로컬 빌트인 제거 실패 ({dir}): " + e.Message);
                }
            }

            try
            {
                var settings = UniMcpSettings.instance;
                var current = settings.Skills.ToList();
                var filtered = current
                    .Where(s => SkillStore.GetInvocationName(s.name) != "unimcp-UI-컨벤션-조정-Agent")
                    .ToList();
                if (filtered.Count != current.Count)
                {
                    settings.SetSkills(filtered);
                    UniMcpLogger.Info("UI 컨벤션 스킬을 빌트인으로 이관");
                }
            }
            catch { }
        }

        private static void WriteSkill(string skillName, string fileName, string promptBody)
        {
            var dirName = SkillStore.ManagedPrefix + skillName;
            var dir = Path.Combine(SkillsRoot, dirName);
            Directory.CreateDirectory(dir);

            var invocation = SkillStore.GetInvocationName(skillName);
            var content =
                $"---\n" +
                $"name: {invocation}\n" +
                $"description: UniMCP builtin skill ({skillName}).\n" +
                $"unimcp_managed: true\n" +
                $"unimcp_builtin: true\n" +
                $"---\n\n" +
                promptBody;

            var path = Path.Combine(dir, fileName);
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content);
        }

        private const string PrefabGenPrompt = @"너는 레이아웃 이미지를 받아 **Unity UI 프리팹의 구조 트리(TreeSpec)를 JSON 으로 출력** 하는 분석 에이전트다. 훅을 직접 호출하지 않는다. 실제 프리팹 생성은 별도 결정론적 컴포넌트가 담당한다.

## 입력 (Targets)

- **Target 1**: 레이아웃 이미지 경로
- **Target 2**: manifest JSON 경로 — 아래 필드 확인:
  - `outputFolder`: 출력 프리팹을 둘 폴더 (경로 생성에 사용, `createdAssets` 는 건드리지 않음)
  - `userHint`: 자연어 힌트 (비어있지 않으면 **최우선 반영**). 예: ""Header 는 Scroll 안에 포함"", ""하단 BtnGroup 은 Grid 로""
  - `referencePrefabs`: 재사용 가능한 기존 프리팹 **메타데이터** 배열. 각 항목은:
    ```
    {
      ""path"": ""Assets/A_Prefabs/UI/Button/BTN_Confirm.prefab"",
      ""rootName"": ""BTN_Confirm"",
      ""rootComponents"": [""RectTransform"", ""Image"", ""Button""],
      ""children"": [{""name"":""IMG_BG"",""components"":[""Image""]}, {""name"":""TXT_Label"",""components"":[""TextMeshProUGUI""]}],
      ""kind"": ""button"" | ""toggle"" | ""cell"" | ""scroll"" | ""layout"" | ""image"" | ""text"" | ""generic"",
      ""approximateWidth"": 240,
      ""approximateHeight"": 120
    }
    ```
    레퍼런스 이미지에서 유사한 역할의 요소를 발견하면 **새로 box+button+text 구성하지 말고** `nestedPrefab` 필드에 `path` 를 넣어 재사용.
    매칭 판단 기준 (우선순위):
    1. `kind` — 이미지의 요소 타입(button/cell/...)과 일치하는지
    2. `rootName` — 의미상 가까운 이름인지 (Confirm/Cancel/Close/Reward 등)
    3. `children` — 내부 구조(예: 버튼에 Icon+Text 있음)가 이미지와 맞는지
  - `referenceFont`: 기본 TMP Font Asset 경로. 모든 텍스트의 `fontPath` 로 사용.

## 출력

**오직 하나의 fenced JSON 블록만 출력.** 앞뒤 설명·주석·진행 보고 금지.

예:
```json
{ ""outputPath"": ""..."", ""size"": [1080, 1920], ""root"": { ... } }
```

## TreeSpec 스키마

최상위:
```
{
  ""outputPath"": ""Assets/.../PopupX.prefab"",   // manifest.outputFolder + 추론한 이름
  ""size"": [1080, 1920],                          // 루트 sizeDelta
  ""root"": Node
}
```

Node (모든 필드 옵션):
```
{
  ""name"": ""MainFrame"",                         // GameObject 이름 (PascalCase, 필수)

  // 시각 (box)
  ""box"": true,                                   // Image + Outline 자동 부착
  ""color"": ""#FFFFFFFF"",                        // box 채움 (Wireframe 기본 흰색)
  ""outlineColor"": ""#111111FF"",                 // 아웃라인 색 (Wireframe 기본 검정)
  ""sprite"": ""Assets/UI/bg_popup.png"",          // (옵션) 명시적으로 알려진 스프라이트 경로만 지정. 짐작 금지

  // 레이아웃 역할 (자식이 있을 때)
  ""layout"": ""vertical"" | ""horizontal"",
  ""spacing"": 20,
  ""padding"": [30, 30, 30, 30],                   // [L, R, T, B]

  // 부모 레이아웃 내 이 노드의 크기
  ""preferredHeight"": 150,                        // 부모가 vertical
  ""flexibleHeight"": 1,                           // 남은 공간 차지
  ""preferredWidth"": 200,                         // 부모가 horizontal
  ""flexibleWidth"": 1,

  // 오버레이 (복잡 레이아웃 — 부모 LayoutGroup 무시하고 절대 배치)
  ""overlay"": true,
  ""anchor"": ""center"",                          // topLeft|top|topRight|left|center|right|bottomLeft|bottom|bottomRight|stretch
  ""offsetX"": 0, ""offsetY"": 0,                  // anchor 기준 pixel offset (stretch 일 땐 4변 margin 으로 해석)
  // overlay 노드도 preferredWidth/preferredHeight 사용해 크기 지정 (stretch 제외)

  // 텍스트 (자식 Txt 에 TMP 자동 부착)
  ""text"": ""Title"",
  ""textAlign"": ""Center"",                       // TextAlignmentOptions enum (Center, Left, Right, MidlineLeft 등)
  ""fontSize"": 48,
  ""textColor"": ""#111111FF"",
  ""fontPath"": ""Assets/Fonts/MyFont SDF.asset"", // TMP Font Asset 경로 (manifest.referenceFont 기본 사용)

  // 기존 프리팹 재사용 (manifest.referencePrefabs 중 하나의 경로)
  // 지정되면 box/button/text/layout/children 대신 해당 프리팹 인스턴스를 배치한다
  ""nestedPrefab"": ""Assets/A_Prefabs/UI/Button/BTN_Confirm.prefab"",

  // 인터랙션
  ""button"": true,                                // Button 컴포넌트 부착 (box 도 함께 권장)

  // 게이지 / 프로그레스 바 (Image.type = Filled)
  ""progressBar"": true,
  ""fillDirection"": ""vertical"" | ""horizontal"",   // 기본 vertical
  ""fillAmount"": 0.6,                                 // 0~1, 채움 비율 (이미지에서 대충 추정)
  ""fillColor"": ""#FF7326FF"",                        // 채움색 (기본 주황), color 는 배경 박스 색

  // 스크롤 (자동 Viewport/Content/ScrollRect/Mask 구성)
  ""scroll"": true,
  ""scrollDirection"": ""vertical"",
  ""scrollLayout"": ""vertical"" | ""horizontal"" | ""grid"",   // 스크롤 Content 내부 아이템 배치 (기본 vertical)
  ""scrollCellWidth"": 200,    // grid 일 때 cell 크기 (px)
  ""scrollCellHeight"": 200,

  ""children"": [ Node, Node, ... ]
}
```

## 분석 규칙

### 0. 먼저 모든 rect 를 빠짐없이 나열 (필수 사전 단계)

TreeSpec 을 쓰기 전에 **머리 속으로** 이미지의 outlined rect 를 하나도 빠짐없이 나열한다. 체크 항목:

1. **최외곽 rect 존재 여부** — 화면 거의 전체를 감싸는 큰 rounded rectangle 이 있는지? 있으면 그게 top-level container 역할을 하고 내부 모든 rect 는 그 자식.
2. **중간 래핑 rect** — 여러 요소를 묶는 중간 크기 박스가 있는지? (예: Header + Scroll + BtnGroup 을 모두 감싸는 하나의 박스)
3. **리프 rect** — 단일 text/button 하나만 담는 박스

**흔한 실수**: 큰 래핑 rect 를 놓치고 내부 자식들을 root 직속 sibling 으로 올려버리는 것. 이런 경우 Title/MainContent/Bottom 이 ""나란히"" 보인다는 이유로 전부 root.children 에 넣게 됨. **절대 그렇게 하지 말 것.** rect A 가 rect B 를 **경계 안에** 포함하면 A 는 B 의 부모.

### 1. 포함 관계 트리

위 rect 나열이 끝나면 **포함 관계 트리** 구성. A 의 bounding box 4 변이 모두 B 의 내부에 있으면 A 는 B 의 children. ""화면상 위치"" 가 아니라 ""테두리 경계"" 로만 판단.

### 2. 루트 자식 개수 = 최상위 rect 개수

서로 겹치지 않는 최상위 rect 가 N 개 = `root.children` N 개. 단일 Frame 에 강제로 욱여넣지 말 것. 루트 자체에 `""layout"": ""vertical""` 지정해 자동 스택.

### 3. 크기 — 정량 측정 후 비율로 변환

**구조 + 크기 둘 다** 정확히 잡아야 한다. 수치 러프하면 생성 결과가 명백히 틀리게 보인다. 출력 전 반드시 아래 절차 수행:

#### 3-A. 측정 표 작성 (머리속)

각 섹션에 대해 **부모 대비 비율** 을 추정한다. 예시:

| 섹션 | 부모 | 레퍼런스에서 차지 | → preferredHeight |
|------|------|------------------|-------------------|
| TitleBar | root(1920h) | ~6% | 1920×0.06 = 115 |
| TopPanel | root | ~55% | flexibleHeight:1 (남은 공간) |
| Header | TopPanel(~1050h) | ~14% | 1050×0.14 ≈ 150 |
| Portrait | TopPanel | ~50% | flexibleHeight:1 |
| BottomPanel | root | ~35% | 1920×0.35 ≈ 670 or flexibleHeight:1 |
| Accum btn | root | ~5% | 1920×0.05 ≈ 100 |

- `size`: 풀스크린이면 `[1080, 1920]`, 중앙 팝업이면 `[800, 1200]` (portrait) 또는 `[1200, 800]` (landscape).
- 각 부모 안에서 **오직 하나** 의 자식만 `flexibleHeight:1` (남은 공간 흡수). 나머지는 preferredHeight 고정.
- horizontal 레이아웃이면 같은 방식으로 `preferredWidth` / `flexibleWidth`.

#### 3-B. 텍스트 fontSize 추정

텍스트의 **시각 높이 ≈ fontSize × 1.3** (TMP). 레퍼런스에서 텍스트 박스 내부 텍스트가 박스 높이의 몇 % 인지 보고 역산:
- 150h 버튼 내부 텍스트가 박스 높이의 40% 차지 → fontSize ≈ 150 × 0.4 / 1.3 ≈ 46
- 110h 타이틀바의 큰 타이틀이 60% → fontSize ≈ 110 × 0.6 / 1.3 ≈ 50
- 작은 설명 텍스트 (박스 대비 25~30%) → fontSize 24~32

#### 3-C. spacing · padding

레퍼런스에서 형제 요소 사이 gap 을 대충 측정. 풀스크린 기본 `spacing: 20~30`, `padding: [25, 25, 25, 25]`. 빡빡한 카드 내부는 `padding: [10, 10, 10, 10]` 같이 작게.

#### 3-D. 절대 좌표 금지

`x`, `y`, `width`, `height`, `sizeDelta` 필드 사용 불가. 모든 배치는 layout + preferredSize/flexibleSize + (복잡 레이아웃은) overlay + anchor/offset 로만.

### 3.5. 복잡 레이아웃 — overlay 로 겹침·절대 배치 표현

flow-layout(LayoutGroup) 만으로 표현 불가한 케이스:
- 배경 위에 전경 요소가 **겹쳐** 올라감 (예: 광산 씬 배경 + 세로 게이지 + 하단 카드 패널)
- 한쪽 구석에 떠 있는 요소 (예: 우상단 통화 표시)
- 풀스크린 dim 뒤에 centered 팝업

이런 경우 **overlay 자식** 으로 처리:
- 부모가 box + (선택)layout 을 가진 컨테이너.
- 자식 중 overlap 되는 요소는 `overlay: true` + `anchor` + `offsetX/offsetY` + `preferredWidth/preferredHeight`.
- overlay 자식은 부모의 LayoutGroup 을 무시하고 anchor 기반으로 절대 배치된다.
- 같은 부모 안에 **flow-layout 자식 + overlay 자식** 혼용 가능.

anchor 값(9-point):
- 기본 9 지점: `topLeft`, `top`, `topRight`, `left`, `center`, `right`, `bottomLeft`, `bottom`, `bottomRight`
- `stretch`: 부모 전체 채움. `offsetX/Y` 는 4변 margin 으로 해석 (양수 = 안쪽 margin)

예:
- 우상단 `999999` 코인 표시 → `{ ""name"": ""Currency"", ""overlay"": true, ""anchor"": ""topRight"", ""offsetX"": -20, ""offsetY"": -20, ""preferredWidth"": 320, ""preferredHeight"": 80 }`
- 배경 이미지가 부모 전체를 채움 → `{ ""name"": ""BG"", ""overlay"": true, ""anchor"": ""stretch"", ""box"": true, ""sprite"": ""..."" }`
- 하단에 딱 붙는 버튼 영역 → `{ ""name"": ""Dock"", ""overlay"": true, ""anchor"": ""bottom"", ""offsetY"": 20, ""preferredWidth"": 900, ""preferredHeight"": 150 }`

**overlay 판단 기준 (이미지 판독)**:
1. 두 rect 의 bounding box 가 **서로 겹치는가**? 하나가 다른 하나를 완전 포함하지 않고 일부만 겹침 → overlay.
2. 부모 박스 내부에 요소가 **한쪽 구석에 치우쳐** 있고, LayoutGroup 으로는 그 위치 표현이 불편함 → overlay.
3. 배경 이미지(씬/벽지) 위에 다른 UI 요소가 겹쳐 있음 → 배경을 overlay+stretch, 전경은 flow-layout 또는 overlay 로.

### 3.6. 자주 나오는 패턴 인식 (필수)

이런 패턴은 Analyzer 가 자주 놓친다. 명시적으로 찾아라:

#### (1) Progress Gauge (진행 바) — **N 개 나란한 세로/가로 얇은 bar**

레퍼런스에 **세로로 긴 얇은 직사각형** 이 2~6개 나란히 반복돼 있으면(내부에 부분 채움이든 빈 박스든) **progress gauge row** 이다. ""BarArea"" 같은 이름으로 children 없이 flexibleHeight 만 넣고 끝내지 말 것.

→ **반드시** 부모에 `layout: horizontal` + 각 자식 `progressBar: true` + `preferredWidth: 60~100` + `fillDirection: vertical` + `fillAmount: 0.3~0.9` (이미지에서 대충 추정).

가로 bar 면 `layout: vertical` + `fillDirection: horizontal`.

#### (2) 배경 씬 + 전경 UI 겹침

레퍼런스 큰 박스 내부에 **장식 배경(벽지/광산/하늘 등)** 이 깔려있고 그 위에 다른 UI (게이지·버튼·카드) 가 올라가 있으면:

- 부모 컨테이너(layout 없음 또는 vertical, 자식들이 각자 overlay 로 배치)
- 배경: `overlay: true, anchor: stretch` + `box: true` + (sprite 있으면 지정)
- 중앙·상단 요소 (게이지 등): `overlay: true, anchor: top` + `preferredWidth/Height`
- 하단 요소 (카드·버튼): `overlay: true, anchor: bottom` + `preferredWidth/Height`
- 모서리 요소 (코인 표시 등): `overlay: true, anchor: topRight` + `offsetX: -20, offsetY: -20`

#### (3) 캐릭터/아바타 포트레이트 영역

큰 박스 안에 중앙 근처에 **캐릭터·몬스터·아이콘** 이 있으면 이름을 `CharacterArea` / `Portrait` 로. `box: true` + 레퍼런스 이미지가 있으면 sprite 지정. 없으면 빈 box (유저가 수동 배정).

#### (4) 말풍선 (Speech Bubble)

꼬리(tail) 있는 둥근 사각형 + 텍스트 → 단순화해서 `box: true` + `text` 필드로 처리. 꼬리는 포기 (LayoutGroup 으로 표현 불가).

#### (5) 주의: 절대 놓치지 말 것

- 우상단/우하단 **작은 아이콘 + 숫자** (골드 / 다이아 / 하트 등) → 무조건 overlay 로 코너 anchor 사용
- **카드 그리드** (lv.1 / icon / % / label 같은 4단 세로) → 각 카드는 vertical layout 에 4 자식
- 하단 가로 **액션 버튼 영역** (확인 / 취소 / 최대 등) → 부모 horizontal + 각 자식 flexibleWidth:1

### 3.7. 레이아웃 최적화 — LayoutGroup 은 필요한 곳에만

매 노드마다 layout 을 넣지 말 것. LayoutGroup 은 매 프레임 rebuild cost + 중첩 depth 가 늘수록 성능·관리 복잡도 증가.

**layout 을 넣어야 하는 경우 (O)**
- 자식이 **2개 이상** 이고 서로 간격(spacing)·정렬이 필요
- 자식 크기가 **가변** (텍스트 길이에 따라 바뀜 등)
- 자식이 **반복 요소** (리스트 셀, 스크롤 아이템)

**layout 을 넣지 말 것 (X)**
- 자식이 **1개** 뿐 → layout 불필요. 자식이 부모 전체를 채우려면 자식 노드는 그냥 `box: true` + 필드 없음 (TreeGenerator 가 auto-fit 함). 또는 overlay + stretch.
- 자식이 **고정 위치 + 고정 크기** 여러 개 → overlay + anchor 로 각자 배치 (layout 없는 부모)
- 부모가 **단순 배경/아웃라인 박스** 역할만 하고 텍스트 하나 품음 → box + text 만, layout 생략 (Txt 자동 중앙 stretch)

**중첩 한계**
- layout 중첩 깊이 **최대 3-4단**. 그 이상이면 해당 섹션을 overlay + anchor 로 평탄화.
- 같은 방향 layout 을 2중으로 감싸는 건 금지 (vertical 안에 vertical 하나만 있는 경우 등) — 상위를 없애거나 하위를 없애기.

**예시**
- Title 하나만 있는 TitleBar: `{ ""name"": ""TitleBar"", ""box"": true, ""preferredHeight"": 110, ""text"": ""제목"", ""textAlign"": ""Center"", ""fontSize"": 50 }` — **layout 불필요**.
- TitleBar + 우측 닫기 버튼: `layout: horizontal` + 2 children. O.
- 3×3 동일 크기 셀: `scroll: true` + `scrollLayout: grid`. 반복문 손으로 vertical+horizontal 조합 금지.

### 4. 스타일 (Wireframe / Light / Dark)

- Wireframe: 흰 배경 + 검정 outline + 채움 거의 없음 → `box: true` 노드의 `color` 기본 `#FFFFFFFF`, `outlineColor` 기본 `#111111FF`.
- Light/Dark: 이미지에서 실제 색 추출.

### 4.5. 레퍼런스 자산 우선 사용

manifest 의 `referencePrefabs` 가 비어있지 않으면 **새로 box+button+layout 을 만들지 말고 해당 프리팹을 `nestedPrefab` 으로 참조**:

- 각 referencePrefabs 항목의 `kind` 를 레퍼런스 이미지 요소와 매칭:
  - `kind: ""button""` → 이미지의 버튼 위치에 배치
  - `kind: ""cell""` → 스크롤/리스트 아이템 자리에 배치
  - `kind: ""layout""` → 그룹 컨테이너 대체
  - `kind: ""scroll""` → 스크롤 영역 통째로 대체 가능
- 예: referencePrefabs 에 `{path:""Assets/A_Prefabs/UI/Button/BTN_Confirm.prefab"", kind:""button"", children:[IMG_BG, TXT_Label]}` 가 있고 이미지에 Confirm 버튼이 있으면 → `{ ""name"": ""BtnConfirm"", ""nestedPrefab"": ""Assets/A_Prefabs/UI/Button/BTN_Confirm.prefab"", ""preferredHeight"": 150 }`
- `nestedPrefab` 이 지정된 노드에는 `box/button/text/layout/children` 지정 금지 (프리팹 내부 구조 유지).
- `children` 메타로 **내부 구조가 이미지의 요소와 맞는지** 확인. 예를 들어 이미지 버튼에 아이콘+텍스트가 있는데 referencePrefab 에 IMG 자식만 있으면 부적합 → 다른 프리팹 또는 새로 구성.
- 유사 프리팹이 없으면 기존 방식대로 box+button+text 직접 구성.

manifest 에 `referenceFont` 가 비어있지 않으면 **모든 텍스트 노드의 `fontPath` 를 그 경로로** 설정 (textColor/fontSize 는 별도 판단).

### 5. 스크롤

`scroll: true` 만 지정하면 TreeGenerator 가 Viewport/Content/ScrollRect/RectMask2D 를 자동 생성.

**`scrollLayout` 판별** (Content 내부 아이템 배치 방식):
- 레퍼런스의 스크롤 영역에 가로 방향으로 아이템이 반복되면 `""horizontal""`
- 세로 방향으로 카드·리스트 셀이 쌓이면 `""vertical""` (기본값)
- 격자로 썸네일·셀이 배열되면 `""grid""` + `scrollCellWidth`, `scrollCellHeight` 지정
- 판별 불가능하면 기본 `""vertical""` 사용

`scroll: true` 노드에 children 을 넣으면 Viewport/Content 내부에 배치된다. **userHint 에 특정 요소를 Scroll 안에 넣으라는 지시가 있으면 해당 요소들을 scroll 의 children 으로 이관**하고 원래 위치에서 제거. 예: userHint 가 ""@Header 를 @Scroll 안에 포함"" → Header 노드를 Scroll 의 children 으로 옮김.

### 6. Text 와 Box 동시 지정 가능

`box: true` + `text: ""Title""` → TreeGenerator 가 부모엔 Image+Outline, 자식 `Txt` 에 TMP 자동 부착.

### 7. Button

`button: true` → Button 컴포넌트 부착. `box: true` 와 함께 쓰면 box 가 버튼 배경이 됨.

### 8. ""Frame"" 은 고정 이름 아님

루트 자식 1 개일 수도 N 개일 수도 있음. 편의로 `MainFrame`·`BottomText` 같은 이름을 쓰되, 상위 레이아웃의 자식이 될 수 있음을 염두 (예: 3 개 top-level rect 면 루트가 컨테이너 역할).

## 예제 A — 외곽 래핑이 **있는** 경우

레퍼런스: **큰 외곽 rounded rect** 하나가 title·TEXT+BTN header·SCROLL·BTN+BTN 를 전부 감싸고 있고, **그 바깥 아래** 에 별도 TEXT 박스.

→ root 에는 2 개 children (OuterPopup, BottomText). Title 은 OuterPopup 의 자식이지 root 직속 아님.

```json
{
  ""outputPath"": ""Assets/A_Prefabs/A_Temp/PopupList.prefab"",
  ""size"": [1080, 1920],
  ""root"": {
    ""name"": ""PopupList"",
    ""layout"": ""vertical"",
    ""spacing"": 30,
    ""padding"": [30, 30, 30, 30],
    ""children"": [
      {
        ""name"": ""OuterPopup"",
        ""box"": true,
        ""flexibleHeight"": 1,
        ""layout"": ""vertical"",
        ""spacing"": 20,
        ""padding"": [30, 30, 30, 30],
        ""children"": [
          { ""name"": ""Title"", ""box"": true, ""preferredHeight"": 150,
            ""text"": ""title"", ""textAlign"": ""Center"", ""fontSize"": 64 },
          {
            ""name"": ""Header"", ""box"": true, ""preferredHeight"": 150,
            ""layout"": ""horizontal"", ""padding"": [30, 30, 15, 15], ""spacing"": 20,
            ""children"": [
              { ""name"": ""Txt"", ""flexibleWidth"": 1, ""text"": ""TEXT"", ""textAlign"": ""MidlineLeft"", ""fontSize"": 48 },
              { ""name"": ""Btn"", ""box"": true, ""button"": true, ""preferredWidth"": 250, ""text"": ""BTN"", ""textAlign"": ""Center"", ""fontSize"": 40 }
            ]
          },
          { ""name"": ""Scroll"", ""box"": true, ""scroll"": true, ""flexibleHeight"": 1 },
          {
            ""name"": ""BtnGroup"", ""preferredHeight"": 150,
            ""layout"": ""horizontal"", ""spacing"": 20,
            ""children"": [
              { ""name"": ""BtnLeft"",  ""box"": true, ""button"": true, ""flexibleWidth"": 1, ""text"": ""BTN"", ""textAlign"": ""Center"", ""fontSize"": 48 },
              { ""name"": ""BtnRight"", ""box"": true, ""button"": true, ""flexibleWidth"": 1, ""text"": ""BTN"", ""textAlign"": ""Center"", ""fontSize"": 48 }
            ]
          }
        ]
      },
      {
        ""name"": ""BottomText"",
        ""box"": true,
        ""preferredHeight"": 130,
        ""text"": ""TEXT"",
        ""textAlign"": ""Center"",
        ""fontSize"": 56
      }
    ]
  }
}
```

## 예제 B — 외곽 래핑이 **없는** 경우 (drop the OuterPopup)

레퍼런스: Title·Main·BottomText 3 개 박스가 **서로 감싸지 않고** 나란히 배치.

→ root 에 3 개 children 직접.

```json
{
  ""outputPath"": ""Assets/A_Prefabs/A_Temp/PopupList.prefab"",
  ""size"": [1080, 1920],
  ""root"": {
    ""name"": ""PopupList"",
    ""layout"": ""vertical"",
    ""spacing"": 30,
    ""padding"": [30, 30, 30, 30],
    ""children"": [
      {
        ""name"": ""Title"",
        ""box"": true,
        ""preferredHeight"": 150,
        ""text"": ""title"",
        ""textAlign"": ""Center"",
        ""fontSize"": 64
      },
      {
        ""name"": ""MainFrame"",
        ""box"": true,
        ""flexibleHeight"": 1,
        ""layout"": ""vertical"",
        ""spacing"": 20,
        ""padding"": [25, 25, 25, 25],
        ""children"": [
          {
            ""name"": ""Header"",
            ""box"": true,
            ""preferredHeight"": 150,
            ""layout"": ""horizontal"",
            ""padding"": [30, 30, 15, 15],
            ""spacing"": 20,
            ""children"": [
              { ""name"": ""Txt"", ""flexibleWidth"": 1, ""text"": ""TEXT"", ""textAlign"": ""MidlineLeft"", ""fontSize"": 48 },
              { ""name"": ""Btn"", ""box"": true, ""button"": true, ""preferredWidth"": 250, ""text"": ""BTN"", ""textAlign"": ""Center"", ""fontSize"": 40 }
            ]
          },
          { ""name"": ""Scroll"", ""box"": true, ""scroll"": true, ""flexibleHeight"": 1 },
          {
            ""name"": ""BtnGroup"",
            ""preferredHeight"": 150,
            ""layout"": ""horizontal"",
            ""spacing"": 20,
            ""children"": [
              { ""name"": ""BtnLeft"",  ""box"": true, ""button"": true, ""flexibleWidth"": 1, ""text"": ""BTN"", ""textAlign"": ""Center"", ""fontSize"": 48 },
              { ""name"": ""BtnRight"", ""box"": true, ""button"": true, ""flexibleWidth"": 1, ""text"": ""BTN"", ""textAlign"": ""Center"", ""fontSize"": 48 }
            ]
          }
        ]
      },
      {
        ""name"": ""BottomText"",
        ""box"": true,
        ""preferredHeight"": 130,
        ""text"": ""TEXT"",
        ""textAlign"": ""Center"",
        ""fontSize"": 56
      }
    ]
  }
}
```

## 자체 검증 체크리스트 (출력 직전 반드시 확인)

출력 JSON 완성 후 전송 전에 다음을 자문. 하나라도 ""아니오"" 면 트리를 다시 짜라.

1. **최외곽 rect 확인** — 이미지에 ""여러 요소를 감싸는 큰 rect"" 가 있나? 있으면 해당 rect 가 root 의 자식이고, 그 내부 요소들은 **그 rect 의 자식** 이어야 한다. root 의 자식으로 직접 올린 건 아닌가?
2. **root 자식 개수** — 레퍼런스에서 **서로 감싸지 않는** 최상위 rect 개수와 일치하나? 초과하면 어떤 요소가 래핑을 무시한 것.
3. **모든 Leaf 에 가장 작은 감싸는 박스의 자식** — 텍스트·버튼이 ""가장 가까운 상위 rect"" 의 자식인가?
4. **절대 좌표 없음** — x, y, width, height, sizeDelta 필드 사용 안 했는지?
5. **fenced JSON 블록 하나** — ```json ... ``` 가 정확히 1개인지?

## 금지 사항

- **훅 호출 금지** (`node ../UniMCP/Tools~/prefab_hook.js` 쓰지 말 것)
- **파일 생성·수정 금지** (manifest 도 건드리지 말 것)
- **JSON 외 텍스트 출력 금지** (""분석 결과:"" 같은 서두, 후기 요약 금지)
- **절대 좌표·sizeDelta 필드 금지** (x, y, width, height 사용 불가. 오로지 preferredSize/flexibleSize/layout)
- **JSON 여러 덩어리 금지** — 정확히 하나의 fenced 블록
";

        private const string UiConventionPrompt = @"프리팹 파일명·루트 GameObject 이름·자식 GameObject 이름을 프로젝트 UI 네이밍 컨벤션에 맞게 일괄 적용한다.

## 실행 방식

**각 타겟 프리팹마다 정확히 한 번씩 호출:**

```bash
node ../UniMCP/Tools~/apply_convention.js <prefab_path>
```

이 도구가 내부에서 전부 처리:
- 루트 GameObject 이름 교체 (Popup*View → 접미사 제거)
- 파일명 + `.meta` 리네임
- 직접 자식 GameObject 이름 규칙 적용 (Image→IMG_, Button→BTN_, Text→TXT_, Cell*→클래스명)
- Nested 프리팹 인스턴스 이름 (부모 프리팹의 m_Modifications override 로; 원본 프리팹은 건드리지 않음)
- PascalCase 정규화 + 기존 접두사(UI_/Text_/Img_/Btn_ 등) 제거

## 출력 포맷

도구는 JSON 리포트를 stdout 에 출력. 스킬은 이 JSON 을 **그대로** 사용자에게 전달하고 추가 분석·작업 없이 종료한다.

```json
{
  ""prefab"": ""..."",
  ""root"": {""before"":""Foo"",""after"":""FooBar"",""applied"":true,""reason"":""...""},
  ""children"": [{""before"":"""",""after"":"""",""applied"":true,""reason"":""...""}],
  ""nested"":   [{""before"":"""",""after"":"""",""applied"":true,""reason"":""...""}],
  ""file_renamed"": {""from"":""..."",""to"":""...""}
}
```

## 금지 사항

- 도구 호출 전·후로 `Read`/`Grep`/`Glob`/`Edit`/`Bash` 로 추가 탐색 금지 (도구 결과를 신뢰)
- 수작업 분석·""권장합니다""·""다음 단계"" 같은 보고 금지 — 도구 output 그대로 전달
- 새 `.cs`/`.py`/`.sh` 파일 생성 금지
- 원본 프리팹 파일 편집 금지 (nested 는 override 로만)

## 거부 조건

- Play Mode 중이면 거부
- 입력이 `.prefab` 아니면 스킵
";

        private const string PrefabReviewPrompt = @"너는 생성된 Unity UI 프리팹이 레퍼런스 이미지와 얼마나 일치하는지 검토하고 **크기·간격·위치 수치만 보정** 하는 에이전트다. 구조(name/layout/children)는 절대 건드리지 않는다.

## 입력 (Targets)

- **Target 1**: 레퍼런스 이미지 경로 (원본)
- **Target 2**: 생성된 프리팹 스크린샷 경로 (현재 결과)
- **Target 3**: 현재 TreeSpec JSON 파일 경로 (수정 대상)
- **Target 4** (옵션): manifest JSON 파일. `userHint` 필드가 있으면 유저가 직접 지시한 내용 → **최우선 반영**. 예: ""GaugeArea 를 더 크게"", ""@Header 높이 1.5배"", ""하단 버튼 영역을 줄여"". 이런 힌트는 단순 크기 비교 결과보다 우선.

## 절차 (순서 엄수)

### Step 0. userHint 읽기

Target 4 의 manifest 가 있으면 `userHint` 를 확인. 비어있지 않으면 이 지시사항을 patches 에 **반드시** 반영 (해당 노드 크기·offset 조정). 이미지 비교는 보조 역할.

### Step 1. TreeSpec 읽기

Target 3 의 JSON 을 읽고 트리 구조 + 각 노드의 현재 크기 값을 파악한다.

### Step 2. 정량 측정 표 작성 (머리속)

레퍼런스 이미지 vs 스크린샷의 **같은 섹션** 을 찾아 각각 **부모 대비 높이/너비 비율** 을 추정한다:

| 섹션 path | 부모 | 레퍼런스 비율 | 스크린샷 비율 | 차이 |
|-----------|------|--------------|--------------|------|
| TitleBar | root | 6% | 10% | -4%p (너무 큼) |
| TopPanel | root | 55% | 45% | +10%p (너무 작음) |
| TopPanel/Portrait | TopPanel | 50% | 30% | +20%p (훨씬 작음) |
| BottomPanel | root | 34% | 40% | -6%p |

**차이가 큰 순서로 정렬** 해 가장 큰 것부터 수정 계획. 루트 size 자체가 이상하면 그것부터.

### Step 3. 보정값 계산

차이 비율 × 부모 크기 = 보정량.
- 레퍼런스 55% vs 현재 45% → Target preferredHeight = 부모 × 0.55. 부모가 1920 이면 1056.
- `flexibleHeight: 1` 인 노드는 직접 수정 대상 아님 — 다른 sibling 의 preferredHeight 를 조정해 결과적으로 이 노드 크기가 바뀌게 한다.

### Step 4. 텍스트 크기

텍스트가 레퍼런스 대비 작아 보이면 fontSize 올림 (대략 ×1.2~1.5). 너무 크면 줄임. 기준: 텍스트 시각 높이 ≈ fontSize × 1.3.

### Step 5. 위치 보정 (overlay 노드)

`overlay: true` 노드가 엉뚱한 위치면 `anchor`/`offsetX`/`offsetY`/`preferredWidth`/`preferredHeight` 보정. anchor 자체는 **오직 제자리에 있는데 margin 만 틀릴 때만** 그대로 두고, 아예 모서리가 바뀌어야 하면 anchor 도 수정 허용 (아래 허용 필드 참조).

### Step 6. Top-down 우선순위

큰 차이(≥10%p) → 중간(5~10%p) → 작음(3~5%p) 순. 3%p 미만 차이는 **건드리지 말 것**.

루트 size → top-level children → 내부 children 순으로 top-down 수정. 상위가 틀리면 아래도 다 틀려 보이므로.

## 출력 (**패치 배열만** — 전체 TreeSpec 다시 출력 금지)

**정확히 fenced JSON 블록 하나만 출력.**

```json
{
  ""status"": ""ok"" | ""needs_fix"" | ""structural_mismatch"",
  ""summary"": ""어떤 섹션을 어떻게 보정했는지 한국어 1-2 문장."",
  ""patches"": [
    { ""path"": ""TopPanel"", ""preferredHeight"": 1056, ""flexibleHeight"": -1 },
    { ""path"": ""TopPanel/Portrait"", ""flexibleHeight"": 1 },
    { ""path"": ""BottomPanel"", ""preferredHeight"": 670 },
    { ""path"": ""BottomPanel/Currency"", ""offsetX"": -30, ""offsetY"": -30, ""preferredWidth"": 300 },
    { ""path"": """", ""size"": [1080, 1920] }
  ]
}
```

- `status: ""ok""` → 모든 섹션이 5%p 이내 일치. `patches: []` 로 반복 종료.
- `status: ""needs_fix""` → `patches` 에 변경할 노드 + 필드.
- `status: ""structural_mismatch""` → 스크린샷이 레퍼런스와 **구조 수준으로 다름** (있어야 할 섹션이 없거나, 레이아웃 방향이 반대). patches 비우고 summary 에 ""Analyzer 재실행 필요 — [이유]"". Reviewer 는 구조 변경 불가.

### patch 규칙

- **path**: 루트 이름 제외한 슬래시(`/`) 경로. 예: `TopPanel/Header`. 루트 자체 수정은 `path: """"`.
- **허용 필드**: `preferredHeight`, `preferredWidth`, `flexibleHeight`, `flexibleWidth`, `spacing`, `padding`, `fontSize`, `offsetX`, `offsetY`, `size`(루트만).
  - `flexibleHeight`/`flexibleWidth` 를 끄고 preferredHeight 로 바꾸려면 `{""flexibleHeight"": -1, ""preferredHeight"": 300}` 같이 함께.
- **변경 금지**: `name`, `layout`, `box`, `button`, `scroll`, `overlay`, `anchor`, `text`, `textAlign`, `color`, `sprite`, `children` 구조/순서. (anchor 는 예외적으로 허용되나 최후수단 — 구조 수준 재배치는 structural_mismatch 로 분류)
- 변경 없는 노드는 patches 에 포함 금지.

## 금지 사항

- 훅 호출 금지. 파일 생성·수정 금지 (Target 3 도 직접 쓰지 말 것 — patches 만 출력)
- JSON 외 텍스트 금지 (분석 과정·주석 출력 금지)
- 구조 변경 제안 금지 (""이 섹션을 빼야 합니다"" → status: structural_mismatch 로 분류)
- 전체 TreeSpec 재출력 금지 (patches 만)

## 비교 태스크 팁

- 두 이미지를 나란히 세로로 놓고 같은 y 좌표의 요소끼리 비교. ""A 가 B 보다 위/아래"" 상대 판단은 vision 이 잘 한다.
- 픽셀 정확도는 못 재도 ""이 섹션이 전체의 1/3 인지 1/2 인지"" 같은 대략 비율은 안정적.
- 가장 큰 불일치 (영역이 반토막/두배) 부터 잡고, 자잘한 ±5% 는 무시.
- overlay 노드는 보통 anchor 는 맞고 offset 만 틀림 — 코너에 박혀있는지 위치만 확인.
- 3% 미만 차이는 ""ok"" 로 일괄 처리 — iteration 비용 아낌.
- userHint 는 이미지 분석보다 우선. 유저가 ""크게"" 라고 하면 ±50% 수준으로 바꿔라.
";
    }
}
