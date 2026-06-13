// =====================================================================
// Catálogo de consultas de lineage / reglas de negocio
// Para el grafo generado por tsql-parser (graph_*.json) cargado en Neo4j.
// Carga previa: ver output/tsql_neo4j_import.cypher
//
// Cada consulta responde una pregunta de negocio concreta. Sustituye los
// literales ('dbo.MiTabla', 'MiColumna', ...) por los tuyos.
// =====================================================================


// 1. PROVENANCE DE COLUMNA  ───────────────────────────────────────────
// "¿De qué columnas fuente (ETL) deriva en última instancia esta columna?"
// Recorre DERIVES_FROM transitivamente hasta las columnas raíz.
MATCH path = (c:Column {name: 'Total'})-[:DERIVES_FROM*1..6]->(src:Column)
WHERE NOT (src)-[:DERIVES_FROM]->()        // src es columna raíz (no deriva de nada)
RETURN c.table AS columna_destino, c.name AS columna,
       src.table AS tabla_origen, src.name AS columna_origen,
       length(path) AS saltos
ORDER BY saltos DESC;


// 2. ANÁLISIS DE IMPACTO  ─────────────────────────────────────────────
// "Si toco esta tabla, ¿qué procedimientos la afectan (directa o por CALLS)?"
MATCH (o:SqlObject)-[a:AFFECTS]->(t:Table {name: 'Production.TransactionHistory'})
RETURN o.full_name AS procedimiento, a.hops AS saltos_en_cadena, a.via AS via
ORDER BY a.hops;


// 3. SUPERFICIE DE SQL DINÁMICO (riesgo de inyección)  ────────────────
// "¿Qué pasos ejecutan SQL dinámico y con qué variables/parámetros lo construyen?"
MATCH (s:Step {is_dynamic_sql: true})-[:BUILDS_SQL_FROM]->(v)
OPTIONAL MATCH (s)<-[:HAS_STEP]-(o:SqlObject)
RETURN o.full_name AS procedimiento, s.line_no AS linea,
       collect(v.name) AS variables_que_construyen_sql
ORDER BY procedimiento;


// 4. ESCRITURAS INCONDICIONALES vs GOBERNADAS  ───────────────────────
// "¿Qué escrituras se ejecutan SIEMPRE (sin ninguna regla que las gobierne)?"
// Útil para detectar efectos colaterales no protegidos por validaciones.
MATCH (s:Step)-[:WRITES_TO]->(t:Table)
WHERE NOT ( (:Rule)-[:GOVERNS]->(s) )
MATCH (s)<-[:HAS_STEP]-(o:SqlObject)
RETURN o.full_name AS procedimiento, s.action AS operacion, t.name AS tabla, s.line_no AS linea
ORDER BY tabla;


// 5. REGLAS QUE GOBIERNAN ESCRITURAS, POR TABLA  ─────────────────────
// "¿Qué condición de negocio dispara cada escritura sobre cada tabla?"
MATCH (r:Rule)-[:GOVERNS]->(s:Step)-[:WRITES_TO]->(t:Table)
RETURN t.name AS tabla, r.type AS tipo_regla, r.expression AS regla, s.action AS operacion
ORDER BY tabla;


// 6. ACOPLAMIENTO IMPLÍCITO ENTRE PROCEDIMIENTOS  ────────────────────
// "Proc A escribe una tabla que Proc B lee → dependencia oculta vía datos."
MATCH (a:SqlObject)-[:HAS_STEP]->(:Step)-[:WRITES_TO]->(t:Table)<-[:READS_FROM]-(:Step)<-[:HAS_STEP]-(b:SqlObject)
WHERE a <> b
RETURN a.full_name AS escribe, t.name AS tabla, b.full_name AS lee
ORDER BY tabla;


// 7. INVENTARIO DE REGLAS DE NEGOCIO  ────────────────────────────────
// "Catálogo de todas las decisiones distintas del código, por frecuencia."
MATCH (r:Rule)-[:GOVERNS]->(s:Step)
RETURN r.type AS tipo, r.expression AS regla, count(s) AS pasos_gobernados
ORDER BY pasos_gobernados DESC;


// 8. VARIABLE → COLUMNA → SQL DINÁMICO (trazado de fuga)  ─────────────
// "¿Qué variable se rellena desde una columna y luego alimenta SQL dinámico?"
// Cadena ASSIGNED_FROM + BUILDS_SQL_FROM: dato de tabla que termina concatenado.
MATCH (v:Variable)-[:ASSIGNED_FROM]->(col:Column)
MATCH (s:Step)-[:BUILDS_SQL_FROM]->(v)
MATCH (s)<-[:HAS_STEP]-(o:SqlObject)
RETURN o.full_name AS procedimiento, v.name AS variable,
       col.table AS tabla_origen, col.name AS columna_origen, s.line_no AS linea;


// 9. HOTSPOTS: TABLAS MÁS ESCRITAS  ──────────────────────────────────
// "¿Qué tablas concentran más escrituras (candidatas a contención/auditoría)?"
MATCH (s:Step)-[:WRITES_TO]->(t:Table)
RETURN t.name AS tabla, count(s) AS num_escrituras,
       collect(DISTINCT s.action) AS operaciones
ORDER BY num_escrituras DESC
LIMIT 15;


// 10. COLUMNAS HUÉRFANAS DE LINAJE  ──────────────────────────────────
// "Columnas que se escriben pero cuyo origen no se pudo trazar (SELECT *, UNION,
//  expresiones sin refs) → puntos ciegos del lineage a revisar manualmente."
MATCH (s:Step)-[:WRITES_COLUMN]->(c:Column)
WHERE NOT (c)-[:DERIVES_FROM]->()
RETURN DISTINCT c.table AS tabla, c.name AS columna
ORDER BY tabla, columna;
