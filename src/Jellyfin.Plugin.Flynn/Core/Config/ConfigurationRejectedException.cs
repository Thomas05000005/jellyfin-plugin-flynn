namespace Jellyfin.Plugin.Flynn.Core.Config;

/// <summary>
/// Thrown when a configuration change was refused because it would not survive being written and
/// read back.
/// <para>
/// This is a client error, not a server fault: something in the submitted values cannot be
/// represented in the stored XML. Endpoints should answer 400 with the message, and the stored
/// configuration is guaranteed untouched.
/// </para>
/// </summary>
public sealed class ConfigurationRejectedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ConfigurationRejectedException"/> class.</summary>
    public ConfigurationRejectedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConfigurationRejectedException"/> class.</summary>
    /// <param name="message">Message safe to show an admin.</param>
    public ConfigurationRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConfigurationRejectedException"/> class.</summary>
    /// <param name="message">Message safe to show an admin.</param>
    /// <param name="innerException">The serialisation failure that caused the refusal.</param>
    public ConfigurationRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
