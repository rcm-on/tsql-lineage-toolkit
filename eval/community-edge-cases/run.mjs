// DEPRECADO (2026-07-03, paso 2 de docs/task-gates-dotnet.md): este runner solo
// regenera out/*/graph_full.json y sale !=0 si el pipeline crashea - nunca
// comparó aristas. El gate real ahora es
// tests/TSqlParser.Tests/CommunityEdgeCaseGateTests.cs (dotnet test, in-process,
// compara DERIVES_FROM/READS_FROM contra *.expected.json de cada caso). Se
// conserva sin borrar (regenera el corpus out/ para inspección manual) hasta que
// nada dependa de él.
import { execSync } from 'child_process';
import { fileURLToPath } from 'url';
import path from 'path';
import fs from 'fs';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const rootDir = path.resolve(__dirname, '..', '..');
const dllPath = path.join(rootDir, 'src', 'TSqlParser', 'bin', 'Release', 'net10.0', 'TSqlParser.dll');
const dbName = 'CommunityCasesDB';

const testCases = [
    { name: 'merge', files: ['dml-advanced/merge.sql'] },
    { name: 'merge-with-output', files: ['dml-advanced/merge-with-output.sql'] },
    { name: 'recursive-cte', files: ['cte-recursive/recursive-cte.sql'] },
    { name: 'window', files: ['window-functions/window.sql'] },
    { name: 'union-view', files: ['set-ops/union-view.sql'] },
    { name: 'lineage-chain', files: ['lineage-chain/01-base-table.sql', 'lineage-chain/02-view-level1.sql', 'lineage-chain/03-view-level2.sql', 'lineage-chain/04-view-level3.sql'] },
    { name: 'dynamic-sql-complex', files: ['dynamic-sql/quotename-case-coalesce.sql'] },
];

function runCommand(command) {
    console.log(`\n> ${command}`);
    try {
        execSync(command, { stdio: 'inherit' });
    } catch (error) {
        console.error(`\n❌ Command failed: ${command}`);
        process.exit(1);
    }
}

console.log('🚀 Running community edge cases...');

for (const testCase of testCases) {
    console.log(`\n--- Processing case: ${testCase.name} ---`);

    const caseDir = path.join(__dirname, 'out', testCase.name);
    if (!fs.existsSync(caseDir)) {
        fs.mkdirSync(caseDir, { recursive: true });
    }

    const inputJsonPath = path.join(caseDir, 'input.json');
    const graphJsonPath = path.join(caseDir, 'graph_full.json');
    const nodestorePath = path.join(caseDir, 'graph_full.nodes');

    const sqlFiles = testCase.files.map(f => `"${path.join(__dirname, f)}"`).join(' ');

    runCommand(`dotnet "${dllPath}" from-sql "${dbName}" "${inputJsonPath}" ${sqlFiles}`);
    runCommand(`dotnet "${dllPath}" "${inputJsonPath}" "${graphJsonPath}" --columns --nodestore`);

    console.log(`✅ Case ${testCase.name} processed successfully.`);
}

console.log('\n🎉 All community edge cases processed.');