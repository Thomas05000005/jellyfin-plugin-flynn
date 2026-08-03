namespace Jellyfin.Plugin.Flynn.Core.Mutations;

/// <summary>
/// Thrown when the kernel declines to make a change.
/// <para>
/// Always before anything happened: a refusal means the system is exactly as it was. It is not an
/// error condition so much as the safety rules working, so callers should show the message rather
/// than treat it as a fault.
/// </para>
/// </summary>
public sealed class MutationRefusedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="MutationRefusedException"/> class.</summary>
    public MutationRefusedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MutationRefusedException"/> class.</summary>
    /// <param name="message">Why it was refused, in terms an admin can act on.</param>
    public MutationRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MutationRefusedException"/> class.</summary>
    /// <param name="message">Why it was refused.</param>
    /// <param name="innerException">The underlying cause.</param>
    public MutationRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
