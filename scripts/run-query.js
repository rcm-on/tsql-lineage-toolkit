// Runs a saved lineage query (by @tag) from lineage-queries.sql against graph.db.
//
//   node scripts/run-query.js @impact            # run the saved @impact query
//   node scripts/run-query.js                    # list available tags
//   node scripts/run-query.js "SELECT ..."       # run ad-hoc SQL
//
// Requires Node 22.5+ (built-in node:sqlite). No npm dependency.
const fs = require('node:fs');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');

const dbPath = process.env.GRAPH_DB || 'out/graph.db';
const sqlFile = path.join(__dirname, 'lineage-queries.sql');
const arg = process.argv[2];

// Parse "-- @tag  description\n<sql up to ';'>" blocks out of the saved file.
function loadTags() {
  const text = fs.readFileSync(sqlFile, 'utf8');
  const tags = {};
  // "-- @tag ..." then skip any further comment-only lines, then the SQL up to ';'.
  const re = /--\s*@(\w+)\b[^\n]*\n(?:[ \t]*--[^\n]*\n)*([\s\S]*?;)/g;
  let m;
  while ((m = re.exec(text))) tags[m[1]] = m[2].trim();
  return tags;
}

const tags = loadTags();

if (!arg) {
  console.log('Available saved queries:');
  for (const t of Object.keys(tags)) console.log('  @' + t);
  process.exit(0);
}

const sql = arg.startsWith('@') ? tags[arg.slice(1)] : arg;
if (!sql) {
  console.error(`Unknown tag ${arg}. Available: ${Object.keys(tags).map(t => '@' + t).join(', ')}`);
  process.exit(1);
}

const db = new DatabaseSync(dbPath);
const t = process.hrtime.bigint();
const rows = db.prepare(sql).all();
const ms = Number(process.hrtime.bigint() - t) / 1e6;
db.close();

if (rows.length) console.table(rows);
console.log(`(${rows.length} rows, ${ms.toFixed(2)} ms)`);
