#nullable enable

namespace com.AtelierAI.Uco.Framework.Common.Model
{
    /// <summary>
    /// Marker for a durable operation handle. Reflected tools returning this marker
    /// are packed as processing rather than as an immediate successful result.
    /// </summary>
    public interface IDurableOperationHandle
    {
    }
}
