// Mirrors VmaVirtualBlockCreateInfo from vk_mem_alloc.h.

using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Parameters for <c>VmaVirtualBlock.Create</c>; describes the size of the
    /// abstract address space and the allocation algorithm.
    /// </summary>
    public struct VmaVirtualBlockCreateInfo
    {
        /// <summary>Size, in bytes, of the abstract address space. Allocations
        /// are returned as offsets in <c>[0, Size)</c>.</summary>
        public ulong Size;

        /// <summary>Combination of <see cref="VmaVirtualBlockCreateFlags"/>.</summary>
        public VmaVirtualBlockCreateFlags Flags;

        /// <summary>Vulkan host memory allocation callbacks used by the block's
        /// internal data structures. Optional.</summary>
        public AllocationCallbacks? AllocationCallbacks;
    }
}
