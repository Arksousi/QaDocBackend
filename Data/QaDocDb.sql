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
    role         VARCHAR(20)  NOT NULL DEFAULT 'Tester' CHECK (role IN ('Admin', 'Leader', 'Developer', 'Tester')),
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
-- Managing the project (members, folders) is not stored: it belongs to a Contributor whose
-- account role is Leader, and to Admins. ProjectAccessService works it out.
-- Deliberately NOT backfilled: existing projects start with no members, so only Admins can
-- reach them until someone is added.
CREATE TABLE IF NOT EXISTS projectmembers (
    projectid INTEGER NOT NULL REFERENCES projects (projectid) ON DELETE CASCADE,
    userid    INTEGER NOT NULL REFERENCES users (userid) ON DELETE CASCADE,
    role      VARCHAR(20) NOT NULL DEFAULT 'Viewer'
              CHECK (role IN ('Viewer', 'Contributor')),
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
    -- Superseded by ticketassignees (a ticket can have several people) and no longer written.
    -- Kept for one release so the previous API can still be rolled back to; drop them after.
    assignedtouserid INTEGER NULL REFERENCES users (userid),
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

-- ---------- Notifications ----------
-- "X assigned a ticket to you", shown in the app's bell. Only the pointers are stored: the key,
-- title and project are joined in when read, so renaming a code never leaves a stale message.
-- readat stays NULL until the person opens it or marks everything read.
CREATE TABLE IF NOT EXISTS notifications (
    notificationid INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    userid         INTEGER NOT NULL REFERENCES users (userid) ON DELETE CASCADE,
    ticketid       INTEGER NOT NULL REFERENCES tickets (ticketid) ON DELETE CASCADE,
    actoruserid    INTEGER NULL REFERENCES users (userid),
    createdat      TIMESTAMPTZ NOT NULL DEFAULT now(),
    readat         TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS ix_notifications_user ON notifications (userid, createdat DESC);

-- ---------- Comment mentions ----------
-- Who an @mention in a comment points at. Stored rather than parsed back out of the text, so a
-- name that changes later, or two people with similar names, never mislinks an old comment.
CREATE TABLE IF NOT EXISTS commentmentions (
    commentid INTEGER NOT NULL REFERENCES ticketcomments (commentid) ON DELETE CASCADE,
    userid    INTEGER NOT NULL REFERENCES users (userid) ON DELETE CASCADE,
    PRIMARY KEY (commentid, userid)
);

-- Notifications gained a kind: 'Assigned' (every row before this) or 'Mentioned', which also
-- points at the comment. Existing rows take the default, which is what they all were.
ALTER TABLE notifications ADD COLUMN IF NOT EXISTS kind VARCHAR(20) NOT NULL DEFAULT 'Assigned';
-- 'Retest' (added later) tells the assignees their ticket was sent back to be tested again.
ALTER TABLE notifications DROP CONSTRAINT IF EXISTS notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check CHECK (kind IN ('Assigned', 'Mentioned', 'Retest'));
ALTER TABLE notifications ADD COLUMN IF NOT EXISTS commentid INTEGER NULL REFERENCES ticketcomments (commentid) ON DELETE CASCADE;

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
-- Later, 'Leader' joined: the one role besides Admin allowed to create projects. Nobody is
-- moved into it here; an Admin promotes people from the Users page.
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_role_check;
UPDATE users SET role = 'Tester' WHERE role NOT IN ('Admin', 'Leader', 'Developer', 'Tester');
ALTER TABLE users ALTER COLUMN role SET DEFAULT 'Tester';
ALTER TABLE users ADD CONSTRAINT users_role_check
    CHECK (role IN ('Admin', 'Leader', 'Developer', 'Tester'));

-- The 'Manager' project role was retired: managing a project now comes from being a Leader
-- who is a Contributor on it. Old Managers keep editing tickets as Contributors; those who
-- should still manage need the Leader account role. Rows first, or the constraint rejects them.
ALTER TABLE projectmembers DROP CONSTRAINT IF EXISTS projectmembers_role_check;
UPDATE projectmembers SET role = 'Contributor' WHERE role NOT IN ('Viewer', 'Contributor');
ALTER TABLE projectmembers ADD CONSTRAINT projectmembers_role_check
    CHECK (role IN ('Viewer', 'Contributor'));

-- Users gained an optional ticket limit: how many unfinished tickets they should hold at once,
-- across every project. NULL means no limit. Only a guide -- the app warns, it never refuses.
ALTER TABLE users ADD COLUMN IF NOT EXISTS ticketlimit INTEGER NULL;
ALTER TABLE users DROP CONSTRAINT IF EXISTS users_ticketlimit_check;
ALTER TABLE users ADD CONSTRAINT users_ticketlimit_check CHECK (ticketlimit IS NULL OR ticketlimit BETWEEN 1 AND 100);

-- Users gained a profile they fill in themselves: contact details and a picture. All optional.
-- avatarversion counts picture changes, so the app can cache a picture until the number moves.
ALTER TABLE users ADD COLUMN IF NOT EXISTS email VARCHAR(254) NULL;
ALTER TABLE users ADD COLUMN IF NOT EXISTS jobtitle VARCHAR(100) NULL;
ALTER TABLE users ADD COLUMN IF NOT EXISTS phone VARCHAR(40) NULL;
ALTER TABLE users ADD COLUMN IF NOT EXISTS bio VARCHAR(500) NULL;
ALTER TABLE users ADD COLUMN IF NOT EXISTS avatarversion INTEGER NOT NULL DEFAULT 0;

-- The picture itself, in its own table so listing users never drags image bytes along.
-- One row per user at most; the app sends a small square (256px) already, so rows stay small.
CREATE TABLE IF NOT EXISTS useravatars (
    userid      INTEGER PRIMARY KEY REFERENCES users (userid) ON DELETE CASCADE,
    contenttype VARCHAR(50) NOT NULL,
    content     BYTEA NOT NULL,
    updatedat   TIMESTAMPTZ NOT NULL DEFAULT now()
);

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

-- ---------- Ticket assignees (many per ticket) ----------
-- assignedbyuserid is per person: whoever added them, which is who they answer to for it.
-- Last in the file so every column it copies exists, however old the database.
-- Created and back-filled in one guarded step: the copy from the old single-assignee column must
-- happen exactly once. Were it re-run on every start, it would put back anyone removed since.
DO $$
BEGIN
    IF to_regclass('ticketassignees') IS NULL THEN
        CREATE TABLE ticketassignees (
            ticketid         INTEGER NOT NULL REFERENCES tickets (ticketid) ON DELETE CASCADE,
            userid           INTEGER NOT NULL REFERENCES users (userid) ON DELETE CASCADE,
            assignedbyuserid INTEGER NULL REFERENCES users (userid),
            assignedat       TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (ticketid, userid)
        );
        INSERT INTO ticketassignees (ticketid, userid, assignedbyuserid, assignedat)
        SELECT ticketid, assignedtouserid, assignedbyuserid, activitydate
        FROM tickets WHERE assignedtouserid IS NOT NULL;
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_ticketassignees_user ON ticketassignees (userid);

-- Back to one person per ticket. Runs once, guarded by the unique index it ends by creating:
-- a ticket that had several people keeps whoever was added first (ties: the lower user id), and
-- its history records the change, attributed to no user (shown as "QaDoc"), so nobody is
-- silently dropped. The index then stops a second assignee from ever being written again.
DO $$
BEGIN
    IF to_regclass('ux_ticketassignees_one_per_ticket') IS NULL THEN
        CREATE TEMP TABLE ranked_assignees ON COMMIT DROP AS
        SELECT a.ticketid, a.userid, u.displayname,
               row_number() OVER (PARTITION BY a.ticketid ORDER BY a.assignedat, a.userid) AS rn,
               count(*)     OVER (PARTITION BY a.ticketid) AS n
        FROM ticketassignees a JOIN users u ON u.userid = a.userid;

        INSERT INTO tickethistory (ticketid, userid, field, oldvalue, newvalue)
        SELECT ticketid, NULL, 'Assigned To',
               string_agg(displayname, ', ' ORDER BY rn),
               max(displayname) FILTER (WHERE rn = 1)
        FROM ranked_assignees WHERE n > 1 GROUP BY ticketid;

        DELETE FROM ticketassignees a
        USING ranked_assignees r
        WHERE r.ticketid = a.ticketid AND r.userid = a.userid AND r.rn > 1;

        CREATE UNIQUE INDEX ux_ticketassignees_one_per_ticket ON ticketassignees (ticketid);
    END IF;
END $$;

-- ============================================================
-- Test Case Generator: suites, screenshots and test cases.
-- A suite belongs to a project and reuses project membership, so there is no second
-- permission system here to keep in step with the first.
-- ============================================================

-- ---------- Test suites ----------
-- isdemo mirrors projects.isdemo (copied when the suite is created): a guest may only browse
-- the suites of a demo project, exactly as they may only browse demo projects' tickets.
-- nextsequence hands out the per-suite test case number under a row lock, the same way
-- folders.nextsequence hands out ticket numbers, so two creates never share a number.
CREATE TABLE IF NOT EXISTS testsuites (
    suiteid             INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    projectid           INTEGER NOT NULL REFERENCES projects (projectid) ON DELETE CASCADE,
    folderid            INTEGER NULL REFERENCES folders (folderid) ON DELETE SET NULL,
    title               VARCHAR(200) NOT NULL,
    businessdescription TEXT NULL,
    createdbyuserid     INTEGER NULL REFERENCES users (userid),
    createdat           TIMESTAMPTZ NOT NULL DEFAULT now(),
    isdemo              BOOLEAN NOT NULL DEFAULT FALSE,
    nextsequence        INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_testsuites_project ON testsuites (projectid);

-- ---------- Suite screenshots ----------
-- The bytes live in ticketattachments (with no ticket), so a screenshot shares the storage,
-- the picture pipeline and GET /api/attachments/{id} with every other picture in the app.
CREATE TABLE IF NOT EXISTS testsuitescreens (
    screenid            INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    suiteid             INTEGER NOT NULL REFERENCES testsuites (suiteid) ON DELETE CASCADE,
    attachmentid        INTEGER NOT NULL REFERENCES ticketattachments (attachmentid) ON DELETE CASCADE,
    sortorder           INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_testsuitescreens_suite ON testsuitescreens (suiteid, sortorder);

-- ---------- Test cases ----------
-- number is the counter within its suite and is shown as TC-0001; steps is a JSONB array of
-- strings. linkedticketid is set null when the ticket goes: a test case must never be what
-- stops a ticket from being deleted.
CREATE TABLE IF NOT EXISTS testcases (
    testcaseid     INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    suiteid        INTEGER NOT NULL REFERENCES testsuites (suiteid) ON DELETE CASCADE,
    number         INTEGER NOT NULL,
    title          VARCHAR(200) NOT NULL,
    category       VARCHAR(20) NOT NULL DEFAULT 'Functional'
                   CHECK (category IN ('Functional', 'Negative', 'Boundary', 'UI')),
    priority       SMALLINT NOT NULL DEFAULT 3 CHECK (priority BETWEEN 1 AND 4),
    preconditions  TEXT NULL,
    steps          JSONB NOT NULL DEFAULT '[]'::jsonb,
    expected       TEXT NULL,
    status         VARCHAR(20) NOT NULL DEFAULT 'Draft'
                   CHECK (status IN ('Draft', 'Approved', 'Passed', 'Failed')),
    linkedticketid INTEGER NULL REFERENCES tickets (ticketid) ON DELETE SET NULL,
    source         VARCHAR(20) NOT NULL DEFAULT 'Manual'
                   CHECK (source IN ('Ai', 'Imported', 'Manual')),
    createdat      TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- Both halves of the numbering rule: the counter is taken under a lock, and this index is what
-- would catch a collision. Two suites may each hold their own TC-0001.
CREATE UNIQUE INDEX IF NOT EXISTS ux_testcases_suite_number ON testcases (suiteid, number);
CREATE INDEX IF NOT EXISTS ix_testcases_suite ON testcases (suiteid);

-- ============================================================
-- QC Generator: doc sets, screens and versioned documents.
-- A doc set belongs to a project and reuses project membership (Viewer reads,
-- Contributor creates/edits). Guests see demo doc sets and approved documents only.
-- ============================================================

-- ---------- QC Doc Sets ----------
-- isdemo mirrors projects.isdemo so a guest only browses demo doc sets.
-- logoattachmentid points to ticketattachments for consistent storage and resize.
CREATE TABLE IF NOT EXISTS qcdocsets (
    docsetid            INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    projectid           INTEGER NOT NULL REFERENCES projects (projectid) ON DELETE CASCADE,
    title               VARCHAR(200) NOT NULL,
    appname             VARCHAR(200) NOT NULL,
    businessdescription TEXT NULL,
    language            VARCHAR(10) NOT NULL DEFAULT 'en',
    logoattachmentid    INTEGER NULL REFERENCES ticketattachments (attachmentid) ON DELETE SET NULL,
    createdby           INTEGER NULL REFERENCES users (userid),
    createdat           TIMESTAMPTZ NOT NULL DEFAULT now(),
    isdemo              BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE INDEX IF NOT EXISTS ix_qcdocsets_project ON qcdocsets (projectid);

-- ---------- QC Doc Screens ----------
-- The screenshots arranged in order by the user. Each screen may have a caption
-- and stores the AI-extracted screen summary (JSON) so that repeated document
-- generations or manual user manual generation can reuse the analysis without re-calling vision AI.
CREATE TABLE IF NOT EXISTS qcdocscreens (
    screenid            INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    docsetid            INTEGER NOT NULL REFERENCES qcdocsets (docsetid) ON DELETE CASCADE,
    sortorder           INTEGER NOT NULL DEFAULT 0,
    caption             VARCHAR(200) NULL,
    attachmentid        INTEGER NOT NULL REFERENCES ticketattachments (attachmentid) ON DELETE CASCADE,
    screensummary       TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_qcdocscreens_docset ON qcdocscreens (docsetid, sortorder);

-- ---------- QC Documents ----------
-- Versioned documents produced for a docset (Documentation or UserManual).
-- Versions start at 1 and increment per (docsetid, kind). Never overwritten.
CREATE TABLE IF NOT EXISTS qcdocuments (
    documentid          INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    docsetid            INTEGER NOT NULL REFERENCES qcdocsets (docsetid) ON DELETE CASCADE,
    kind                VARCHAR(20) NOT NULL CHECK (kind IN ('Documentation', 'UserManual')),
    version             INTEGER NOT NULL DEFAULT 1,
    markdown            TEXT NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'Draft'
                        CHECK (status IN ('Draft', 'Approved')),
    source              VARCHAR(20) NOT NULL DEFAULT 'Ai'
                        CHECK (source IN ('Ai', 'Imported', 'Manual')),
    generatedby         INTEGER NULL REFERENCES users (userid),
    createdat           TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_qcdocuments_docset ON qcdocuments (docsetid, kind, version DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_qcdocuments_docset_kind_version ON qcdocuments (docsetid, kind, version);
