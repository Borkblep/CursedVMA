// Mirrors VmaAllocationInfo2 from vk_mem_alloc.h (added in VMA 3.1).

namespace CursedVMA
{
    /// <summary>
    /// Extended allocation info that adds the parent block's size and a flag
    /// indicating whether the allocation has its own dedicated
    /// <c>VkDeviceMemory</c>.
    /// </summary>
    public unsafe struct VmaAllocationInfo2
    {
        /// <summary>Base allocation info (memory handle, offset, size, mapped data, etc.).</summary>
        public VmaAllocationInfo AllocationInfo;

        /// <summary>Size of the <c>VkDeviceMemory</c> object backing this allocation.</summary>
        public ulong BlockSize;

        /// <summary>True when this allocation owns its own dedicated <c>VkDeviceMemory</c>;
        /// false when it is a suballocation within a shared block.</summary>
        public bool DedicatedMemory;
    }
}
