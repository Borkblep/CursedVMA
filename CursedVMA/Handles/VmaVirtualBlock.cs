// C# port of VmaVirtualBlock_T from vk_mem_alloc.h. Phase 2 defines the type
// shell only; allocation, free, and stats methods land in Phase 11.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Self-contained suballocation engine over an abstract address space, with
    /// no Vulkan dependency. Used for sub-dividing large GPU buffers / pre-built
    /// memory regions using the same TLSF or linear algorithm VMA uses for real
    /// device memory. Equivalent to the C++ <c>VmaVirtualBlock_T</c>.
    /// </summary>
    public sealed class VmaVirtualBlock : IDisposable
    {
        internal VmaVirtualBlock()
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Phase 11 will release the underlying metadata structures.
        }
    }
}
