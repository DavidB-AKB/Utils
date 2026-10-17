#!/usr/bin/env bash
# Provisions or upgrades a ServicesDb deployment, in the one order that works.
#
# THE ORDERING IS THE POINT. 02 and 03 both discover what they act on by inspecting the live schema,
# so they have to run AFTER migrations - and after EVERY migration, because a new table has no
# grants and a new service's audit table has no triggers. Running them too early is the failure this
# script exists to prevent: it succeeds, reports nothing unusual, and leaves the audit tables
# unprotected.
#
#   ./provision.sh --database ServicesDb --migrate
#
# Re-runnable. With --migrate it also applies every service's EF migrations; without, it assumes you
# have already run them and only (re)applies grants and triggers, which is the common case after an
# upgrade performed some other way.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
workspace="$(cd "$here/../.." && pwd)"

database="ServicesDb"
runtime_role="app_runtime"
migrator_role="app_migrator"
create_roles=0
migrate=0

usage() {
    cat <<'USAGE'
Usage: provision.sh [options]

  --database NAME         Target database (default: ServicesDb)
  --runtime-role NAME     Runtime login (default: app_runtime)
  --migrator-role NAME    Migration login (default: app_migrator)
  --create-roles          Also run 01-roles.sql. Requires RUNTIME_PASSWORD and
                          MIGRATOR_PASSWORD in the environment.
  --migrate               Also apply every service's EF migrations between roles and grants.
  -h, --help              This.

psql connection comes from the usual PG* environment variables (PGHOST, PGUSER, ...), so this
script never takes a password on the command line.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --database) database="$2"; shift 2 ;;
        --runtime-role) runtime_role="$2"; shift 2 ;;
        --migrator-role) migrator_role="$2"; shift 2 ;;
        --create-roles) create_roles=1; shift ;;
        --migrate) migrate=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

run_sql() {
    # ON_ERROR_STOP so a failed statement fails the script. Without it psql reports the error and
    # exits 0, which would let provisioning "succeed" having applied half of itself.
    psql --no-psqlrc --quiet --set ON_ERROR_STOP=1 --dbname "$database" "$@"
}

step() { printf '\n==> %s\n' "$1"; }

if [ "$create_roles" -eq 1 ]; then
    : "${RUNTIME_PASSWORD:?--create-roles needs RUNTIME_PASSWORD in the environment}"
    : "${MIGRATOR_PASSWORD:?--create-roles needs MIGRATOR_PASSWORD in the environment}"

    step "Creating roles"
    run_sql \
        --set runtime_password="$RUNTIME_PASSWORD" \
        --set migrator_password="$MIGRATOR_PASSWORD" \
        --file "$here/sql/01-roles.sql"
fi

if [ "$migrate" -eq 1 ]; then
    step "Applying migrations"

    # repo | --project | --startup-project | --context
    services=(
        "Users|UsersMicroservice.Core|UsersMicroservice|UserDbContext"
        "Permissions|Permissions.Core|Permissions|PermissionsDbContext"
        "Notifications|NotificationsService.Core|NotificationsService|NotificationsDbContext"
        "Comments|CommentsService.Core|CommentsService|CommentsDbContext"
        "Leads|LeadsService.Core|LeadsService|LeadsDbContext"
        "Invoices|InvoicesService.Core|InvoicesService|InvoicesDbContext"
        "Contacts|ContactsService.Core|ContactsService|ContactsDbContext"
        "Tasks|TasksService.Core|TasksService|TasksDbContext"
    )

    for entry in "${services[@]}"; do
        IFS='|' read -r repo project startup context <<< "$entry"
        if [ ! -d "$workspace/$repo" ]; then
            echo "    skip $repo (not checked out)"
            continue
        fi

        printf '    %-16s' "$repo"
        # --context is required in every repo referencing Permissions.Core, since two DbContexts are
        # then visible to the design-time tools.
        ( cd "$workspace/$repo" && dotnet ef database update \
            --project "$project" --startup-project "$startup" --context "$context" >/dev/null )
        echo "migrated"
    done
fi

# ALWAYS, even when this run did not migrate: an upgrade applied by any other route still leaves new
# tables ungranted and new audit tables unprotected.
step "Applying runtime grants"
run_sql \
    --set runtime_role="$runtime_role" \
    --set migrator_role="$migrator_role" \
    --file "$here/sql/02-runtime-grants.sql"

step "Installing append-only audit triggers"
run_sql --file "$here/sql/03-append-only-audit.sql"

step "Verifying"

# Two triggers per audit table. Reported rather than asserted against a fixed number, because the
# right count is however many services are deployed - but zero is always wrong, and 03 already fails
# on that.
triggers=$(run_sql --tuples-only --no-align --command \
    "SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'utils_audit_append_only%' AND NOT tgisinternal;")
echo "    append-only triggers: $triggers"

# THE CHECK LOOKS AT EVERY ROLE, NOT THE RUNTIME ONE, and that is deliberate.
#
# Checking the runtime role here would be close to vacuous: 02 has just revoked its UPDATE/DELETE/
# TRUNCATE three lines above, so the check would mostly re-verify what the previous statement did.
# (Confirmed the hard way - granting the runtime role UPDATE and re-running this script reported
# "clean", because the script had repaired it before looking.)
#
# What 02 does NOT fix is some OTHER role holding those privileges: a human DBA login, a reused
# application role, anything granted by hand. That is the drift worth catching, and nothing else
# looks for it.
#
# The owning role is excluded because it necessarily holds everything - it created the tables. It is
# not a hole: the append-only triggers from 03 refuse the owner too, which is the whole reason they
# exist alongside the grants.
rewriters=$(run_sql --tuples-only --no-align --command \
    "SELECT string_agg(DISTINCT grantee, ', ')
     FROM information_schema.role_table_grants
     WHERE table_name = 'AuditEvents'
       AND privilege_type IN ('UPDATE', 'DELETE', 'TRUNCATE')
       AND grantee <> '$migrator_role'
       AND grantee <> current_user;")

if [ -n "$rewriters" ]; then
    echo "    ERROR: these roles can rewrite audit history: $rewriters" >&2
    echo "    The append-only triggers still refuse them, but the grant should not exist." >&2
    echo "    Revoke it, then re-run this script." >&2
    exit 1
fi
echo "    no role other than $migrator_role can rewrite audit history"

printf '\nProvisioning complete.\n'
