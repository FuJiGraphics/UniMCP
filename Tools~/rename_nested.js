#!/usr/bin/env node
/**
 * Nested prefab 인스턴스의 m_Name 을 부모 프리팹의 m_Modifications override 로 변경.
 * 원본 프리팹은 건드리지 않는다.
 *
 * 사용:
 *   node rename_nested.js <parent_prefab> <instance_id> <source_root_file_id> <source_guid> <new_name>
 */

const fs = require('fs');

function escapeRegExp(s) {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function rename(parentPath, instanceId, sourceRootFileId, sourceGuid, newName) {
    const text = fs.readFileSync(parentPath, 'utf-8');

    const blockPattern = new RegExp(
        `(^--- !u!1001 &${escapeRegExp(instanceId)}[\\s\\S]*?)(^--- |$(?![\\s\\S]))`,
        'm'
    );
    const match = blockPattern.exec(text);

    if (!match) {
        console.error(`error: PrefabInstance &${instanceId} not found`);
        process.exit(2);
    }

    const block = match[1];
    const rest = match[2] || '';
    const blockStart = match.index;
    const blockEnd = match.index + match[0].length;

    const existingPattern = new RegExp(
        `(- target: \\{fileID: ${escapeRegExp(sourceRootFileId)}, guid: ${escapeRegExp(sourceGuid)}, type: 3\\}\\s*\\n\\s+propertyPath: m_Name\\s*\\n\\s+value: )[^\\n]*`
    );

    let newBlock;

    if (existingPattern.test(block)) {
        newBlock = block.replace(existingPattern, `$1${newName}`);
        const newText = text.slice(0, blockStart) + newBlock + rest + text.slice(blockEnd);
        fs.writeFileSync(parentPath, newText, 'utf-8');
        console.log(`updated existing m_Name override to '${newName}'`);
        return;
    }

    const modEntry =
        `    - target: {fileID: ${sourceRootFileId}, guid: ${sourceGuid}, type: 3}\n` +
        `      propertyPath: m_Name\n` +
        `      value: ${newName}\n` +
        `      objectReference: {fileID: 0}\n`;

    const markerMatch = block.match(/^(\s*)(m_RemovedComponents|m_RemovedGameObjects|m_AddedGameObjects|m_AddedComponents|m_SourcePrefab):/m);

    if (!markerMatch) {
        console.error('error: no marker found to insert modification');
        process.exit(3);
    }

    const insertPos = markerMatch.index;
    newBlock = block.slice(0, insertPos) + modEntry + block.slice(insertPos);
    const newText = text.slice(0, blockStart) + newBlock + rest + text.slice(blockEnd);
    fs.writeFileSync(parentPath, newText, 'utf-8');
    console.log(`added m_Name override '${newName}' to instance &${instanceId}`);
}

function main() {
    if (process.argv.length < 7) {
        console.error('usage: node rename_nested.js <parent_prefab> <instance_id> <source_root_file_id> <source_guid> <new_name>');
        process.exit(1);
    }

    const parent = process.argv[2];

    if (!fs.existsSync(parent)) {
        console.error(`not found: ${parent}`);
        process.exit(2);
    }

    rename(parent, process.argv[3], process.argv[4], process.argv[5], process.argv[6]);
}

main();
