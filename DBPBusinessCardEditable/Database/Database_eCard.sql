/* =====================================================================
   DBP Digital Business Card (eCard) - database setup
   Server   : UAT database server (172.16.64.20)
   Database : DBP_eCard
   Run as   : a DBA login (needs CREATE DATABASE for Step 1 if the
              database does not exist yet).

   Safe to run more than once: every step only creates what is missing.
   It never drops, deletes or changes existing data, and it does not
   touch any other database.
   ===================================================================== */

SET NOCOUNT ON;

/* ---------------------------------------------------------------------
   STEP 1. Create the database if it does not exist.
   The collation MUST be case-insensitive (CI): employee usernames and
   Employee IDs are matched without regard to capital letters.
   --------------------------------------------------------------------- */
USE [master];
IF DB_ID(N'DBP_eCard') IS NULL
BEGIN
    CREATE DATABASE [DBP_eCard] COLLATE SQL_Latin1_General_CP1_CI_AS;
    PRINT 'Created database DBP_eCard.';
END
ELSE
    PRINT 'Database DBP_eCard already exists - not changed.';
GO

USE [DBP_eCard];
GO

IF CONVERT(nvarchar(128), DATABASEPROPERTYEX(N'DBP_eCard', 'Collation')) LIKE N'%[_]CS[_]%'
    RAISERROR('WARNING: DBP_eCard uses a case-sensitive collation. The eCard app expects a case-insensitive (CI) collation.', 10, 1);
GO

/* ---------------------------------------------------------------------
   STEP 2. Create the cards table (one row per employee Windows login).
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.cards', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.cards (
        sam_account_name     NVARCHAR(256) NOT NULL CONSTRAINT PK_cards PRIMARY KEY,
        emp_id               NVARCHAR(64)  NOT NULL CONSTRAINT DF_cards_emp_id        DEFAULT N'',
        card_token           NVARCHAR(64)  NOT NULL CONSTRAINT DF_cards_card_token    DEFAULT N'',
        card_pin             NVARCHAR(200) NOT NULL CONSTRAINT DF_cards_card_pin      DEFAULT N'',
        pin_is_hashed        INT           NOT NULL CONSTRAINT DF_cards_pin_is_hashed DEFAULT 0,
        is_active            INT           NOT NULL CONSTRAINT DF_cards_is_active     DEFAULT 1,
        is_published         INT           NOT NULL CONSTRAINT DF_cards_is_published  DEFAULT 0,
        published_at         NVARCHAR(40)  NULL,
        name                 NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_name                DEFAULT N'',
        show_name            INT           NOT NULL CONSTRAINT DF_cards_show_name           DEFAULT 1,
        preferred_name       NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_preferred_name      DEFAULT N'',
        show_preferred_name  INT           NOT NULL CONSTRAINT DF_cards_show_preferred_name DEFAULT 0,
        photo                NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_photo               DEFAULT N'',
        show_photo           INT           NOT NULL CONSTRAINT DF_cards_show_photo          DEFAULT 1,
        title                NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_title               DEFAULT N'',
        show_title           INT           NOT NULL CONSTRAINT DF_cards_show_title          DEFAULT 1,
        department           NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_department          DEFAULT N'',
        show_department      INT           NOT NULL CONSTRAINT DF_cards_show_department     DEFAULT 1,
        org                  NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_org DEFAULT N'Development Bank of the Philippines',
        show_org             INT           NOT NULL CONSTRAINT DF_cards_show_org            DEFAULT 1,
        office               NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_office              DEFAULT N'',
        show_office          INT           NOT NULL CONSTRAINT DF_cards_show_office         DEFAULT 1,
        phone                NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_phone               DEFAULT N'',
        show_phone           INT           NOT NULL CONSTRAINT DF_cards_show_phone          DEFAULT 1,
        email                NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_email               DEFAULT N'',
        show_email           INT           NOT NULL CONSTRAINT DF_cards_show_email          DEFAULT 1,
        github               NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_github              DEFAULT N'',
        show_github          INT           NOT NULL CONSTRAINT DF_cards_show_github         DEFAULT 1,
        linkedin             NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_linkedin            DEFAULT N'',
        show_linkedin        INT           NOT NULL CONSTRAINT DF_cards_show_linkedin       DEFAULT 1,
        portfolio            NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_portfolio           DEFAULT N'',
        show_portfolio       INT           NOT NULL CONSTRAINT DF_cards_show_portfolio      DEFAULT 1,
        extra_info           NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_extra_info          DEFAULT N'[]',
        view_history         NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_view_history        DEFAULT N'[]',
        total_views          INT           NOT NULL CONSTRAINT DF_cards_total_views         DEFAULT 0,
        audit_log            NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_audit_log           DEFAULT N'[]',
        created_at           NVARCHAR(40)  NOT NULL,
        updated_at           NVARCHAR(40)  NOT NULL,
        pending_title        NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_pending_title       DEFAULT N'',
        pending_department   NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_pending_department  DEFAULT N'',
        pending_org          NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_pending_org         DEFAULT N'',
        pending_office       NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_pending_office      DEFAULT N'',
        pending_status       NVARCHAR(20)  NOT NULL CONSTRAINT DF_cards_pending_status      DEFAULT N'none',
        pending_submitted_at NVARCHAR(40)  NULL,
        pending_submitted_by NVARCHAR(256) NOT NULL CONSTRAINT DF_cards_pending_submitted_by DEFAULT N'',
        view_log             NVARCHAR(MAX) NOT NULL CONSTRAINT DF_cards_view_log            DEFAULT N'[]',
        card_expires_at      NVARCHAR(40)  NULL
    );
    PRINT 'Created table dbo.cards.';
END
ELSE
    PRINT 'Table dbo.cards already exists - not changed.';
GO

/* ---------------------------------------------------------------------
   STEP 3. Indexes (Employee ID and card link must be unique).
   --------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_cards_emp_id' AND object_id = OBJECT_ID(N'dbo.cards'))
BEGIN
    CREATE UNIQUE INDEX idx_cards_emp_id ON dbo.cards(emp_id) WHERE emp_id <> N'';
    PRINT 'Created index idx_cards_emp_id.';
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_cards_token' AND object_id = OBJECT_ID(N'dbo.cards'))
BEGIN
    CREATE UNIQUE INDEX idx_cards_token ON dbo.cards(card_token) WHERE card_token <> N'';
    PRINT 'Created index idx_cards_token.';
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_cards_pending' AND object_id = OBJECT_ID(N'dbo.cards'))
BEGIN
    CREATE INDEX idx_cards_pending ON dbo.cards(pending_status);
    PRINT 'Created index idx_cards_pending.';
END
GO

/* ---------------------------------------------------------------------
   STEP 4 (DBA decides). Application login - read/write on DBP_eCard only.
   The app must NOT use 'sa'. Remove the comment markers, set a strong
   password, and give the login name + password to the System Admin
   (they go in the server's web.config, never in email/chat/git).
   --------------------------------------------------------------------- */
/*
USE [master];
IF SUSER_ID(N'dbp_ecard_app') IS NULL
    CREATE LOGIN [dbp_ecard_app] WITH PASSWORD = N'<STRONG-PASSWORD>', CHECK_POLICY = ON, DEFAULT_DATABASE = [DBP_eCard];
USE [DBP_eCard];
IF USER_ID(N'dbp_ecard_app') IS NULL
    CREATE USER [dbp_ecard_app] FOR LOGIN [dbp_ecard_app];
ALTER ROLE db_datareader ADD MEMBER [dbp_ecard_app];
ALTER ROLE db_datawriter ADD MEMBER [dbp_ecard_app];
GRANT VIEW DEFINITION ON dbo.cards TO [dbp_ecard_app];
*/

/* ---------------------------------------------------------------------
   STEP 5. Check - should show the table, 47 columns and 0 rows on a new install.
   --------------------------------------------------------------------- */
USE [DBP_eCard];
SELECT  DB_NAME()                                                     AS [database],
        CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) AS [collation],
        (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.cards')) AS [columns_in_cards],
        (SELECT COUNT(*) FROM dbo.cards)                                AS [cards_rows];
GO
