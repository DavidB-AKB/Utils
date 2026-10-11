-- Roles for a ServicesDb deployment. Re-runnable.
--
-- TWO LOGINS, AND THE SPLIT IS THE WHOLE POINT. The application runs as one; migrations run as the
-- other. Without that separation the append-only audit grant in 02 is meaningless, because the same
-- login that must not rewrite audit rows would also be the one holding the privileges needed to
-- create and alter the tables.
--
--   psql -v runtime_password=... -v migrator_password=... -f 01-roles.sql
--
-- Passwords are parameters rather than literals so this file can be committed. In a real deployment
-- they come from the secrets store (see Utils issue #5), not from a shell history.

\set ON_ERROR_STOP on

-- The migrator OWNS the schemas and tables. It is the only role that can change their shape, which
-- is also what stops the runtime role disabling the append-only triggers in 03 - a trigger can only
-- be disabled by the table's owner, so ownership is load-bearing rather than incidental.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_migrator') THEN
        CREATE ROLE app_migrator LOGIN;
    END IF;

    -- The application. Reads and writes business data, appends audit rows, and can do nothing else.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_runtime') THEN
        CREATE ROLE app_runtime LOGIN;
    END IF;
END;
$$;

ALTER ROLE app_migrator WITH PASSWORD :'migrator_password';
ALTER ROLE app_runtime  WITH PASSWORD :'runtime_password';

-- Neither role is a superuser, and neither may create databases or roles. Stated explicitly rather
-- than relied on as the default, because a role that picked either up later would silently defeat
-- everything below.
ALTER ROLE app_migrator WITH NOSUPERUSER NOCREATEDB NOCREATEROLE;
ALTER ROLE app_runtime  WITH NOSUPERUSER NOCREATEDB NOCREATEROLE;
