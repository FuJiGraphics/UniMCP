#!/usr/bin/env node
/**
 * 프리팹 하나에 UI 컨벤션을 일괄 적용하는 단일 도구.
 * 분석 → 규칙 적용 → 직접 자식 m_Name 교체 → nested m_Modifications override → 파일명 리네임까지 전부 처리.
 *
 * 사용:
 *   node apply_convention.js <prefab_path>
 *
 * 출력(JSON):
 *   {
 *     "prefab": "Assets/...",
 *     "root": {"before":"Foo","after":"FooBar","applied":true},
 *     "children": [{before, after, applied, reason}],
 *     "nested":   [{before, after, applied, reason}]
 *   }
 */

const fs = require('fs');
const path = require('path');

const BUILTIN_SCRIPT_GUIDS = {
    'fe87c0e1cc204ed48ad3b37840f39efc': 'Image',
    '1aa08ab6e0800fa44ae55d278d1423e3': 'ScrollRect',
    '8a8695521f0d02e499659fee002a26c2': 'GridLayoutGroup',
    '59f8146938fff824cb5fd77236b75775': 'VerticalLayoutGroup',
    '3245ec927659c4140ac4f8d17403cc18': 'ContentSizeFitter',
    '3312d7739989d2b4e91e6319e9a96d76': 'RectMask2D',
    '4e29b1a8efbd4b44bb3f3716e73f07ff': 'Button',
    'f4688fdb7df04437aeb418b961361dc5': 'TextMeshProUGUI',
    'ce71017a2903f7c4c9a699e438d0b897': 'LoopVerticalScrollRect',
};

const SKIP_DIRS = new Set(['Library', 'Temp', 'Logs', 'UserSettings', 'obj', 'bin', '.git']);
const STRIP_PREFIXES = /^(UI_|Text_|Img_|Btn_|Txt_|Tmp_|TXT_|IMG_|BTN_)+/i;

function walk(dir, matcher, out) {
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const ent of entries) {
        const full = path.join(dir, ent.name);
        if (ent.isDirectory()) {
            if (SKIP_DIRS.has(ent.name)) continue;
            walk(full, matcher, out);
        } else if (matcher(ent.name)) out.push(full);
    }
}

function buildGuidToClassMap(projectRoot) {
    const mapping = {};
    const metas = [];
    walk(projectRoot, n => n.endsWith('.cs.meta'), metas);
    for (const meta of metas) {
        try {
            const csPath = meta.slice(0, -5);
            if (!fs.existsSync(csPath)) continue;
            const guidMatch = fs.readFileSync(meta, 'utf-8').match(/guid:\s*([0-9a-f]+)/);
            if (!guidMatch) continue;
            const classMatch = fs.readFileSync(csPath, 'utf-8').match(/(?:public|internal)\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)/);
            if (classMatch) mapping[guidMatch[1]] = classMatch[1];
        } catch {}
    }
    return mapping;
}

function buildPrefabGuidToPath(projectRoot) {
    const mapping = {};
    const metas = [];
    walk(projectRoot, n => n.endsWith('.prefab.meta'), metas);
    for (const meta of metas) {
        try {
            const guidMatch = fs.readFileSync(meta, 'utf-8').match(/guid:\s*([0-9a-f]+)/);
            if (!guidMatch) continue;
            let p = meta.slice(0, -5).replace(/\\/g, '/');
            const idx = p.indexOf('Assets/');
            if (idx >= 0) p = p.slice(idx);
            mapping[guidMatch[1]] = p;
        } catch {}
    }
    return mapping;
}

function extractDupSuffix(s) {
    // " (N)" 또는 "_N" 접미사 → { core, suffix } 형태로 분리. suffix 는 언제나 `_N` 으로 정규화
    const paren = s.match(/^(.*?)\s*\((\d+)\)$/);
    if (paren) return { core: paren[1], suffix: '_' + paren[2] };
    const under = s.match(/^(.*?)_(\d+)$/);
    if (under) return { core: under[1], suffix: '_' + under[2] };
    return { core: s, suffix: '' };
}

function pascalCase(s) {
    const { core, suffix } = extractDupSuffix(s);
    const cleaned = core
        .replace(STRIP_PREFIXES, '')
        .split(/[_\s-]+/)
        .filter(Boolean)
        .map(w => w.charAt(0).toUpperCase() + w.slice(1))
        .join('');
    return cleaned + suffix;
}

function buildPrefixed(prefix, currentName, fallback) {
    const core = pascalCase(currentName);

    // 이미 동일 접두사로 시작 + 뒤 잘 되어 있으면 변경 불필요
    if (currentName.startsWith(prefix)) {
        const rest = currentName.slice(prefix.length);
        if (rest && /^[A-Z0-9]/.test(rest)) return currentName;
    }

    const body = core || fallback;
    return prefix + body;
}

function computeNewName(currentName, components) {
    // Cell 계열 스크립트 부착 → 클래스명 + `_N` 접미사(중복이면)
    const cellScript = components.find(c => /^Cell[A-Z]/.test(c));
    if (cellScript) {
        const { suffix } = extractDupSuffix(currentName);
        return { newName: cellScript + suffix, reason: `Cell script: ${cellScript}` };
    }

    if (components.includes('Button')) {
        return { newName: buildPrefixed('BTN_', currentName, 'Button'), reason: 'Button' };
    }

    if (components.includes('Image') || components.includes('RawImage')) {
        return { newName: buildPrefixed('IMG_', currentName, 'Image'), reason: 'Image' };
    }

    if (components.includes('TextMeshProUGUI') || components.includes('TMP_Text')) {
        return { newName: buildPrefixed('TXT_', currentName, 'Text'), reason: 'Text' };
    }

    return { newName: null, reason: 'no matching rule' };
}

function computeRootName(currentName, components) {
    // Popup*View → strip View
    const popup = components.find(c => /^Popup[A-Z]/.test(c));
    if (popup) {
        const stripped = popup.replace(/View$/, '');
        return { newName: stripped, reason: `strip View from ${popup}` };
    }

    const content = components.find(c => /^Content[A-Z]/.test(c));
    if (content) return { newName: content, reason: `Content class` };

    const cell = components.find(c => /^Cell[A-Z]/.test(c));
    if (cell) return { newName: cell, reason: `Cell class` };

    return { newName: null, reason: 'no matching rule for root' };
}

let _projectRoot = null;
let _spriteGuidToName = null;

/**
 * 텍스처/스프라이트 파일명을 guid 로 조회. 의미 없는 Image 이름 추론용
 */
function buildSpriteGuidToName() {
    if (_spriteGuidToName) return _spriteGuidToName;
    _spriteGuidToName = {};

    const metas = [];
    walk(_projectRoot, n => /\.(png|jpg|jpeg|psd|tga|webp)\.meta$/i.test(n), metas);

    for (const meta of metas) {
        try {
            const guidMatch = fs.readFileSync(meta, 'utf-8').match(/guid:\s*([0-9a-f]+)/);
            if (!guidMatch) continue;
            const base = path.basename(meta).replace(/\.meta$/, '');
            const stem = base.replace(/\.(png|jpg|jpeg|psd|tga|webp)$/i, '');
            _spriteGuidToName[guidMatch[1]] = stem;
        } catch {}
    }

    return _spriteGuidToName;
}

/**
 * 주어진 GameObject 가 Image 컴포넌트를 가지면 m_Sprite guid 로 sprite 이름 조회해 반환
 */
function inferNameFromImageSprite(goId, componentIds, rawBlocks, components) {
    for (const compId of componentIds) {
        const comp = components[compId];
        if (!comp || (comp.type !== 'Image' && comp.type !== 'RawImage')) continue;
        const body = rawBlocks[compId];
        if (!body) continue;

        const spriteMatch = body.match(/m_Sprite:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]+)/);
        if (!spriteMatch) continue;

        const name = buildSpriteGuidToName()[spriteMatch[1]];
        if (name) return name;
    }

    return null;
}

/**
 * 이름이 모호한지 판정. 1~4자 짧거나 흔한 단어면 true
 */
function isAmbiguousName(name) {
    const { core } = extractDupSuffix(name);
    const stripped = core.replace(STRIP_PREFIXES, '');
    if (stripped.length <= 4) return true;
    const lower = stripped.toLowerCase();
    const ambiguous = ['on', 'off', 'icon', 'bg', 'img', 'image', 'button', 'btn', 'text', 'txt', 'cell', 'top', 'bottom', 'left', 'right', 'center'];
    return ambiguous.includes(lower);
}

function parseGameObjects(prefabText, guidToClass) {
    const blocks = prefabText.split(/^--- !u!(\d+) &(-?\d+).*$/m);
    const gameobjects = {};
    const components = {};
    const rawBlocks = {}; // fileID → raw body (sprite 조회용)
    const prefabInstances = {};
    const goToPrefabInstance = {};
    const transformToGo = {};
    const transformHasNoParent = {};

    for (let i = 1; i < blocks.length; i += 3) {
        const classId = blocks[i];
        const fileId = blocks[i + 1];
        const body = blocks[i + 2];
        rawBlocks[fileId] = body;

        if (classId === '1') {
            const nameMatch = body.match(/m_Name:\s*(.+)/);
            const compMatches = [...body.matchAll(/component:\s*\{fileID:\s*(-?\d+)\}/g)];
            gameobjects[fileId] = { name: nameMatch ? nameMatch[1].trim() : '?', componentIds: compMatches.map(m => m[1]) };
            const instMatch = body.match(/m_PrefabInstance:\s*\{fileID:\s*(-?\d+)\}/);
            if (instMatch && instMatch[1] !== '0') goToPrefabInstance[fileId] = instMatch[1];
        } else if (classId === '4' || classId === '224') {
            const goMatch = body.match(/m_GameObject:\s*\{fileID:\s*(-?\d+)\}/);
            const fatherMatch = body.match(/m_Father:\s*\{fileID:\s*(-?\d+)\}/);
            components[fileId] = { type: classId === '224' ? 'RectTransform' : 'Transform', goId: goMatch ? goMatch[1] : null };
            if (goMatch) {
                transformToGo[fileId] = goMatch[1];
                transformHasNoParent[fileId] = fatherMatch && fatherMatch[1] === '0';
            }
        } else if (classId === '114') {
            const goMatch = body.match(/m_GameObject:\s*\{fileID:\s*(-?\d+)\}/);
            const guidMatch = body.match(/m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]+)/);
            let name = null;
            if (guidMatch) name = guidToClass[guidMatch[1]] || BUILTIN_SCRIPT_GUIDS[guidMatch[1]] || `Script(${guidMatch[1].slice(0,8)})`;
            if (goMatch) components[fileId] = { type: name || 'MonoBehaviour', goId: goMatch[1] };
        } else if (classId === '1001') {
            const srcMatch = body.match(/m_SourcePrefab:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]+)/);
            const nameMatch = body.match(/propertyPath:\s*m_Name\s*\n\s+value:\s*(.+)/);
            if (srcMatch) prefabInstances[fileId] = {
                source_guid: srcMatch[1],
                instance_name_override: nameMatch ? nameMatch[1].trim() : null,
            };
        }
    }

    let rootGoId = null;
    for (const [tfId, isRoot] of Object.entries(transformHasNoParent)) {
        if (!isRoot) continue;
        const goId = transformToGo[tfId];
        if (goId && !(goId in goToPrefabInstance)) { rootGoId = goId; break; }
    }
    if (!rootGoId) rootGoId = Object.keys(gameobjects)[0];

    return { gameobjects, components, rawBlocks, prefabInstances, goToPrefabInstance, rootGoId };
}

/**
 * source prefab YAML 에서 nested 인스턴스 목록 추출.
 * @returns [{ instance_id, source_guid, instance_name_override }]
 */
function parseSourceNestedInstances(text) {
    const blocks = text.split(/^--- !u!(\d+) &(-?\d+).*$/m);
    const nested = [];

    for (let i = 1; i < blocks.length; i += 3) {
        if (blocks[i] !== '1001') continue;
        const fid = blocks[i + 1];
        const body = blocks[i + 2];
        const srcMatch = body.match(/m_SourcePrefab:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]+)/);
        const nameMatch = body.match(/propertyPath:\s*m_Name\s*\n\s+value:\s*(.+)/);

        if (srcMatch) {
            nested.push({
                instance_id: fid,
                source_guid: srcMatch[1],
                instance_name_override: nameMatch ? nameMatch[1].trim() : null,
            });
        }
    }

    return nested;
}

function parseSourceRoot(text, guidToClass) {
    const blocks = text.split(/^--- !u!(\d+) &(-?\d+).*$/m);
    const gos = {};
    const comps = {};
    const tfToGo = {};
    const tfNoParent = {};

    for (let i = 1; i < blocks.length; i += 3) {
        const cid = blocks[i], fid = blocks[i+1], body = blocks[i+2];
        if (cid === '1') {
            const m = body.match(/m_Name:\s*(.+)/);
            const cs = [...body.matchAll(/component:\s*\{fileID:\s*(-?\d+)\}/g)];
            gos[fid] = { name: m?m[1].trim():'?', componentIds: cs.map(x=>x[1]) };
        } else if (cid === '4' || cid === '224') {
            const go = body.match(/m_GameObject:\s*\{fileID:\s*(-?\d+)\}/);
            const f = body.match(/m_Father:\s*\{fileID:\s*(-?\d+)\}/);
            comps[fid] = { type: cid==='224'?'RectTransform':'Transform', goId: go?go[1]:null };
            if (go) { tfToGo[fid] = go[1]; tfNoParent[fid] = f && f[1]==='0'; }
        } else if (cid === '114') {
            const go = body.match(/m_GameObject:\s*\{fileID:\s*(-?\d+)\}/);
            const g = body.match(/m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]+)/);
            let n = null;
            if (g) n = guidToClass[g[1]] || BUILTIN_SCRIPT_GUIDS[g[1]] || `Script(${g[1].slice(0,8)})`;
            if (go) comps[fid] = { type: n||'MonoBehaviour', goId: go[1] };
        }
    }

    let rootGoId = null;
    for (const [tfId, isRoot] of Object.entries(tfNoParent)) {
        if (isRoot && tfToGo[tfId]) { rootGoId = tfToGo[tfId]; break; }
    }
    if (!rootGoId) rootGoId = Object.keys(gos)[0];
    if (!rootGoId) return null;

    const ct = gos[rootGoId].componentIds
        .filter(c => comps[c] && comps[c].type !== 'RectTransform' && comps[c].type !== 'Transform')
        .map(c => comps[c].type);
    return { fileId: rootGoId, name: gos[rootGoId].name, components: ct };
}

function renameGoInYaml(text, currentName, newName, fileId) {
    // fileID 바로 뒤의 GameObject 블록의 m_Name 을 찾아 교체
    const pattern = new RegExp(
        `(^--- !u!1 &${fileId}\\s*\\n[\\s\\S]*?^  m_Name: )${escapeRegExp(currentName)}(\\s*$)`,
        'm'
    );
    if (!pattern.test(text)) return null;
    return text.replace(pattern, `$1${newName}$2`);
}

function escapeRegExp(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

function applyNestedOverride(text, instanceId, sourceRootFileId, sourceGuid, newName) {
    const blockPattern = new RegExp(
        `(^--- !u!1001 &${escapeRegExp(instanceId)}[\\s\\S]*?)(^--- |$(?![\\s\\S]))`,
        'm'
    );
    const m = blockPattern.exec(text);
    if (!m) return null;
    const block = m[1];
    const rest = m[2] || '';

    const existingPat = new RegExp(
        `(- target: \\{fileID: ${escapeRegExp(sourceRootFileId)}, guid: ${escapeRegExp(sourceGuid)}, type: 3\\}\\s*\\n\\s+propertyPath: m_Name\\s*\\n\\s+value: )[^\\n]*`
    );

    let newBlock;
    if (existingPat.test(block)) {
        newBlock = block.replace(existingPat, `$1${newName}`);
    } else {
        const entry =
            `    - target: {fileID: ${sourceRootFileId}, guid: ${sourceGuid}, type: 3}\n` +
            `      propertyPath: m_Name\n` +
            `      value: ${newName}\n` +
            `      objectReference: {fileID: 0}\n`;
        const markerMatch = block.match(/^(\s*)(m_RemovedComponents|m_RemovedGameObjects|m_AddedGameObjects|m_AddedComponents|m_SourcePrefab):/m);
        if (!markerMatch) return null;
        newBlock = block.slice(0, markerMatch.index) + entry + block.slice(markerMatch.index);
    }
    return text.slice(0, m.index) + newBlock + rest + text.slice(m.index + m[0].length);
}

function findProjectRoot(prefabPath) {
    let cur = path.resolve(prefabPath);
    while (cur !== path.dirname(cur)) {
        if (path.basename(cur) === 'Assets') return path.dirname(cur);
        cur = path.dirname(cur);
    }
    return path.dirname(path.resolve(prefabPath));
}

function main() {
    if (process.argv.length < 3) {
        console.error('usage: node apply_convention.js <prefab_path>');
        process.exit(1);
    }

    const prefabPath = process.argv[2];
    if (!fs.existsSync(prefabPath)) {
        console.error(`not found: ${prefabPath}`);
        process.exit(2);
    }

    _projectRoot = findProjectRoot(prefabPath);
    const guidToClass = buildGuidToClassMap(_projectRoot);
    const prefabGuidToPath = buildPrefabGuidToPath(_projectRoot);

    let text = fs.readFileSync(prefabPath, 'utf-8');
    const parsed = parseGameObjects(text, guidToClass);
    const report = { prefab: prefabPath.replace(/\\/g, '/'), root: null, children: [], nested: [] };

    // 루트
    if (parsed.rootGoId) {
        const go = parsed.gameobjects[parsed.rootGoId];
        const compTypes = go.componentIds
            .map(c => parsed.components[c])
            .filter(c => c && c.type !== 'RectTransform' && c.type !== 'Transform')
            .map(c => c.type);
        const { newName, reason } = computeRootName(go.name, compTypes);
        if (newName && newName !== go.name) {
            const updated = renameGoInYaml(text, go.name, newName, parsed.rootGoId);
            if (updated) {
                text = updated;
                report.root = { before: go.name, after: newName, applied: true, reason };
            } else {
                report.root = { before: go.name, after: newName, applied: false, reason: 'yaml pattern mismatch' };
            }
        } else {
            report.root = { before: go.name, after: newName || go.name, applied: false, reason: reason || 'already convention' };
        }
    }

    // 직접 자식 (nested 아닌 것만)
    for (const [goId, go] of Object.entries(parsed.gameobjects)) {
        if (goId === parsed.rootGoId) continue;
        if (goId in parsed.goToPrefabInstance) continue; // nested 는 아래서 처리

        const compTypes = go.componentIds
            .map(c => parsed.components[c])
            .filter(c => c && c.type !== 'RectTransform' && c.type !== 'Transform')
            .map(c => c.type);

        const { newName, reason } = computeNewName(go.name, compTypes);
        if (newName && newName !== go.name) {
            const updated = renameGoInYaml(text, go.name, newName, goId);
            if (updated) {
                text = updated;
                report.children.push({ before: go.name, after: newName, applied: true, reason });
            } else {
                report.children.push({ before: go.name, after: newName, applied: false, reason: 'yaml pattern mismatch' });
            }
        }
    }

    // N단계 재귀: 각 prefab 에 대해
    //  1) 직접 자식 GameObject 이름 갱신 (source prefab 의 자체 자식 포함)
    //  2) 현재 owner 의 m_Modifications override 값 갱신 (nested 인스턴스 표시 이름)
    //  3) nested 인스턴스의 source 파일로 재귀
    const visited = new Set();

    function processRecursive(ownerAbsPath, ownerText, depth, isRootCall) {
        if (depth > 10) return ownerText;
        if (visited.has(ownerAbsPath)) return ownerText;
        visited.add(ownerAbsPath);

        let mutated = ownerText;

        // 1) 직접 자식 이름 갱신 (루트 호출 시에는 이미 처리됨 — 스킵)
        if (!isRootCall) {
            const ownerParsed = parseGameObjects(mutated, guidToClass);
            for (const [goId, go] of Object.entries(ownerParsed.gameobjects)) {
                if (goId === ownerParsed.rootGoId) continue;
                if (goId in ownerParsed.goToPrefabInstance) continue;

                const compTypes = go.componentIds
                    .map(c => ownerParsed.components[c])
                    .filter(c => c && c.type !== 'RectTransform' && c.type !== 'Transform')
                    .map(c => c.type);

                let { newName, reason } = computeNewName(go.name, compTypes);

                // Image 컴포넌트 + 이름 모호 → sprite 로 추론
                if (newName && (isAmbiguousName(go.name) || isAmbiguousName(newName))) {
                    const spriteName = inferNameFromImageSprite(goId, go.componentIds, ownerParsed.rawBlocks, ownerParsed.components);
                    if (spriteName) {
                        if (compTypes.includes('Image') || compTypes.includes('RawImage')) {
                            newName = 'IMG_' + pascalCase(spriteName);
                            reason = 'Image (sprite-inferred)';
                        } else if (compTypes.includes('Button')) {
                            newName = 'BTN_' + pascalCase(spriteName);
                            reason = 'Button (sprite-inferred)';
                        }
                    }
                }

                if (newName && newName !== go.name) {
                    const updated = renameGoInYaml(mutated, go.name, newName, goId);
                    if (updated) {
                        mutated = updated;
                        report.nested.push({
                            owner: ownerAbsPath.replace(_projectRoot, '').replace(/\\/g, '/').replace(/^\//, ''),
                            depth,
                            kind: 'child',
                            before: go.name,
                            after: newName,
                            applied: true,
                            reason,
                        });
                    }
                }
            }
        }

        // 2) 현재 owner 의 m_Modifications override 갱신
        const innerInstances = parseSourceNestedInstances(mutated);
        for (const inner of innerInstances) {
            const innerSrcRel = prefabGuidToPath[inner.source_guid];
            if (!innerSrcRel) continue;
            const innerAbs = path.join(_projectRoot, innerSrcRel);
            if (!fs.existsSync(innerAbs)) continue;

            const innerText = fs.readFileSync(innerAbs, 'utf-8');
            const innerRoot = parseSourceRoot(innerText, guidToClass);
            if (!innerRoot) continue;

            const currentName = inner.instance_name_override || innerRoot.name;
            const { newName, reason } = computeNewName(currentName, innerRoot.components);

            if (newName && newName !== currentName) {
                const updated = applyNestedOverride(mutated, inner.instance_id, innerRoot.fileId, inner.source_guid, newName);
                if (updated) {
                    mutated = updated;
                    report.nested.push({
                        owner: ownerAbsPath.replace(_projectRoot, '').replace(/\\/g, '/').replace(/^\//, ''),
                        depth,
                        kind: 'override',
                        before: currentName,
                        after: newName,
                        applied: true,
                        reason,
                    });
                }
            }

            // 3) inner source 파일로 재귀
            const processedInner = processRecursive(innerAbs, innerText, depth + 1, false);
            if (processedInner !== innerText) {
                fs.writeFileSync(innerAbs, processedInner, 'utf-8');
            }
        }

        return mutated;
    }

    text = processRecursive(path.resolve(prefabPath), text, 1, true);

    fs.writeFileSync(prefabPath, text, 'utf-8');

    // 파일명이 m_Name 과 불일치하면 파일명 교정 (applied 여부 무관)
    const currentFileBase = path.basename(prefabPath, '.prefab');
    const needFileRename = report.root && report.root.after && currentFileBase !== report.root.after;

    if (needFileRename) {
        const dir = path.dirname(prefabPath);
        const base = report.root.after;
        let finalName = base;
        let targetPath = path.join(dir, finalName + '.prefab');

        if (path.resolve(prefabPath) !== path.resolve(targetPath)) {
            // 사용 가능한 이름 탐색
            let n = 1;
            while (fs.existsSync(targetPath)) {
                finalName = base + '_' + n;
                targetPath = path.join(dir, finalName + '.prefab');
                n++;
                if (n > 99) break;
            }

            try {
                fs.renameSync(prefabPath, targetPath);
                fs.renameSync(prefabPath + '.meta', targetPath + '.meta');
                report.file_renamed = { from: prefabPath, to: targetPath };

                // 접미사가 붙은 경우 루트 m_Name 도 맞춰서 재수정
                if (finalName !== base) {
                    let newText = fs.readFileSync(targetPath, 'utf-8');
                    newText = newText.replace(
                        new RegExp(`(^--- !u!1 &${parsed.rootGoId}\\s*\\n[\\s\\S]*?^  m_Name: )${escapeRegExp(base)}(\\s*$)`, 'm'),
                        `$1${finalName}$2`
                    );
                    fs.writeFileSync(targetPath, newText, 'utf-8');
                    report.root.after = finalName;
                    report.root.reason += ` (suffixed due to collision)`;
                }
            } catch (e) {
                report.file_renamed = { error: e.message };
            }
        }
    }

    console.log(JSON.stringify(report, null, 2));
}

main();
