// Mirrors VmaStatistics from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Lightweight stats covering block / allocation counts and totals. Cheap
    /// to compute; the more expensive <see cref="VmaDetailedStatistics"/> adds
    /// unused-range counters.
    /// </summary>
    public struct VmaStatistics
    {
        /// <summary>Number of <c>VkDeviceMemory</c> blocks currently allocated.</summary>
        public uint BlockCount;

        /// <summary>Number of <see cref="VmaAllocation"/> objects currently allocated.</summary>
        public uint AllocationCount;

        /// <summary>Total bytes held in <c>VkDeviceMemory</c> blocks.</summary>
        public ulong BlockBytes;

        /// <summary>Total bytes occupied by live allocations within those blocks.</summary>
        public ulong AllocationBytes;
    }
}
