# QaDoc API

The backend for [QaDoc](../README.md), a JIRA-style ticket tracker for QA teams. ASP.NET Core 9 Web API, PostgreSQL over plain ADO.NET (Npgsql), JWT authentication.

No ORM and no migrations tool: queries are hand-written SQL in the repositories, and the schema is one idempotent script the API applies to itself at startup.

## Stack

| Piece | Choice |
|-------|--------|
| Runtime | .NET 9 (`net9.0`) |
| Data access | Npgsql 10 — raw `NpgsqlCommand`, no EF Core |
| Database | PostgreSQL |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` 9, ASP.NET `PasswordHasher` |
| Docs | Swashbuckle / Swagger UI at `/swagger` |

## Layout

```
Controllers/      HTTP surface, one per resource
Repositories/     SQL per aggregate (Project, Ticket, Folder, User, Attachment, ProjectMember)
Data/             QaDocDb.sql (embedded), SchemaInitializer, SqlConnectionFactory, SqlExtensions
Infrastructure/   TokenService, ProjectAccessService, GuestReadOnlyFilter, ApiExceptionHandler
Models/           Records for rows and request/response bodies
Program.cs        Composition root: DI, JWT, CORS, pipeline
```

A request goes `Controller → I*Repository → ISqlConnectionFactory → Postgres`. Controllers hold authorization and validation; repositories hold SQL and nothing else. Every service is registered explicitly in [Program.cs](Program.cs) — start there when tracing anything.

## Running it

Needs the .NET 9 SDK and a reachable PostgreSQL.

```powershell
# from the repository root: Postgres on 5432 (user qadoc / password qadoc_dev)
docker compose up -d db

dotnet run --project QaDocBackend --launch-profile http
```

The API listens on http://localhost:5134. Open `/swagger`, call `POST /api/auth/login`, then paste the token into **Authorize** to exercise the protected endpoints. [QaDocBackend.http](QaDocBackend.http) has the same calls for the IDE's HTTP client.

`appsettings.Development.json` carries a local connection string and a throwaway JWT key so a fresh clone runs without setup. Both are development-only values — never reuse them anywhere reachable.

Whole-stack instructions (Docker Compose, the Angular UI, Railway) live in the [root README](../README.md).

## Configuration

| Setting | Environment variable | Notes |
|---------|---------------------|-------|
| `ConnectionStrings:DefaultConnection` | `DATABASE_URL` | `DATABASE_URL` wins when set, and accepts the `postgresql://user:pass@host:port/db` form hosts hand out. See [SqlConnectionFactory.cs](Data/SqlConnectionFactory.cs) |
| `Jwt:Key` | `Jwt__Key` | Signing key, at least 32 bytes. The API **refuses to start** without one — a fail-fast in `JwtSettings.SigningKey()` |
| `Jwt:Issuer`, `Jwt:Audience`, `Jwt:ExpiryHours` | — | Default to `QaDoc`, `QaDoc`, 8 hours |
| `Cors:Origins` | `Cors__Origins__0`, `__1`, … | Allowed front-end origins; falls back to `http://localhost:4200`. An entry may contain `*` for one label (`https://qadocfrontend-*-qad-oc.vercel.app`) to admit preview deployments |

The published image (see [Dockerfile](Dockerfile)) listens on `$PORT` when the host sets one, otherwise 8080.

## Database

[Data/QaDocDb.sql](Data/QaDocDb.sql) is embedded in the assembly and applied by `SchemaInitializer` on every startup, retrying for a minute while the database boots. It is written to be idempotent — `CREATE TABLE IF NOT EXISTS` plus explicit `ALTER`s for columns added later, since `IF NOT EXISTS` skips an existing table outright. There is no migration step to run by hand; change the script and restart.

Tables: `users`, `projects`, `folders`, `projectmembers`, `tickets`, `ticketassignees`, `tickettags`, `ticketcomments`, `tickethistory`, `ticketattachments`, `notifications`.

## Authentication and authorization

Four layers, each in one place:

1. **Fallback policy** — every endpoint requires an authenticated user unless it is marked `[AllowAnonymous]`. Only `/api/auth/status`, `/setup`, `/login` and `/guest` are open.
2. **Per-request revalidation** — `OnTokenValidated` reloads the account on every request and compares its `TokenVersion`, so deactivation, a role change or a password reset takes effect immediately rather than when the token expires. The role claim is attached from the database row, not trusted from the token.
3. **Global roles** — `Admin`, `Leader`, `Developer`, `Tester`. `[Authorize(Roles = Roles.Admin)]` guards user management, ticket deletion and the demo-project endpoints. `[Authorize(Roles = Roles.Leads)]` (Admin or Leader) guards project creation and `GET /api/projects/scoreboard`, the Leader Dashboard.
4. **Per-project access** — `IProjectAccessService` answers "what may this user do here" once, for every controller: `Manager` > `Contributor` > `Viewer` > `None`. The `projectmembers` table stores only `Viewer` or `Contributor`; `Manager` is derived — a Contributor whose account role is `Leader` gets it, and global admins are short-circuited to it. It is also the seam between demo projects and real ones: a guest sees only demo projects, a real account only real ones.

Guest tokens name no account. `GuestReadOnlyFilter` rejects anything that is not `GET`/`HEAD`/`OPTIONS` before the action runs, so an endpoint added later is read-only for guests by default.

Attachment downloads accept `?access_token=` because a `<video src>` cannot send an `Authorization` header. That is deliberately confined to `/api/attachments` — elsewhere the token would leak into browser history and logs.

Errors go through `ApiExceptionHandler` and come back as RFC 7807 `ProblemDetails`.

## Endpoints

### `/api/auth`
| Method | Route | Who |
|--------|-------|-----|
| GET | `/status` | anonymous — reports whether the first admin still needs creating |
| POST | `/setup` | anonymous — creates the first admin; refused once any user exists |
| POST | `/login` | anonymous |
| POST | `/guest` | anonymous — read-only token for the demo projects |
| GET | `/me` | signed in |
| POST | `/change-password` | signed in |

### `/api/projects`
| Method | Route | Who |
|--------|-------|-----|
| GET | `/`, `/recent?top=`, `/{id}` | any access |
| POST | `/` | Admin or Leader |
| GET | `/scoreboard` | Admin or Leader — the Leader Dashboard |
| GET | `/demo`, PUT `/{id}/demo`, DELETE `/{id}` | Admin |
| GET | `/{id}/tickets` | project access — supports search, filters and sorting |
| GET | `/{id}/suggestions`, `/{id}/assignees` | project access |
| GET | `/{id}/members`, PUT `/{id}/members`, DELETE `/{id}/members/{userId}` | Leader + Contributor, or Admin |

### `/api/projects/{projectId}/folders`
`GET` · `POST` · `PUT /{folderId}` · `DELETE /{folderId}`

### `/api/tickets`
`GET /{id}` · `POST` · `PUT /{id}` · `POST /{id}/comments` · `DELETE /{id}` (Admin). Every field change is written to `tickethistory`. A ticket can have up to 10 assignees (`assignedToUserIds`), stored in `ticketassignees` with who added each; a save writes only the difference, and each person added gets a row in `notifications`, in the same transaction. `POST /{id}/comments` takes optional `mentionedUserIds`: Contributors (and Admins) on the project are stored in `commentmentions` and notified with kind `Mentioned`; anyone else, and the author, is dropped quietly. A Leader who contributes (or an Admin) may assign someone outside the project, who is made a Contributor first.

### `/api/notifications`
The caller's own, never anyone else's: `GET /?top=` (newest first, only for projects they can still open), `GET /unread-count` (polled by the app every minute), `POST /{id}/read` (someone else's id is a 404) and `POST /read-all`.

### `/api/attachments`
`POST` (multipart, Contributor or better) and `GET /{id}`. Videos only — MP4, WebM, Ogg, QuickTime — capped by `Attachments.MaxBytes` and stored as bytes in `ticketattachments`. Downloads enable range processing so the browser can seek.

### `/api/users`
`GET /options` for everyone; `GET /workload` (the Users Dashboard) and `GET /{id}/card` (the avatar hover card; it lists only projects the caller can open) for Admins and Leaders; `GET /`, `POST /`, `PUT /{id}`, `PUT /{id}/ticket-limit` and `POST /{id}/reset-password` are Admin only. Users carry an optional `ticketLimit` (1–100, null for none). `GET /options`, `GET /api/projects/{id}/assignees` and the scoreboard also return each person's `openTickets` — unfinished tickets across all real projects, one definition in `Data/Workload.cs` — so the app can warn; nothing is ever refused for being over a limit.

## Tests

The authorization matrix runs end to end from the repository root:

```powershell
.\run-authz-tests.ps1
```

It starts a throwaway Postgres container on port 55432 with a tmpfs volume, boots the API against it, runs the Playwright suite, then tears everything down. Nothing touches a deployed environment.
