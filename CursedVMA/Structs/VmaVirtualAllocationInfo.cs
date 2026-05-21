// Mirrors VmaVirtualAllocationInfo from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Snapshot of the state of a <see cref="VmaVirtualAllocation"/>, filled by
    /// <c>VmaVirtualBlock.GetAllocationInfo</c>.
    /// </summary>
    public struct VmaVirtualAllocationInfo
    {
        /// <summary>Offset of the allocation within its parent
        /// <see cref="VmaVirtualBlock"/>'s address space.</summary>
        public ulong Offset;

        /// <summary>Size of the allocation, in bytes.</summary>
        public ulong Size;

        /// <summary>User data assigned via
        /// <see cref="VmaVirtualAllocationCreateInfo.UserData"/> or
        /// <c>VmaVirtualBlock.SetAllocationUserData</c>.</summary>
        public object? UserData;
    }
}
