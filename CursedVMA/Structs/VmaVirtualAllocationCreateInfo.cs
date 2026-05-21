// Mirrors VmaVirtualAllocationCreateInfo from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Parameters for <c>VmaVirtualBlock.Allocate</c>.
    /// </summary>
    public struct VmaVirtualAllocationCreateInfo
    {
        /// <summary>Requested allocation size, in bytes.</summary>
        public ulong Size;

        /// <summary>Required alignment, in bytes. Must be a power of two or
        /// zero (zero means alignment of 1).</summary>
        public ulong Alignment;

        /// <summary>Combination of <see cref="VmaVirtualAllocationCreateFlags"/>.</summary>
        public VmaVirtualAllocationCreateFlags Flags;

        /// <summary>Arbitrary user data stored alongside the allocation.</summary>
        public object? UserData;
    }
}
