using Jellyfin.Plugin.Flynn.Core.Mutations;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Flynn.Configuration;

/// <summary>
/// Persisted plugin settings.
/// <para>
/// Serialised by Jellyfin with <c>XmlSerializer</c>. Two rules that bite:
/// every collection property needs a <c>= new()</c> initialiser (the serialiser can bypass the
/// constructor on nil-marked XML and leave you a null), and a property may be ADDED freely but
/// never RENAMED — configs already on disk carry the old element names.
/// </para>
/// <para>
/// Only settings live here. Anything that grows without bound — history, snapshots, issues —
/// belongs in the module SQLite database, not in this file.
/// </para>
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the per-module on/off switches.
    /// <para>
    /// A module absent from this list has never been touched by the admin and falls back to its
    /// own default, so removing an entry is "reset", not "disable".
    /// </para>
    /// </summary>
    public List<ModuleToggle> Modules { get; set; } = new();

    /// <summary>
    /// Gets or sets the furthest any module is allowed to reach when changing something.
    /// <para>
    /// Defaults to <see cref="WriteLevel.None"/>: a fresh install reports and changes nothing until
    /// the admin says otherwise. The kernel refuses anything above this before it runs, which is
    /// what makes report-only a mode rather than a promise.
    /// </para>
    /// </summary>
    public WriteLevel MaxWriteLevel { get; set; } = WriteLevel.None;
}

/// <summary>One module's on/off state, as persisted.</summary>
public class ModuleToggle
{
    /// <summary>Gets or sets the module id. Matches <c>IFlynnModule.Id</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the admin switched this module on.</summary>
    public bool Enabled { get; set; }
}
