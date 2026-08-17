// Copyright (c) AeroCode V3.0
// FTS5 migration — actually enable SQLite full-text search.
// AeroCodeDbContext only declares regular indexes; this migration adds the FTS5 virtual table.
using Microsoft.EntityFrameworkCore;

namespace AeroCode.Core.Data;

public static class FtsMigrations
{
    /// <summary>
    /// Run after EnsureCreated() to enable FTS5 on the notes table.
    /// Idempotent: silently no-op if the FTS table already exists.
    /// </summary>
    public static void EnsureFts5(AeroCodeDbContext db)
    {
        try
        {
            // FTS5 virtual table mirroring notes(title, content).
            // content_rowid maps to notes.id for joining.
            db.Database.ExecuteSqlRaw("""
                CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(
                    title,
                    content,
                    content='notes',
                    content_rowid='id',
                    tokenize='unicode61 remove_diacritics 2'
                );
                """);

            // Triggers to keep FTS in sync with notes table.
            db.Database.ExecuteSqlRaw("""
                CREATE TRIGGER IF NOT EXISTS notes_ai AFTER INSERT ON notes BEGIN
                    INSERT INTO notes_fts(rowid, title, content) VALUES (new.id, new.title, new.content);
                END;
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TRIGGER IF NOT EXISTS notes_ad AFTER DELETE ON notes BEGIN
                    INSERT INTO notes_fts(notes_fts, rowid, title, content) VALUES('delete', old.id, old.title, old.content);
                END;
                """);
            db.Database.ExecuteSqlRaw("""
                CREATE TRIGGER IF NOT EXISTS notes_au AFTER UPDATE ON notes BEGIN
                    INSERT INTO notes_fts(notes_fts, rowid, title, content) VALUES('delete', old.id, old.title, old.content);
                    INSERT INTO notes_fts(rowid, title, content) VALUES (new.id, new.title, new.content);
                END;
                """);
        }
        catch
        {
            // FTS5 not available (very old SQLite) — silently fallback to LIKE
        }
    }
}
