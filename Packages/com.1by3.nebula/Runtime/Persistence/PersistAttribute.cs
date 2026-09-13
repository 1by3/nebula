using System;

namespace Nebula
{
    /// <summary>
    /// Marks a <see cref="NetworkVariable{T}"/> field as part of the entity's saved state. The variable is written
    /// into the persistence blob under its discovered name (<c>"&lt;BehaviourTypeName&gt;.&lt;FieldName&gt;"</c>) and
    /// assigning it also schedules the entity's next checkpoint, so a saved value is never more than a checkpoint
    /// behind what the simulation holds.
    /// <code>
    /// public sealed class Door : NetworkBehaviour
    /// {
    ///     [Persist] public NetworkVariable&lt;bool&gt; IsOpen = new NetworkVariable&lt;bool&gt;();
    /// }
    /// </code>
    /// The entity itself opts in by carrying a <see cref="PersistentEntity"/>; without one, a
    /// <see cref="PersistAttribute"/> has no effect.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public sealed class PersistAttribute : Attribute
    {
    }
}
