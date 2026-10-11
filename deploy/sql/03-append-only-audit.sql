-- Makes every AuditEvents table genuinely append-only. Re-runnable, and MUST be re-run after any
-- migration that creates a new service's audit table.
--
--   psql -f 03-append-only-audit.sql
--
-- THIS IS THE LAYER THAT ACTUALLY ENFORCES IT, and the reason it exists rather than relying on the
-- grants in 02 is specific to Postgres. Postgres has no DENY: privileges are additive, REVOKE only
-- removes a prior grant, and nothing in the privilege system expresses "never allow this even if
-- granted later". So the revokes in 02 are correct for the roles as configured and would be
-- silently undone by a later GRANT or a broader role membership.
--
-- A trigger has no such hole. It applies to every role, including the table owner and a superuser,
-- so an audit row cannot be rewritten by anyone who has not first explicitly and visibly disabled
-- it - which requires ownership of the table, which the runtime role deliberately does not have
-- (see 01-roles.sql).
--
-- It is not tamper-EVIDENCE. A superuser can still drop the trigger and rewrite history, and nothing
-- here would record that. Tamper-evidence needs a hash chain over the rows or an append-only WAL
-- archive, and should only be reached for if it becomes a stated requirement - see
-- Utils/CLAUDE.md's "Append-only enforcement".

\set ON_ERROR_STOP on

CREATE OR REPLACE FUNCTION public.utils_reject_audit_rewrite()
RETURNS trigger
LANGUAGE plpgsql
AS $fn$
BEGIN
    -- insufficient_privilege (42501) rather than a bespoke code, so a client that maps SQLSTATEs at
    -- all reports this as what it is: not permitted, rather than a transient fault to retry.
    RAISE EXCEPTION
        'AuditEvents is append-only: % on %.% is not permitted.',
        TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END;
$fn$;

DO $$
DECLARE
    audit_table record;
    installed int := 0;
BEGIN
    -- Discovered, not listed - same reasoning as 02. Every service maps the shared AuditEvent entity
    -- into its own schema under the same table name, so this finds all of them and will find the next
    -- one without an edit here.
    FOR audit_table IN
        SELECT n.nspname AS schema_name, c.relname AS table_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'r'
          AND c.relname = 'AuditEvents'
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
    LOOP
        EXECUTE format(
            'DROP TRIGGER IF EXISTS utils_audit_append_only ON %I.%I',
            audit_table.schema_name, audit_table.table_name);
        EXECUTE format($t$
            CREATE TRIGGER utils_audit_append_only
            BEFORE UPDATE OR DELETE ON %I.%I
            FOR EACH ROW EXECUTE FUNCTION public.utils_reject_audit_rewrite()
        $t$, audit_table.schema_name, audit_table.table_name);

        -- A SEPARATE STATEMENT-LEVEL TRIGGER FOR TRUNCATE, and it is not redundant: TRUNCATE fires
        -- no row-level triggers at all, so the trigger above would not see it. Without this, one
        -- statement erases the entire trail and nothing objects.
        EXECUTE format(
            'DROP TRIGGER IF EXISTS utils_audit_append_only_truncate ON %I.%I',
            audit_table.schema_name, audit_table.table_name);
        EXECUTE format($t$
            CREATE TRIGGER utils_audit_append_only_truncate
            BEFORE TRUNCATE ON %I.%I
            FOR EACH STATEMENT EXECUTE FUNCTION public.utils_reject_audit_rewrite()
        $t$, audit_table.schema_name, audit_table.table_name);

        installed := installed + 1;
    END LOOP;

    -- Loud rather than silent. Finding nothing means this ran before any migration, or against the
    -- wrong database - either way the audit tables are left unprotected, and a script that printed
    -- nothing would look like it had worked.
    IF installed = 0 THEN
        RAISE EXCEPTION
            'No AuditEvents tables found in database %. Run migrations first, or check the target.',
            current_database();
    END IF;

    RAISE NOTICE 'Append-only triggers installed on % AuditEvents table(s).', installed;
END;
$$;
