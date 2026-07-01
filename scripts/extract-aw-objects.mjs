// extract-aw-objects.mjs

import { execFile } from 'child_process';
import { promises as fs } from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

// --- Configuration ---
const SQLCMD_PATH = 'C:\\Program Files\\Microsoft SQL Server\\Client SDK\\ODBC\\180\\Tools\\Binn\\sqlcmd.exe';
const SERVER_INSTANCE = 'localhost\\SQLEXPRESS';
const DATABASE_NAME = 'AdventureWorks2019';
const OUTPUT_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'sql', 'adventureworks');
const DELIMITER = 'GO--_--OBJECT_SEPARATOR--_--';

// List of the 20 views from AdventureWorks2019 to process, as per task #A
const ROOT_VIEWS = [
    { schema: 'HumanResources', name: 'vEmployee' },
    { schema: 'HumanResources', name: 'vEmployeeDepartment' },
    { schema: 'HumanResources', name: 'vEmployeeDepartmentHistory' },
    { schema: 'HumanResources', name: 'vJobCandidate' },
    { schema: 'HumanResources', name: 'vJobCandidateEducation' },
    { schema: 'HumanResources', name: 'vJobCandidateEmployment' },
    { schema: 'Person', name: 'vAdditionalContactInfo' },
    { schema: 'Person', name: 'vIndividualCustomer' },
    { schema: 'Person', name: 'vPersonDemographics' },
    { schema: 'Person', name: 'vStateProvinceCountryRegion' },
    { schema: 'Production', name: 'vProductAndDescription' },
    { schema: 'Production', name: 'vProductModelCatalogDescription' },
    { schema: 'Production', name: 'vProductModelInstructions' },
    { schema: 'Purchasing', name: 'vVendorWithAddresses' },
    { schema: 'Purchasing', name: 'vVendorWithContacts' },
    { schema: 'Sales', name: 'vIndividualDemographics' },
    { schema: 'Sales', name: 'vSalesPerson' },
    { schema: 'Sales', name: 'vSalesPersonSalesByFiscalYears' },
    { schema: 'Sales', name: 'vStoreWithAddresses' },
    { schema: 'Sales', name: 'vStoreWithContacts' },
];

/**
 * Generates the T-SQL query to find all dependencies and script them out.
 * @returns {string} The T-SQL query string.
 */
function getExtractionQuery() {
    const rootValues = ROOT_VIEWS.map(v => `('${v.schema}', '${v.name}')`).join(',\n');

    // This T-SQL recursively finds all dependencies (tables, views, functions, types)
    // starting from the root views and then scripts their DDL.
    return `
        SET NOCOUNT ON;

        -- 1. Define root objects
        IF OBJECT_ID('tempdb..#RootObjects') IS NOT NULL DROP TABLE #RootObjects;
        CREATE TABLE #RootObjects (SchemaName SYSNAME, ObjectName SYSNAME);
        INSERT INTO #RootObjects (SchemaName, ObjectName) VALUES
        ${rootValues};

        -- 2. Store all dependencies found
        IF OBJECT_ID('tempdb..#AllDependencies') IS NOT NULL DROP TABLE #AllDependencies;
        CREATE TABLE #AllDependencies (
            SchemaName SYSNAME,
            ObjectName SYSNAME,
            ObjectType NVARCHAR(60),
            UNIQUE (SchemaName, ObjectName)
        );

        -- 3. Store objects to process in the current iteration
        IF OBJECT_ID('tempdb..#ToProcess') IS NOT NULL DROP TABLE #ToProcess;
        CREATE TABLE #ToProcess (SchemaName SYSNAME, ObjectName SYSNAME);

        -- Start with the root objects
        INSERT INTO #ToProcess (SchemaName, ObjectName)
        SELECT SchemaName, ObjectName FROM #RootObjects;

        -- Recursively find dependencies
        WHILE (SELECT COUNT(*) FROM #ToProcess) > 0
        BEGIN
            -- Add objects to be processed to the final dependency list
            INSERT INTO #AllDependencies (SchemaName, ObjectName, ObjectType)
            SELECT
                p.SchemaName,
                p.ObjectName,
                o.type_desc
            FROM #ToProcess p
            JOIN sys.objects o ON o.object_id = OBJECT_ID(QUOTENAME(p.SchemaName) + '.' + QUOTENAME(p.ObjectName))
            JOIN sys.schemas s ON o.schema_id = s.schema_id AND s.name = p.SchemaName
            WHERE NOT EXISTS (
                SELECT 1 FROM #AllDependencies ad
                WHERE ad.SchemaName = p.SchemaName AND ad.ObjectName = p.ObjectName
            );

            -- Find dependencies of the current batch of objects
            IF OBJECT_ID('tempdb..#NewFound') IS NOT NULL DROP TABLE #NewFound;
            CREATE TABLE #NewFound (SchemaName SYSNAME, ObjectName SYSNAME);

            INSERT INTO #NewFound (SchemaName, ObjectName)
            SELECT DISTINCT
                COALESCE(d.referenced_schema_name, s.name) AS SchemaName,
                d.referenced_entity_name AS ObjectName
            FROM #ToProcess t
            CROSS APPLY sys.dm_sql_referenced_entities(QUOTENAME(t.SchemaName) + '.' + QUOTENAME(t.ObjectName), 'OBJECT') AS d
            JOIN sys.objects o ON o.object_id = d.referenced_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE d.referenced_id IS NOT NULL AND o.is_ms_shipped = 0;

            -- Clear the processing table
            TRUNCATE TABLE #ToProcess;

            -- Populate the processing table with newly found dependencies that we haven't processed yet
            INSERT INTO #ToProcess (SchemaName, ObjectName)
            SELECT
                nf.SchemaName,
                nf.ObjectName
            FROM #NewFound nf
            WHERE NOT EXISTS (
                SELECT 1 FROM #AllDependencies ad
                WHERE ad.SchemaName = nf.SchemaName AND ad.ObjectName = nf.ObjectName
            );

            DROP TABLE #NewFound;
        END;

        -- Also find User-Defined Types (UDTs) used by the collected objects
        INSERT INTO #AllDependencies (SchemaName, ObjectName, ObjectType)
        SELECT DISTINCT
            s.name,
            t.name,
            'USER_DEFINED_TYPE'
        FROM sys.types t
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        JOIN sys.sql_expression_dependencies ed ON ed.referenced_id = t.user_type_id
        WHERE t.is_user_defined = 1
          AND ed.referencing_id IN (
              SELECT OBJECT_ID(QUOTENAME(ad.SchemaName) + '.' + QUOTENAME(ad.ObjectName))
              FROM #AllDependencies ad
          )
          AND NOT EXISTS (
              SELECT 1 FROM #AllDependencies existing
              WHERE existing.SchemaName = s.name AND existing.ObjectName = t.name
          );

        -- 4. Script out the DDL for each dependency
        DECLARE @schemaName SYSNAME, @objectName SYSNAME, @objectType NVARCHAR(60);
        DECLARE @fullName NVARCHAR(512);
        DECLARE @definition NVARCHAR(MAX);

        DECLARE cur CURSOR FOR
            SELECT SchemaName, ObjectName, ObjectType
            FROM #AllDependencies
            ORDER BY
                CASE ObjectType
                    WHEN 'USER_DEFINED_TYPE' THEN 1
                    WHEN 'USER_TABLE' THEN 2
                    WHEN 'SQL_SCALAR_FUNCTION' THEN 3
                    WHEN 'SQL_TABLE_VALUED_FUNCTION' THEN 4
                    WHEN 'SQL_INLINE_TABLE_VALUED_FUNCTION' THEN 5
                    WHEN 'VIEW' THEN 6
                    ELSE 99
                END,
                SchemaName, ObjectName;

        OPEN cur;
        FETCH NEXT FROM cur INTO @schemaName, @objectName, @objectType;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @fullName = QUOTENAME(@schemaName) + '.' + QUOTENAME(@objectName);
            SET @definition = NULL;

            IF @objectType = 'USER_DEFINED_TYPE'
            BEGIN
                SELECT @definition = 'CREATE TYPE ' + @fullName + ' FROM ' + bt.name +
                                     CASE WHEN st.max_length != -1 AND bt.name NOT IN ('nchar', 'nvarchar', 'sysname') THEN '(' + CAST(st.max_length AS VARCHAR) + ')' ELSE '' END +
                                     CASE WHEN st.max_length != -1 AND bt.name IN ('nchar', 'nvarchar', 'sysname') THEN '(' + CAST(st.max_length/2 AS VARCHAR) + ')' ELSE '' END +
                                     CASE WHEN st.precision > 0 AND bt.name IN ('decimal', 'numeric') THEN '(' + CAST(st.precision AS VARCHAR) + ',' + CAST(st.scale AS VARCHAR) + ')' ELSE '' END +
                                     CASE WHEN st.is_nullable = 0 THEN ' NOT NULL' ELSE '' END
                FROM sys.types st
                JOIN sys.types bt ON st.system_type_id = bt.user_type_id AND bt.is_user_defined = 0
                WHERE st.schema_id = SCHEMA_ID(@schemaName) AND st.name = @objectName;
            END
            ELSE
            BEGIN
                SET @definition = OBJECT_DEFINITION(OBJECT_ID(@fullName));
            END

            IF @definition IS NOT NULL AND LTRIM(RTRIM(@definition)) != ''
            BEGIN
                PRINT '-- FILENAME: ' + @schemaName + '.' + @objectName + '.sql';
                PRINT @definition;
                PRINT N'${DELIMITER}';
            END

            FETCH NEXT FROM cur INTO @schemaName, @objectName, @objectType;
        END

        CLOSE cur;
        DEALLOCATE cur;

        DROP TABLE #RootObjects;
        DROP TABLE #AllDependencies;
        DROP TABLE #ToProcess;
    `;
}

/**
 * Main function to execute the extraction process.
 */
async function main() {
    console.log(`Starting extraction for ${DATABASE_NAME}...`);

    const query = getExtractionQuery();
    const args = [
        '-S', SERVER_INSTANCE,
        '-d', DATABASE_NAME,
        '-E', // Integrated security
        '-C', // Trust server certificate
        '-W', // Remove trailing spaces
        '-Q', query
    ];

    try {
        console.log(`Executing sqlcmd against ${SERVER_INSTANCE}...`);
        const { stdout, stderr } = await new Promise((resolve, reject) => {
            // Increase buffer size for potentially large DDL output
            execFile(SQLCMD_PATH, args, { maxBuffer: 1024 * 1024 * 10 }, (error, stdout, stderr) => {
                if (error && stderr && /Msg [0-9]+, Level [0-9]+, State [0-9]+/.test(stderr)) {
                    reject(error);
                    return;
                }
                resolve({ stdout, stderr });
            });
        });

        if (stderr && !stderr.includes('Changed database context')) {
            console.warn('sqlcmd produced warnings:', stderr);
        }

        console.log('Parsing sqlcmd output...');
        const objects = stdout.split(DELIMITER);

        await fs.mkdir(OUTPUT_DIR, { recursive: true });
        console.log(`Ensured output directory exists: ${OUTPUT_DIR}`);

        let filesWritten = 0;
        for (const objectBlock of objects) {
            const trimmedBlock = objectBlock.trim();
            if (!trimmedBlock) continue;

            const firstLineEnd = trimmedBlock.indexOf('\n');
            const firstLine = trimmedBlock.substring(0, firstLineEnd).trim();

            if (firstLine.startsWith('-- FILENAME:')) {
                const filename = firstLine.replace('-- FILENAME:', '').trim();
                const content = trimmedBlock.substring(firstLineEnd + 1).trim();

                if (content) {
                    const filePath = path.join(OUTPUT_DIR, filename);
                    await fs.writeFile(filePath, content, 'utf8');
                    filesWritten++;
                }
            }
        }

        console.log(`Successfully wrote ${filesWritten} SQL object files to ${OUTPUT_DIR}`);

    } catch (error) {
        console.error('An error occurred during extraction:');
        console.error('Message:', error.message);
        if (error.stderr) {
            console.error('STDERR:', error.stderr);
        }
        if (error.stdout) {
            console.error('STDOUT:', error.stdout);
        }
        process.exit(1);
    }
}

main();