namespace Jellyfin.Plugin.Flynn.Core.Data;

/// <summary>
/// One schema step, applied once and then never again.
/// </summary>
/// <param name="Version">
/// Strictly increasing, starting at 1, with no gaps. It is what the database remembers, so a
/// version that ships and is later renumbered will either be skipped or re-run on existing
/// installations.
/// </param>
/// <param name="Name">Short description, for the log and for reading the list.</param>
/// <param name="Sql">
/// The statements to run, in one transaction.
/// <para>
/// <b>Additive only.</b> New tables, new columns, new indexes. Never <c>DROP</c>, never
/// <c>RENAME</c>, never a narrowing type change: the database on the other end belongs to someone
/// who is upgrading, and a released migration cannot be edited afterwards, only followed by
/// another one. A column that is no longer used costs a few bytes; a dropped column costs the
/// user their data.
/// </para>
/// <para>
/// Order within a step matters too: <c>CREATE TABLE</c> before <c>ALTER TABLE</c> before
/// <c>CREATE INDEX</c>. A fresh database forgives the wrong order because everything is created at
/// once; an existing one does not.
/// </para>
/// </param>
public sealed record Migration(int Version, string Name, string Sql);
