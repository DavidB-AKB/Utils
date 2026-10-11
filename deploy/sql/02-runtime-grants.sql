-- Privileges for the runtime login. Re-runnable, and MUST be re-run after every migration that
-- creates a schema or table (the ALTER DEFAULT PRIVILEGES below covers tables created afterwards by
-- the migrator, but not ones that already exist when this first runs).
--
--   psql -v runtime_role=app_runtime -v migrator_role=app_migrator -f 02-runtime-grants.sql
--
-- WHY THERE IS NO "DENY" HERE, which the original design called for: Postgres has no DENY. Its
-- privilege system is purely additive - GRANT adds, REVOKE removes a prior grant, and nothing
-- expresses "and never allow this even if granted later". That is a real difference from SQL Server,
-- where DENY outranks any subsequent GRANT.
--
-- So the revokes below are necessary but NOT sufficient: they are correct for the roles as
-- configured, and they would be silently undone by someone granting the runtime role a broader
-- membership later. The property the audit trail actually needs - "these rows cannot be rewritten,
-- whatever the grants say" - comes from the triggers in 03-append-only-audit.sql, which apply to
-- every role including the table owner. Run both; neither alone is the control.

\set ON_ERROR_STOP on

-- Role names are carried into the DO block through session settings rather than referenced directly.
-- psql substitutes :'var' while lexing the input, and a dollar-quoted body ($$ ... $$) is ONE token
-- to the lexer - so a :'runtime_role' written inside the block below would be sent to the server
-- verbatim and fail there. These two SELECTs are plain statements, where substitution does happen.
SELECT set_config('utils.runtime_role',  :'runtime_role',  false);
SELECT set_config('utils.migrator_role', :'migrator_role', false);

DO $$
DECLARE
    runtime_role  text := current_setting('utils.runtime_role');
    migrator_role text := current_setting('utils.migrator_role');
    service_schema text;
    audit_table record;
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), runtime_role);

    -- SERVICE SCHEMAS ARE DISCOVERED, NOT LISTED.
    --
    -- A schema belongs to a service exactly when it holds that service's own
    -- __EFMigrationsHistory table - which is the workspace's shared-database convention, so this
    -- rule is not a heuristic, it is the definition. Discovering them means adding a service needs
    -- no edit here, and the hand-maintained list this replaced was already two schemas out of date
    -- (it predated contacts and tasks) while looking complete.
    FOR service_schema IN
        SELECT DISTINCT n.nspname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'r'
          AND c.relname = '__EFMigrationsHistory'
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
    LOOP
        EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', service_schema, runtime_role);
        EXECUTE format(
            'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I',
            service_schema, runtime_role);
        EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', service_schema, runtime_role);

        -- Tables the migrator creates LATER get the same privileges automatically. Without this, the
        -- first request after an upgrade fails with "permission denied for table" on whatever the
        -- migration just added - a failure that looks like a code bug and is not.
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I '
            'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
            migrator_role, service_schema, runtime_role);
        EXECUTE format(
            'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO %I',
            migrator_role, service_schema, runtime_role);
    END LOOP;

    -- Then take back exactly what the audit tables must not allow. Done AFTER the blanket grant
    -- above rather than by granting each table individually, so a new audit table added by a future
    -- service is caught by re-running this file rather than by somebody remembering to exclude it.
    --
    -- TRUNCATE is revoked alongside UPDATE and DELETE because it is neither: it is its own privilege,
    -- it is not covered by withholding DELETE, and it bypasses row-level triggers entirely. Omitting
    -- it would leave a one-statement way to erase the whole trail.
    FOR audit_table IN
        SELECT n.nspname AS schema_name, c.relname AS table_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'r'
          AND c.relname = 'AuditEvents'
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
    LOOP
        EXECUTE format(
            'REVOKE UPDATE, DELETE, TRUNCATE ON %I.%I FROM %I',
            audit_table.schema_name, audit_table.table_name, runtime_role);

        -- PUBLIC too. Nothing here grants to PUBLIC today, but a privilege held by PUBLIC is held by
        -- every role that will ever exist, so leaving it unaddressed makes the revoke above
        -- conditional on nobody ever doing it.
        EXECUTE format(
            'REVOKE UPDATE, DELETE, TRUNCATE ON %I.%I FROM PUBLIC',
            audit_table.schema_name, audit_table.table_name);
    END LOOP;
END;
$$;
