#!/usr/bin/env node
/**
 * Unity 프리팹 YAML 분석기. UniMCP 공용 도구.
 *
 * 사용:
 *     node <path-to-UniMCP>/Tools~/analyze_prefab.js <path/to/file.prefab>
 *
 * 출력(JSON):
 *   {
 *     "root_name": "...",
 *     "root_components": [...],
 *     "children": [{fileID, name, components, is_nested, nested_instance_id}],
 *     "nested_prefabs": [{instance_id, name, source_guid, source_path}]
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

/**
 * 재귀 순회하며 특정 확장자 파일 수집. Unity 관리 폴더·VCS 는 스킵.
 */
function walk(dir, matcher, out) {
    let entries;
    try {
        entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (e) {
        return;
    }

    for (const ent of entries) {
        const full = path.join(dir, ent.name);

        if (ent.isDirectory()) {
            if (SKIP_DIRS.has(ent.name)) continue;
            walk(full, matcher, out);
        } else if (matcher(ent.name)) {
            out.push(full);
        }
    }
}

function buildGuidToClassMap(projectRoot) {
    const mapping = {};
    const metas = [];
    walk(projectRoot, n => n.endsWith('.cs.meta'), metas);

    for (const meta of metas) {
        try {
            const csPath = meta.slice(0, -5); // strip ".meta"
            if (!fs.existsSync(csPath)) continue;

            const metaContent = fs.readFileSync(meta, 'utf-8');
            const guidMatch = metaContent.match(/guid:\s*([0-9a-f]+)/);
            if (!guidMatch) continue;

            const csText = fs.readFileSync(csPath, 'utf-8');
            const classMatch = csText.match(/(?:public|internal)\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)/);
            if (classMatch) {
                mapping[guidMatch[1]] = classMatch[1];
            }
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
            const metaContent = fs.readFileSync(meta, 'utf-8');
            const guidMatch = metaContent.match(/guid:\s*([0-9a-f]+)/);
            if (guidMatch) {
                let prefabPath = meta.slice(0, -5).replace(/\\/g, '/');
                const idx = prefabPath.indexOf('Assets/');
                if (idx >= 0) prefabPath = prefabPath.slice(idx);
                mapping[guidMatch[1]] = prefabPath;
            }
        } catch {}
    }

    return mapping;
}

function parsePrefab(prefabText, guidToClass, prefabGuidToPath) {
    const blocks = prefabText.split(/^--- !u!(\d+) &(-?\d+).*$/m);

    const gameobjects = {};
    const components = {};
    const prefabInstances = {};
    const goToPrefabInstance = {};
    const transformToGo = {};          // Transform/RectTransform fileID → GameObject fileID
    const transformHasNoParent = {};   // Transform fileID → bool (m_Father == 0)

    for (let i = 1; i < blocks.length; i += 3) {
        const classId = blocks[i];
        const fileId = blocks[i + 1];
        const body = blocks[i + 2];

        if (classId === '1') {
            const nameMatch = body.match(/m_Name:\s*(.+)/);
            const compMatches = [...body.matchAll(/component:\s*\{fileID:\s*(-?\d+)\}/g)];
            const name = nameMatch ? nameMatch[1].trim() : '?';
            gameobjects[fileId] = { name, componentIds: compMatches.map(m => m[1]) };

            const prefabInstMatch = body.match(/m_PrefabInstance:\s*\{fileID:\s*(-?\d+)\}/);
            if (prefabInstMatch && prefabInstMatch[1] !== '0') {
                goToPrefabInstance[fileId] = prefabInstMatch[1];
            }
        } else if (classId === '4' || classId === '224') {
            // Transform 또는 RectTransform: GameObject 참조 + m_Father 확인
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
            let className = null;

            if (guidMatch) {
                const guid = guidMatch[1];
                className = guidToClass[guid] || BUILTIN_SCRIPT_GUIDS[guid] || `Script(${guid.slice(0, 8)})`;
            }

            if (goMatch) {
                components[fileId] = { type: className || 'MonoBehaviour', goId: goMatch[1] };
            }
        } else if (classId === '1001') {
            const srcMatch = body.match(/m_SourcePrefab:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]+)/);
            const nameMatch = body.match(/propertyPath:\s*m_Name\s*\n\s+value:\s*(.+)/);
            const instName = nameMatch ? nameMatch[1].trim() : null;
            if (srcMatch) {
                prefabInstances[fileId] = {
                    source_guid: srcMatch[1],
                    instance_name_override: instName,
                };
            }
        }
    }

    // 진짜 루트: Transform/RectTransform 의 m_Father == 0 인 GameObject
    let rootGoId = null;
    for (const [tfId, isRoot] of Object.entries(transformHasNoParent)) {
        if (!isRoot) continue;
        const goId = transformToGo[tfId];
        // nested 프리팹 루트 transform 도 m_Father=0 일 수 있으니 nested 는 제외
        if (goId && !(goId in goToPrefabInstance)) {
            rootGoId = goId;
            break;
        }
    }
    if (!rootGoId) rootGoId = Object.keys(gameobjects)[0] || null;

    const rootInfo = {};

    if (rootGoId && gameobjects[rootGoId]) {
        rootInfo.root_name = gameobjects[rootGoId].name;
        rootInfo.root_components = gameobjects[rootGoId].componentIds
            .filter(c => components[c] && components[c].type !== 'RectTransform')
            .map(c => components[c].type);
    }

    const children = [];
    for (const [goId, go] of Object.entries(gameobjects)) {
        if (goId === rootGoId) continue;

        const isNested = goId in goToPrefabInstance;
        const instId = goToPrefabInstance[goId] || null;

        const compTypes = go.componentIds
            .filter(c => components[c] && components[c].type !== 'RectTransform')
            .map(c => components[c].type);

        children.push({
            fileID: goId,
            name: go.name,
            components: compTypes,
            is_nested: isNested,
            nested_instance_id: instId,
        });
    }

    const nestedList = [];
    for (const [instId, inst] of Object.entries(prefabInstances)) {
        const srcPath = prefabGuidToPath[inst.source_guid] || '';
        let sourceRootFileId = null;
        let sourceRootComponents = [];
        let sourceRootName = null;

        if (srcPath && _projectRoot) {
            const absSrc = path.join(_projectRoot, srcPath);

            if (fs.existsSync(absSrc)) {
                try {
                    const srcText = fs.readFileSync(absSrc, 'utf-8');
                    const srcRoot = parseSourceRoot(srcText, guidToClass);

                    if (srcRoot) {
                        sourceRootFileId = srcRoot.fileId;
                        sourceRootComponents = srcRoot.components;
                        sourceRootName = srcRoot.name;
                    }
                } catch {}
            }
        }

        const nameGuess = inst.instance_name_override
            || sourceRootName
            || (srcPath ? path.basename(srcPath, '.prefab') : '?');

        nestedList.push({
            instance_id: instId,
            name: nameGuess,
            source_guid: inst.source_guid,
            source_path: srcPath,
            source_root_file_id: sourceRootFileId,
            source_root_components: sourceRootComponents,
            has_name_override: inst.instance_name_override !== null && inst.instance_name_override !== undefined,
        });
    }

    return { ...rootInfo, children, nested_prefabs: nestedList };
}

let _projectRoot = null;

function parseSourceRoot(prefabText, guidToClass) {
    const blocks = prefabText.split(/^--- !u!(\d+) &(-?\d+).*$/m);
    const gameobjects = {};
    const components = {};
    const rootCandidates = [];

    for (let i = 1; i < blocks.length; i += 3) {
        const classId = blocks[i];
        const fileId = blocks[i + 1];
        const body = blocks[i + 2];

        if (classId === '1') {
            const nameMatch = body.match(/m_Name:\s*(.+)/);
            const compMatches = [...body.matchAll(/component:\s*\{fileID:\s*(-?\d+)\}/g)];
            gameobjects[fileId] = {
                name: nameMatch ? nameMatch[1].trim() : '?',
                componentIds: compMatches.map(m => m[1]),
            };
            if (body.includes('m_CorrespondingSourceObject: {fileID: 0}')) {
                rootCandidates.push(fileId);
            }
        } else if (classId === '114') {
            const goMatch = body.match(/m_GameObject:\s*\{fileID:\s*(-?\d+)\}/);
            const guidMatch = body.match(/m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]+)/);
            let className = null;
            if (guidMatch) {
                const guid = guidMatch[1];
                className = guidToClass[guid] || BUILTIN_SCRIPT_GUIDS[guid] || `Script(${guid.slice(0, 8)})`;
            }
            if (goMatch) {
                components[fileId] = { type: className || 'MonoBehaviour', goId: goMatch[1] };
            }
        } else if (classId === '224') {
            components[fileId] = { type: 'RectTransform', goId: null };
        }
    }

    const rootGoId = rootCandidates[0] || Object.keys(gameobjects)[0];

    if (!rootGoId || !gameobjects[rootGoId]) return null;

    const go = gameobjects[rootGoId];
    const compTypes = go.componentIds
        .filter(c => components[c] && components[c].type !== 'RectTransform')
        .map(c => components[c].type);

    return { fileId: rootGoId, name: go.name, components: compTypes };
}

function findProjectRoot(prefabPath) {
    let cur = path.resolve(prefabPath);
    while (cur !== path.dirname(cur)) {
        if (path.basename(cur) === 'Assets') {
            return path.dirname(cur);
        }
        cur = path.dirname(cur);
    }
    return path.dirname(path.resolve(prefabPath));
}

function main() {
    if (process.argv.length < 3) {
        console.error('usage: node analyze_prefab.js <path>');
        process.exit(1);
    }

    const prefabPath = process.argv[2];

    if (!fs.existsSync(prefabPath)) {
        console.error(`not found: ${prefabPath}`);
        process.exit(2);
    }

    const projectRoot = findProjectRoot(prefabPath);
    _projectRoot = projectRoot;
    const guidToClass = buildGuidToClassMap(projectRoot);
    const prefabGuidToPath = buildPrefabGuidToPath(projectRoot);
    const prefabText = fs.readFileSync(prefabPath, 'utf-8');
    const result = parsePrefab(prefabText, guidToClass, prefabGuidToPath);

    console.log(JSON.stringify(result, null, 2));
}

main();
