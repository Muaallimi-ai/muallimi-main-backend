-- Legacy AuthAPI snapshot fixture for Phase 9 US7 backfill tests.
-- Schema: legacy_auth (read-only source)
-- Used by BackfillContractTests and BackfillClaimShapeTests.
-- All IDs are deterministic UUIDs so tests can assert linkage by ID.

CREATE SCHEMA IF NOT EXISTS legacy_auth;

CREATE TABLE IF NOT EXISTS legacy_auth."Users" (
    "Id"              uuid         NOT NULL PRIMARY KEY,
    "Email"           text,
    "NormalizedEmail" text,
    "PasswordHash"    text,
    "FullName"        text         NOT NULL DEFAULT '',
    "PhoneNumber"     text,
    "Role"            text         NOT NULL,
    "EmailConfirmed"  boolean      NOT NULL DEFAULT false,
    "CreatedAt"       timestamptz  NOT NULL DEFAULT now(),
    "UpdatedAt"       timestamptz  NOT NULL DEFAULT now(),
    "DeletedAt"       timestamptz
);

-- ── Parent users ──────────────────────────────────────────────────────────
-- parent-1: active, email confirmed → should become Active Personal user in Family tenant
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('a0000001-0000-0000-0000-000000000001',
     'parent1@example.com', 'PARENT1@EXAMPLE.COM',
     '$2a$12$dummyhashparent1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'أحمد الشامة', 'Parent', true, '2024-01-10T10:00:00Z');

-- parent-2: active, email NOT confirmed → PendingEmailVerification
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('a0000002-0000-0000-0000-000000000002',
     'parent2@example.com', 'PARENT2@EXAMPLE.COM',
     '$2a$12$dummyhashparent2xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'فاطمة حسن', 'Parent', false, '2024-02-01T08:00:00Z');

-- ── Student users ─────────────────────────────────────────────────────────
-- student-1: child of parent-1, email confirmed (managed user)
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('b0000001-0000-0000-0000-000000000001',
     NULL, NULL,
     '$2a$12$dummyhashstudent1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'محمد أحمد', 'Student', false, '2024-01-15T12:00:00Z');

-- student-2: child of parent-2
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('b0000002-0000-0000-0000-000000000002',
     NULL, NULL,
     '$2a$12$dummyhashstudent2xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'نور فاطمة', 'Student', false, '2024-02-05T09:00:00Z');

-- ── School admin users ────────────────────────────────────────────────────
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('c0000001-0000-0000-0000-000000000001',
     'schooladmin@school1.example.com', 'SCHOOLADMIN@SCHOOL1.EXAMPLE.COM',
     '$2a$12$dummyhashschoola1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'مدير المدرسة', 'SchoolAdmin', true, '2024-03-01T10:00:00Z');

-- ── Teacher users ─────────────────────────────────────────────────────────
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('d0000001-0000-0000-0000-000000000001',
     'teacher@school1.example.com', 'TEACHER@SCHOOL1.EXAMPLE.COM',
     '$2a$12$dummyhashteacher1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'معلم الرياضيات', 'Teacher', true, '2024-03-05T08:00:00Z');

-- ── Curriculum admin ──────────────────────────────────────────────────────
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('e0000001-0000-0000-0000-000000000001',
     'curricadmin@platform.example.com', 'CURRICADMIN@PLATFORM.EXAMPLE.COM',
     '$2a$12$dummyhashcurric1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'مسؤول المناهج', 'CurriculumAdmin', true, '2024-04-01T10:00:00Z');

-- ── Super admin ───────────────────────────────────────────────────────────
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt")
VALUES
    ('f0000001-0000-0000-0000-000000000001',
     'superadmin@platform.example.com', 'SUPERADMIN@PLATFORM.EXAMPLE.COM',
     '$2a$12$dummyhashsuper1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'المسؤول الأعلى', 'SuperAdmin', true, '2024-01-01T00:00:00Z');

-- ── Deleted user (must be SKIPPED by backfill) ────────────────────────────
INSERT INTO legacy_auth."Users"
    ("Id", "Email", "NormalizedEmail", "PasswordHash", "FullName", "Role", "EmailConfirmed", "CreatedAt", "DeletedAt")
VALUES
    ('dead0001-0000-0000-0000-000000000001',
     'deleted@example.com', 'DELETED@EXAMPLE.COM',
     '$2a$12$dummyhashdeleted1xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
     'Deleted User', 'Parent', true, '2024-01-01T00:00:00Z', '2024-06-01T00:00:00Z');

-- ── Legacy supplementary tables (school association for school users) ──────
-- A minimal school_administrators table so the backfill can resolve school IDs.
CREATE TABLE IF NOT EXISTS legacy_auth."SchoolAdministrators" (
    "Id"       uuid NOT NULL PRIMARY KEY,
    "UserId"   uuid NOT NULL,
    "SchoolId" uuid NOT NULL
);

INSERT INTO legacy_auth."SchoolAdministrators" ("Id", "UserId", "SchoolId")
VALUES
    ('ca000001-0000-0000-0000-000000000001',
     'c0000001-0000-0000-0000-000000000001',
     '99000001-0000-0000-0000-000000000001');

CREATE TABLE IF NOT EXISTS legacy_auth."Teachers" (
    "Id"       uuid NOT NULL PRIMARY KEY,
    "UserId"   uuid NOT NULL,
    "SchoolId" uuid NOT NULL
);

INSERT INTO legacy_auth."Teachers" ("Id", "UserId", "SchoolId")
VALUES
    ('da000001-0000-0000-0000-000000000001',
     'd0000001-0000-0000-0000-000000000001',
     '99000001-0000-0000-0000-000000000001');

-- StudentProfiles table linking students to parents.
CREATE TABLE IF NOT EXISTS legacy_auth."StudentProfiles" (
    "Id"           uuid NOT NULL PRIMARY KEY,
    "UserId"       uuid,
    "ParentUserId" uuid NOT NULL
);

INSERT INTO legacy_auth."StudentProfiles" ("Id", "UserId", "ParentUserId")
VALUES
    ('ba000001-0000-0000-0000-000000000001',
     'b0000001-0000-0000-0000-000000000001',
     'a0000001-0000-0000-0000-000000000001'),
    ('ba000002-0000-0000-0000-000000000002',
     'b0000002-0000-0000-0000-000000000002',
     'a0000002-0000-0000-0000-000000000002');
