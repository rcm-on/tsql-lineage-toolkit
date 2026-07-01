// extract-aw-full.mjs — robust AdventureWorks2019 extractor.
// Writes one CREATE-statement file per object into sql/adventureworks/:
//   - VIEW / FUNCTION  -> OBJECT_DEFINITION (verbatim DDL)
//   - USER_TABLE       -> generated CREATE TABLE from INFORMATION_SCHEMA.COLUMNS
// Replaces the PRINT-delimiter approach (which produced 0 files).
import { execFileSync } from 'node:child_process';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SQLCMD = 'C:\\Program Files\\Microsoft SQL Server\\Client SDK\\ODBC\\180\\Tools\\Binn\\sqlcmd.exe';
const SERVER = 'localhost\\SQLEXPRESS';
const DB = 'AdventureWorks2019';
const OUT = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'sql', 'adventureworks');

function q(sql) {
  return execFileSync(SQLCMD, ['-S', SERVER, '-d', DB, '-E', '-C', '-h-1', '-s', '\t', '-Q',
    'SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;\n' + sql], { encoding: 'utf8', maxBuffer: 1 << 28 });
}

// OBJECT_DEFINITION can exceed PRINT's ~4000-char limit (it silently truncates,
// splitting long definitions mid-token). Fetch via SELECT with -y0 (unlimited column
// width) instead, then slice from the first CREATE to drop sqlcmd's header rows.
function qdef(fn) {
  const raw = execFileSync(SQLCMD, ['-S', SERVER, '-d', DB, '-E', '-C', '-y0', '-Q',
    `SET NOCOUNT ON; SELECT OBJECT_DEFINITION(OBJECT_ID('${fn}'));`],
    { encoding: 'utf8', maxBuffer: 1 << 28 });
  const m = raw.match(/\bCREATE\s/i);
  return m ? raw.slice(m.index).replace(/\r\n/g, '\n').trim() : '';
}

// CREATE TABLE generator: one row per table with the full DDL pre-assembled in T-SQL.
const TABLE_DDL_SQL = `
SELECT s.name + '.' + t.name AS fn,
  'CREATE TABLE [' + s.name + '].[' + t.name + '] (' + CHAR(10) +
  STUFF((
    SELECT ',' + CHAR(10) + '  [' + c.name + '] ' +
      ty.name +
      CASE WHEN ty.name IN ('varchar','char','varbinary','binary')
             THEN '(' + IIF(c.max_length=-1,'max',CAST(c.max_length AS varchar)) + ')'
           WHEN ty.name IN ('nvarchar','nchar')
             THEN '(' + IIF(c.max_length=-1,'max',CAST(c.max_length/2 AS varchar)) + ')'
           WHEN ty.name IN ('decimal','numeric')
             THEN '(' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
           ELSE '' END +
      CASE WHEN c.is_nullable=0 THEN ' NOT NULL' ELSE ' NULL' END
    FROM sys.columns c
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = t.object_id
    ORDER BY c.column_id
    FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 1, '') +
  CHAR(10) + ');' AS ddl
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0
ORDER BY fn;
`;

// Programmable objects (views, functions) — name + type only; DDL fetched per-object.
const PROG_SQL = `
SELECT s.name + '.' + o.name AS fn, o.type_desc
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('V','FN','IF','TF')
ORDER BY fn;
`;

function rows(out) {
  return out.split(/\r?\n/).map(l => l.trimEnd()).filter(l => l && !/rows affected/.test(l));
}

async function main() {
  await fs.mkdir(OUT, { recursive: true });
  let n = 0;

  // 1) Tables (DDL assembled in SQL; one row per table, tab-separated fn<TAB>ddl-with-CHAR(10)).
  // CHAR(10) inside the value keeps the DDL on one logical column; sqlcmd prints it across lines,
  // so re-fetch each table individually to keep newlines intact.
  const tableList = rows(q(
    `SELECT s.name + '.' + t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY 1;`));
  for (const fn of tableList) {
    const ddl = q(`
      DECLARE @r nvarchar(max);
      SELECT @r = 'CREATE TABLE [' + s.name + '].[' + t.name + '] (' + CHAR(10) +
        STUFF((SELECT ',' + CHAR(10) + '  [' + c.name + '] ' + ty.name +
          CASE WHEN ty.name IN ('varchar','char','varbinary','binary') THEN '(' + IIF(c.max_length=-1,'max',CAST(c.max_length AS varchar)) + ')'
               WHEN ty.name IN ('nvarchar','nchar') THEN '(' + IIF(c.max_length=-1,'max',CAST(c.max_length/2 AS varchar)) + ')'
               WHEN ty.name IN ('decimal','numeric') THEN '(' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
               ELSE '' END +
          CASE WHEN c.is_nullable=0 THEN ' NOT NULL' ELSE ' NULL' END
          FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id
          WHERE c.object_id=t.object_id ORDER BY c.column_id
          FOR XML PATH(''), TYPE).value('.','nvarchar(max)'),1,1,'') + CHAR(10) + ');'
      FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
      WHERE s.name='${fn.split('.')[0]}' AND t.name='${fn.slice(fn.indexOf('.')+1)}';
      PRINT @r;`);
    await fs.writeFile(path.join(OUT, fn + '.sql'), ddl.replace(/\r?\n/g, '\n').trim() + '\n', 'utf8');
    n++;
  }

  // 2) Views + functions: verbatim OBJECT_DEFINITION.
  const prog = rows(q(PROG_SQL)).map(l => l.split('\t')[0].trim());
  for (const fn of prog) {
    const def = qdef(fn);
    if (def) { await fs.writeFile(path.join(OUT, fn + '.sql'), def + '\n', 'utf8'); n++; }
  }

  console.log(`Wrote ${n} files to ${OUT} (${tableList.length} tables, ${prog.length} programmables).`);
}
main();
