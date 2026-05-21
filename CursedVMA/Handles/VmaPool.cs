// C# port of VmaPool_T from vk_mem_alloc.h. Phase 2 defines the type shell only.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Custom memory pool pinned to a specific Vulkan memory type. Equivalent to
    /// the C++ <c>VmaPool_T</c>; lifetime is owned by the parent <see cref="VmaAllocator"/>.
    /// </summary>
    public sealed class VmaPool : IDisposable
    {
        internal VmaPool()
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Phase 8 (pools) will release block lists owned by this pool.
        }
    }
}
