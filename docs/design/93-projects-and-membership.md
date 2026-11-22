# 93 Projects and membership

Status: PROPOSED (2026-11-22). Nothing here is DECIDED. Docs first: the code PR follows this one.
Depends on: `04-postgres-schema-and-migrations.md`, `12-kanban-and-dependencies.md`, `24-company-systems-core.md` section 2 (Org chart, loans), `08-memory-system.md` (project areas), `18-vault.md`, `38-ryusei-fuda.md`.

## 1. Why this doc exists

Several designs say "the project's members" and none defines the project or its members. `tasks.project_id`, `calendar.project_id` and `org_relation.project_id` are bare uuids, and their SQL comments say the project table arrives later. Doc 12 limits board reads to the owner because "no table links people or agents to a project". Doc 08 and doc 18 (project areas, `projects/<slug>/**`, `twin/<slug>/**`) key read access on `IProjectMembership`, which has no production source, so an agent reads no project area on a live server. This doc defines that source and nothing else.

## 2. Scope

In scope: the `project` table, the member link, how loans count, the `IProjectMembership` source, and its change audit. Out of scope: boards, tasks, schedules, the Org view and anything Kanban or RyuseiFuda own. Those keep their own tables and only gain a foreign key to `project`.

## 3. Model [PROPOSED]

```sql
project (id uuid PK, slug text UNIQUE NOT NULL CHECK (slug ~ '^[a-z0-9][a-z0-9-]{0,62}$'),
         title text NOT NULL, state text CHECK (state IN ('active','archived')),
         created_by, created_at, version bigint)
project_member (project_id, principal_kind text CHECK (principal_kind IN ('agent','user')),
                principal_id uuid, role text CHECK (role IN ('lead','member')),
                starts_at, ends_at NULL, added_by, version bigint,
                PRIMARY KEY (project_id, principal_kind, principal_id))
```

- The slug is immutable once set. It is the path segment in the Vault, so a rename is a move plus a new project, not an edit.
- `project_id` columns elsewhere may gain a foreign key to `project(id)` in the module that owns each table, in its own change. This doc does not force it.
- Titles are free text and follow the encrypted-personal-fields rule of doc 04 where they hold personal data.

## 4. Who counts as a member [PROPOSED]

- **Agents:** a row in `project_member` that is current (`starts_at <= now`, `ends_at` null or later), **or** a current `org_relation` row of kind `lent_to` whose `project_id` matches and whose `to_agent_id` is the agent. Doc 24 already says a loan adds the agent to the project's member list with an end date; this section states the read path. A loan row is not copied into `project_member`; the membership source unions the two, so a loan that ends stops granting access with no cleanup job. Recommended answer to the open choice "do loans count": yes, as above.
- **Humans:** only through `project_member` rows. The owner reads every project without a row (doc 12, doc 18).
- **`reports_to` edges grant nothing** (doc 24, "No authority").
- **Archived projects:** members keep read access, nobody gains write access. Archiving never deletes rows.
- **Fail closed:** an unknown principal, an unknown project, a malformed slug or a database error yields no membership, never all.

## 5. Interface [PROPOSED]

`IProjectMembership.ProjectsOf(VaultPrincipal)` (already in `Bazaar.Server/Memory/Access`) returns the immutable slug set. The production implementation lives in a module (`Bazaar.Server.Projects` with `.Abstractions`), not in `Bazaar.Server`. It reads `project`, `project_member` and `org_relation` in one query and caches nothing beyond a request (doc 48: changes take effect at the next request). The module also owns the write API (`create`, `add_member`, `end_membership`, `archive`), each audited with actor, diff and time (doc 48), and `project.*` settings.

## 6. Settings [PROPOSED]

| Setting | Default | Notes |
|---|---|---|
| `projects.creators` | `owner` | Who may create a project; widening is audited. |
| `projects.member_adders` | `lead_and_owner` | Who may add members. Adding an agent follows the Council rule of doc 10 where that doc requires a vote. |
| `projects.loans_grant_read` | `true` | Recommended answer 4. A false value restricts project areas to `project_member` rows only. May tighten, never loosen below the owner-only floor. |

## 7. Open questions

1. Does adding an agent to a project need a Council vote (doc 10, membership motions) or only the lead? Recommended: lead adds, Council only when the project's `projects.member_adders` is set to `council`.
2. Should project GCs be created with the project and their membership follow `project_member`? Recommended: yes, through `IGcProvisioner`, as a follow-up.
3. Kanban board reads (doc 12) widening to project members is a separate owner decision that this doc enables but does not make.
