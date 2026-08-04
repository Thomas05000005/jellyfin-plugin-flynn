namespace Jellyfin.Plugin.Flynn.Core.Modules;

/// <summary>
/// Which shelf a module belongs on.
/// <para>
/// A fixed set rather than a free string, so the dashboard groups predictably and two modules
/// cannot end up under "Library" and "Libraries". Adding a value is a deliberate act; the page
/// renders whatever it finds, in declaration order.
/// </para>
/// </summary>
public enum ModuleCategory
{
    /// <summary>Keeping the server healthy and telling people about it.</summary>
    Operations = 0,

    /// <summary>Disks, memory, capacity, what the hardware is doing.</summary>
    System = 1,

    /// <summary>What is in the libraries and what is wrong with it.</summary>
    Library = 2,

    /// <summary>Music, the part of a media server everything else neglects.</summary>
    Music = 3,

    /// <summary>Accounts, sessions, and the people using the server.</summary>
    People = 4,
}
