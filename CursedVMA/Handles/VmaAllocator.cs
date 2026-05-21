// C# port of VmaAllocator_T from vk_mem_alloc.h. Phase 2 defines the type
// shell only; allocation, statistics, and lifecycle methods land in later phases.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Top-level VMA object; manages all memory allocations for a single
    /// <c>VkDevice</c>. Equivalent to the C++ <c>VmaAllocator_T</c>.
    /// </summary>
    public sealed class VmaAllocator : IDisposable
    {
        internal VmaAllocator()
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Phase 7 will release device memory blocks and tear down internal state.
        }
    }
}
