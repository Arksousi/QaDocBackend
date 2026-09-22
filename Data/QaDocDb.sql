-- ============================================================
-- QaDoc database schema (PostgreSQL)
-- Idempotent: the API runs this automatically every time it starts,
-- and it is also safe to run by hand (psql, pgAdmin, Railway's Data tab).
--
-- No user is created here: the app shows a one-time "Create admin account"
-- screen while the users table is empty.
-- ============================================================

-- ---------- Users ----------
CREATE TABLE IF NOT EXISTS users (
    userid       INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username     VARCHAR(50)  NOT NULL,
    displayname  VARCHAR(100) NOT NULL,
    passwordhash VARCHAR(200) NOT NULL,                 -- ASP.NET Core PasswordHasher (salted PBKDF2), never plain text
    role         VARCHAR(20)  NOT NULL DEFAULT 'Tester' CHECK (role IN ('Admin', 'Developer', 'Tester')),
    isactive     BOOLEAN      NOT NULL DEFAULT TRUE,
    tokenversion INTEGER      NOT NULL DEFAULT 1,      -- bumped to sign the user out everywhere
    createdat    TIMESTAMPTZ  NOT NULL DEFAULT now()
);
-- Case-insensitive: "Dana" and "dana" count as the same username
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_username ON users (lower(username));

-- ---------- Projects ----------
-- projectcode is the first segment of every ticket key: RMS in RMS-V1-0001.
CREATE TABLE IF NOT EXISTS projects (
    projectid       INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    projectname     VARCHAR(150) NOT NULL,
    projectcode     VARCHAR(10) NOT NULL,
    createdbyuserid INTEGER NULL REFERENCES users (userid),
    -- Sample data for the "Continue as a guest" tour. Guests see only these; everyone else
    -- sees only the rest. ProjectAccessService applies the same rule in code.
    isdemo          BOOLEAN NOT NULL DEFAULT FALSE,
    createdat       TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- The unique index on projectcode is created in the migration section at the end of this file:
-- on a database made before codes existed the column is only added down there, and
-- CREATE INDEX IF NOT EXISTS still resolves its columns, so indexing it here would error.

-- ---------- Folders ----------
-- The layer between a project and its tickets: Project - Folder - Ticket.
-- foldercode is the middle segment of a ticket key (V1 in RMS-V1-0001), and nextsequence
-- hands out the trailing number. Each folder counts from 1, so V1 and V2 both start at 0001.
CREATE TABLE IF NOT EXISTS folders (
    folderid        INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    projectid       INTEGER NOT NULL REFERENCES projects (projectid) ON DELETE CASCADE,
    foldername      VARCHAR(150) NOT NULL,
    foldercode      VARCHAR(10) NOT NULL,
    nextsequence    INTEGER NOT NULL DEFAULT 1,
    createdbyuserid INTEGER NULL REFERENCES users (userid),
    createdat       TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- Codes only have to be unique inside their project, so two projects can both have a V1.
CREATE UNIQUE INDEX IF NOT EXISTS ux_folders_project_code ON folders (projectid, upper(foldercode));

-- ---------- Project members ----------
-- Who may see a project, and how much they may do in it. A user with no row here has no
-- access at all; global Admins bypass this table entirely.
--   Viewer      - read tickets, post comments
--   Contributor - also create, edit and assign tickets
--   Manager     - also add and remove this project's members
-- Deliberately NOT backfilled: existing projects start with no members, so only Admins can
-- reach them until someone is added.
CREATE TABLE IF NOT EXISTS projectmembers (
    projectid INTEGER NOT NULL REFERENCES projects (projectid) ON DELETE CASCADE,
    userid    INTEGER NOT NULL REFERENCES users (userid) ON DELETE CASCADE,
    role      VARCHAR(20) NOT NULL DEFAULT 'Viewer'
              CHECK (role IN ('Viewer', 'Contributor', 'Manager')),
    addedat   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (projectid, userid)
);
CREATE INDEX IF NOT EXISTS ix_projectmembers_user ON projectmembers (userid);

-- ---------- Tickets ----------
-- projectid is kept alongside folderid: access is checked against the project on nearly every
-- request, and carrying it here avoids a join on that hot path. The API keeps the two in step.
-- sequence is the ticket's number within its folder; the displayed key is built from
-- projectcode + foldercode + sequence rather than stored, so renaming a code renames its keys.
CREATE TABLE IF NOT EXISTS tickets (
    ticketid         INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    projectid        INTEGER NOT NULL REFERENCES projects (projectid) ON DELETE CASCADE,
    folderid         INTEGER NOT NULL REFERENCES folders (folderid) ON DELETE CASCADE,
    sequence         INTEGER NOT NULL,
    title            VARCHAR(200) NOT NULL,
    description      TEXT NULL,                         -- HTML; pictures embedded as data URLs
    tickettype       VARCHAR(20) NOT NULL DEFAULT 'Bug'
                     CHECK (tickettype IN ('Bug', 'Enhancement', 'Issue')),
    assignedtouserid INTEGER NULL REFERENCES users (userid),
    -- Who put the current assignee there: the creator, or whoever changed it last.
    assignedbyuserid INTEGER NULL REFERENCES users (userid),
    state            VARCHAR(20) NOT NULL DEFAULT 'Open'
                     CHECK (state IN ('Open', 'In Progress', 'Resolved', 'Retest', 'Closed')),
    priority         SMALLINT NOT NULL DEFAULT 3 CHECK (priority BETWEEN 1 AND 4),
    impact           VARCHAR(20) NOT NULL DEFAULT 'Medium'
                     CHECK (impact IN ('Low', 'Medium', 'High', 'Critical', 'Showstopper')),
    createdbyuserid  INTEGER NULL REFERENCES users (userid),
    updatedbyuserid  INTEGER NULL REFERENCES users (userid),
    createdat        TIMESTAMPTZ NOT NULL DEFAULT now(),
    activitydate     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_tickets_project_activity ON tickets (projectid, activitydate DESC);
CREATE INDEX IF NOT EXISTS ix_tickets_assignedto ON tickets (assignedtouserid);

-- ---------- Ticket tags (many per ticket) ----------
CREATE TABLE IF NOT EXISTS tickettags (
    ticketid INTEGER NOT NULL REFERENCES tickets (ticketid) ON DELETE CASCADE,
    tag      VARCHAR(50) NOT NULL,
    PRIMARY KEY (ticketid, tag)
);
CREATE INDEX IF NOT EXISTS ix_tickettags_tag ON tickettags (lower(tag));

-- ---------- Ticket comments (thread) ----------
CREATE TABLE IF NOT EXISTS ticketcomments (
    commentid    INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticketid     INTEGER NOT NULL REFERENCES tickets (ticketid) ON DELETE CASCADE,
    authoruserid INTEGER NULL REFERENCES users (userid),  -- always set by the API
    text         TEXT NOT NULL,
    createdat    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_ticketcomments_ticket ON ticketcomments (ticketid, createdat);

-- ---------- Ticket change history ----------
CREATE TABLE IF NOT EXISTS tickethistory (
    historyid INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticketid  INTEGER NOT NULL REFERENCES tickets (ticketid) ON DELETE CASCADE,
    userid    INTEGER NULL REFERENCES users (userid),
    field     VARCHAR(50) NOT NULL,                     -- 'Created', 'Title', 'Description', 'Assigned To', 'State', 'Priority', 'Impact', 'Tag'
    oldvalue  TEXT NULL,
    newvalue  TEXT NULL,
    changedat TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_tickethistory_ticket ON tickethistory (ticketid, changedat);

-- ---------- Ticket attachments ----------
-- Files too big to sit inline in a description as a data URL (videos above all). The
-- description references one by id; the bytes are streamed from GET /api/attachments/{id}.
-- projectid is what access is checked against, and is set at upload time because an
-- attachment can be added while composing a ticket that does not exist yet.
CREATE TABLE IF NOT EXISTS ticketattachments (
    attachmentid     INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    projectid        INTEGER NOT NULL REFERENCES projects (projectid) ON DELETE CASCADE,
    ticketid         INTEGER NULL REFERENCES tickets (ticketid) ON DELETE CASCADE,
    filename         VARCHAR(255) NOT NULL,
    contenttype      VARCHAR(100) NOT NULL,
    bytesize         BIGINT NOT NULL,
    content          BYTEA NOT NULL,
    uploadedbyuserid INTEGER NULL REFERENCES users (userid),
    createdat        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_ticketattachments_project ON ticketattachments (projectid);

-- ============================================================
-- Migrations for databases created by an earlier version.
-- CREATE TABLE IF NOT EXISTS skips an existing table outright, so column and constraint
-- changes have to be applied explicitly. Each step is safe to re-run.
-- ============================================================

-- Impact gained 'Showstopper' (above Critical). Drop-then-add keeps this idempotent:
-- the constraint is recreated from scratch on every start.
ALTER TABLE tickets DROP CONSTRAINT IF EXISTS tickets_impact_check;
ALTER TABLE tickets ADD CONSTRAINT tickets_impact_check
    CHECK (impact IN ('Low', 'Medium', 'High', 'Critical', 'Showstopper'));

-- The single 'Member' role split into 'Developer' and 'Tester'. Neither carries any privilege
-- of its own -- what a person may do still comes from their per-project membership -- so the
-- old Members all become Testers and can be moved across one at a time.
-- The rows are rewritten before the new constraint goes on, or it would reject them.
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_role_check;
UPDATE users SET role = 'Tester' WHERE role NOT IN ('Admin', 'Developer', 'Tester');
ALTER TABLE users ALTER COLUMN role SET DEFAULT 'Tester';
ALTER TABLE users ADD CONSTRAINT users_role_check
    CHECK (role IN ('Admin', 'Developer', 'Tester'));

-- Projects gained a demo flag for the guest tour. Existing projects are real work, so they
-- default to FALSE and stay invisible to guests until something marks them.
ALTER TABLE projects ADD COLUMN IF NOT EXISTS isdemo BOOLEAN NOT NULL DEFAULT FALSE;
CREATE INDEX IF NOT EXISTS ix_projects_isdemo ON projects (isdemo);

-- Tickets gained a type (Bug / Enhancement, later Issue) and a record of who assigned them.
-- Existing rows become Bugs, which is what they all were.
ALTER TABLE tickets ADD COLUMN IF NOT EXISTS tickettype VARCHAR(20) NOT NULL DEFAULT 'Bug';
ALTER TABLE tickets DROP CONSTRAINT IF EXISTS tickets_tickettype_check;
ALTER TABLE tickets ADD CONSTRAINT tickets_tickettype_check
    CHECK (tickettype IN ('Bug', 'Enhancement', 'Issue'));
ALTER TABLE tickets ADD COLUMN IF NOT EXISTS assignedbyuserid INTEGER NULL REFERENCES users (userid);

-- ---------- Project - Folder - Ticket ----------
-- Added nullable first so existing rows survive; the block below fills them and the
-- constraints are tightened afterwards. On a fresh database every step is a no-op.
ALTER TABLE projects ADD COLUMN IF NOT EXISTS projectcode VARCHAR(10);
ALTER TABLE tickets  ADD COLUMN IF NOT EXISTS folderid INTEGER REFERENCES folders (folderid) ON DELETE CASCADE;
ALTER TABLE tickets  ADD COLUMN IF NOT EXISTS sequence INTEGER;

DO $$
DECLARE
    p          RECORD;
    t          RECORD;
    base       TEXT;
    candidate  TEXT;
    suffix     INT;
    target     INT;
    nextnumber INT;
BEGIN
    -- 1. Give every project a code. Multi-word names become initials
    --    ("Restaurant Management System" -> RMS); single words are truncated ("POS" -> POS).
    FOR p IN SELECT projectid, projectname FROM projects WHERE projectcode IS NULL OR projectcode = '' LOOP
        IF array_length(regexp_split_to_array(btrim(p.projectname), '\s+'), 1) > 1 THEN
            base := array_to_string(ARRAY(
                SELECT upper(left(w, 1)) FROM regexp_split_to_table(btrim(p.projectname), '\s+') w WHERE w <> ''
            ), '');
        ELSE
            base := upper(left(regexp_replace(p.projectname, '[^A-Za-z0-9]', '', 'g'), 4));
        END IF;

        base := left(regexp_replace(base, '[^A-Z0-9]', '', 'g'), 10);
        IF base = '' THEN base := 'P' || p.projectid; END IF;

        -- Names can collide ("Point Of Sale" and "POS" both want POS): number the later ones.
        candidate := base;
        suffix := 1;
        WHILE EXISTS (SELECT 1 FROM projects WHERE upper(projectcode) = candidate) LOOP
            suffix := suffix + 1;
            candidate := left(base, 8) || suffix;
        END LOOP;

        UPDATE projects SET projectcode = candidate WHERE projectid = p.projectid;
    END LOOP;

    -- 2. Every project needs somewhere to put its tickets.
    INSERT INTO folders (projectid, foldername, foldercode, createdbyuserid)
    SELECT pr.projectid, 'General', 'GEN', pr.createdbyuserid
    FROM projects pr
    WHERE NOT EXISTS (SELECT 1 FROM folders f WHERE f.projectid = pr.projectid);

    -- 3. Move existing tickets into their project's first folder, numbered by creation order
    --    so the oldest ticket becomes 0001.
    FOR t IN SELECT ticketid, projectid FROM tickets WHERE folderid IS NULL ORDER BY projectid, ticketid LOOP
        SELECT folderid INTO target FROM folders WHERE projectid = t.projectid ORDER BY folderid LIMIT 1;
        UPDATE folders SET nextsequence = nextsequence + 1
        WHERE folderid = target
        RETURNING nextsequence - 1 INTO nextnumber;
        UPDATE tickets SET folderid = target, sequence = nextnumber WHERE ticketid = t.ticketid;
    END LOOP;
END $$;

-- Now that nothing is blank, make the shape permanent.
ALTER TABLE projects ALTER COLUMN projectcode SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_code ON projects (upper(projectcode));
ALTER TABLE tickets  ALTER COLUMN folderid SET NOT NULL;
ALTER TABLE tickets  ALTER COLUMN sequence SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_tickets_folder_sequence ON tickets (folderid, sequence);
CREATE INDEX IF NOT EXISTS ix_tickets_folder ON tickets (folderid);
