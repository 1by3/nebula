namespace Nebula
{
    /// <summary>
    /// Where an entity's extent comes from: the box, in the entity's own space, that the ghost band measures to
    /// each neighbouring container instead of the entity's root position (<c>NetworkIdentity.ExtentSource</c>).
    /// </summary>
    public enum EntityExtentSource : byte
    {
        /// <summary>No extent: the entity is ghosted by its root position alone, as every entity was before extents.</summary>
        None = 0,
        /// <summary>The box given in <c>NetworkIdentity.Extent</c>, authored on the prefab or set with <c>SetExtent</c>.</summary>
        Explicit = 1,
        /// <summary>A box computed from the entity's own enabled, non-trigger colliders, refreshed as they change.</summary>
        Colliders = 2,
    }
}
