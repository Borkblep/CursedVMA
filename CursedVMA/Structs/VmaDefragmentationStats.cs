// Mirrors VmaDefragmentationStats from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Totals produced by a completed defragmentation session, filled by
    /// <c>VmaAllocator.EndDefragmentation</c>.
    /// </summary>
    public struct VmaDefragmentationStats
    {
        /// <summary>Total bytes copied across all passes.</summary>
        public ulong BytesMoved;

        /// <summary>Total bytes freed by releasing now-empty blocks.</summary>
        public ulong BytesFreed;

        /// <summary>Number of allocations that were relocated.</summary>
        public uint AllocationsMoved;

        /// <summary>Number of <c>VkDeviceMemory</c> blocks freed.</summary>
        public uint DeviceMemoryBlocksFreed;
    }
}
