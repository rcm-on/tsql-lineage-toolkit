// Mide tiempo de lectura+resolución y memoria real para el Caso 2 del análisis
// nodestore-analysis.md: "¿bajo qué condiciones se ejecuta step18?"
// A) graph.json completo: cargar todo + seguir GOVERNS/NESTED_IN entre Rules.
// B) nodestore: leer 1 object.json y tomar el campo condition_path ya resuelto.
const fs = require('fs');
const path = require('path');

const N = 200; // repeticiones para estabilizar el tiempo
const strip = s => s.replace(/^﻿/, '');

function approachA() {
  const raw = strip(fs.readFileSync(path.join(__dirname, 'graph.json'), 'utf8'));
  const g = JSON.parse(raw);
  const rels = g.Relationships;
  const nodes = g.Nodes;
  const byId = {};
  for (const n of nodes) byId[n.Id] = n;

  // 1) GOVERNS cuyo EndNodeId es #step18
  const target = rels.find(r => r.Type === 'GOVERNS' && r.EndNodeId.endsWith('#step18'));
  let ruleId = target.StartNodeId;
  const chain = [];
  while (ruleId) {
    const ruleNode = byId[ruleId];
    chain.push(ruleNode.Properties.expression || ruleNode.Properties.condition);
    const nested = rels.find(r => r.Type === 'NESTED_IN' && r.StartNodeId === ruleId);
    ruleId = nested ? nested.EndNodeId : null;
  }
  return chain;
}

function approachB() {
  const raw = strip(fs.readFileSync(
    path.join(__dirname, 'graph.nodes/objects/TestWorkflowDb_dbo.ProcessOrderWorkflow/object.json'), 'utf8'));
  const obj = JSON.parse(raw);
  const step = obj.owned.steps.find(s => s.id.endsWith('#step18'));
  return step.properties.condition_path;
}

function bench(fn, n) {
  // warm-up (descarta efectos de cache de disco/JIT en la primera pasada)
  fn();
  const memBefore = process.memoryUsage().heapUsed;
  const t0 = process.hrtime.bigint();
  let last;
  for (let i = 0; i < n; i++) last = fn();
  const t1 = process.hrtime.bigint();
  const memAfter = process.memoryUsage().heapUsed;
  return {
    result: last,
    totalMs: Number(t1 - t0) / 1e6,
    avgMs: Number(t1 - t0) / 1e6 / n,
    heapDeltaKB: (memAfter - memBefore) / 1024,
  };
}

const a = bench(approachA, N);
const b = bench(approachB, N);

console.log('=== A) graph.json completo ===');
console.log('resultado:', a.result);
console.log(`tiempo total (${N} iter): ${a.totalMs.toFixed(1)} ms | media: ${a.avgMs.toFixed(3)} ms/iter`);
console.log(`heap tras ${N} iter: ${a.heapDeltaKB.toFixed(0)} KB`);

console.log('\n=== B) nodestore (object.json) ===');
console.log('resultado:', b.result);
console.log(`tiempo total (${N} iter): ${b.totalMs.toFixed(1)} ms | media: ${b.avgMs.toFixed(3)} ms/iter`);
console.log(`heap tras ${N} iter: ${b.heapDeltaKB.toFixed(0)} KB`);

console.log(`\nRatio tiempo (A/B): ${(a.avgMs / b.avgMs).toFixed(2)}x`);
