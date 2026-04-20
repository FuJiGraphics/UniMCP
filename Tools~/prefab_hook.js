#!/usr/bin/env node
/**
 * Unity UI 프리팹 조작 훅 CLI 디스패처.
 * Library/UniMCP/PrefabHook/cmd/<uuid>.json 에 명령 파일을 작성하고
 * Library/UniMCP/PrefabHook/res/<uuid>.json 응답을 폴링해 결과를 stdout 에 출력한다.
 *
 * 사용:
 *   node prefab_hook.js <op> [--key value ...]
 *
 * 예시:
 *   node prefab_hook.js create-prefab --path "Assets/A_Prefabs/A_Popup/PopupShop.prefab"
 *   node prefab_hook.js add-child --parent "" --name "Frame"
 *   node prefab_hook.js add-image --path "Frame" --color "#FFFFFF"
 *   node prefab_hook.js set-rect --path "Frame" --anchor center --x 0 --y 0 --w 800 --h 1200
 *   node prefab_hook.js add-tmp --path "Frame/Title" --text "상점" --fontSize 48
 *   node prefab_hook.js save-prefab
 *
 * 종료 코드: success=0, fail=1, timeout=2
 */

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const NUMERIC_KEYS = new Set([
    'x', 'y', 'z', 'w', 'h',
    'minX', 'minY', 'maxX', 'maxY',
    'pivotX', 'pivotY',
    'fontSize', 'alpha', 'aspectRatio',
    'minWidth', 'minHeight', 'preferredWidth', 'preferredHeight', 'flexibleWidth', 'flexibleHeight',
    'spacing', 'spacingX', 'spacingY',
    'cellW', 'cellH',
    'padLeft', 'padRight', 'padTop', 'padBottom',
    'constraintCount', 'index', 'siblingIndex', 'sortingOrder',
]);

const BOOL_KEYS = new Set([
    'active', 'interactable', 'blocksRaycasts', 'ignoreParentGroups',
    'showMaskGraphic', 'overrideSorting', 'ignoreLayout',
    'childForceExpandWidth', 'childForceExpandHeight',
    'childControlWidth', 'childControlHeight',
    'raycastTarget', 'isOn',
]);

// args 에 해당 키가 명시적으로 들어왔음을 표시하는 플래그 키 매핑 (C# 쪽 hasXxx 필드와 연결)
const HAS_FLAGS = {
    x: 'hasPos', y: 'hasPos',
    w: 'hasSize', h: 'hasSize',
    pivotX: 'hasPivot', pivotY: 'hasPivot',
    fontSize: 'hasFontSize',
    raycastTarget: 'hasRaycast',
    index: 'hasIndex',
    childForceExpandWidth: 'hasForceExpand',
    childForceExpandHeight: 'hasForceExpand',
    childControlWidth: 'hasControl',
    childControlHeight: 'hasControl',
};

function parseArgv(argv) {
    const out = {};
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (!a.startsWith('--')) continue;
        const key = a.slice(2);
        let val = argv[i + 1];
        if (val === undefined || val.startsWith('--')) {
            out[key] = true;
            continue;
        }
        if (NUMERIC_KEYS.has(key)) val = Number(val);
        else if (BOOL_KEYS.has(key)) val = (val === 'true' || val === '1');
        out[key] = val;
        if (HAS_FLAGS[key]) out[HAS_FLAGS[key]] = true;
        i++;
    }
    return out;
}

function findProjectRoot(start) {
    let dir = path.resolve(start);
    for (let i = 0; i < 8; i++) {
        if (fs.existsSync(path.join(dir, 'Assets')) &&
            fs.existsSync(path.join(dir, 'Packages'))) {
            return dir;
        }
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    throw new Error('Unity 프로젝트 루트를 찾지 못함 (Assets/·Packages/ 기준)');
}

async function sleep(ms) {
    return new Promise(r => setTimeout(r, ms));
}

async function invoke(op, args, timeoutSec) {
    const projectRoot = findProjectRoot(process.cwd());
    const hookRoot = path.join(projectRoot, 'Library', 'UniMCP', 'PrefabHook');
    const cmdDir = path.join(hookRoot, 'cmd');
    const resDir = path.join(hookRoot, 'res');
    fs.mkdirSync(cmdDir, { recursive: true });
    fs.mkdirSync(resDir, { recursive: true });

    const id = crypto.randomUUID();
    const cmdFile = path.join(cmdDir, id + '.json');
    const resFile = path.join(resDir, id + '.json');

    const payload = { op, args: JSON.stringify(args) };
    fs.writeFileSync(cmdFile, JSON.stringify(payload));

    const deadline = Date.now() + timeoutSec * 1000;
    while (Date.now() < deadline) {
        if (fs.existsSync(resFile)) {
            const raw = fs.readFileSync(resFile, 'utf-8');
            try { fs.unlinkSync(resFile); } catch {}
            const res = JSON.parse(raw);
            if (res.data) {
                try { res.data = JSON.parse(res.data); } catch {}
            }
            return res;
        }
        await sleep(150);
    }

    try { fs.unlinkSync(cmdFile); } catch {}
    return { success: false, error: `Unity 응답 타임아웃 (${timeoutSec}s)`, _timeout: true };
}

async function main() {
    const [, , op, ...rest] = process.argv;

    if (!op || op === '--help' || op === '-h') {
        process.stderr.write('사용: node prefab_hook.js <op> [--key value ...]\n');
        process.exit(1);
    }

    const parsed = parseArgv(rest);
    const timeoutSec = Number(parsed.timeout) || 30;
    delete parsed.timeout;

    const res = await invoke(op, parsed, timeoutSec);

    process.stdout.write(JSON.stringify(res, null, 2) + '\n');

    if (res._timeout) process.exit(2);
    process.exit(res.success ? 0 : 1);
}

main().catch(e => {
    process.stderr.write(String(e.stack || e.message || e) + '\n');
    process.exit(1);
});
