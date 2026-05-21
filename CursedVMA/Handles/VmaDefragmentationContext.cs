// C# port of VmaDefragmentationContext_T from vk_mem_alloc.h. Phase 2 defines
// the type shell only; pass execution lands in Phase 10.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Transient context for a defragmentation session. Created by
    /// <c>VmaAllocator.BeginDefragmentation</c> and consumed pass-by-pass via
    /// <c>BeginDefragmentationPass</c> / <c>EndDefragmentationPass</c>, then
    /// terminated by <c>EndDefragmentation</c>. Equivalent to the C++
    /// <c>VmaDefragmentationContext_T</c>.
    /// </summary>
    public sealed class VmaDefragmentationContext : IDisposable
    {
        internal VmaDefragmentationContext()
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Phase 10 will free temp allocations reserved for in-flight moves.
        }
    }
}
