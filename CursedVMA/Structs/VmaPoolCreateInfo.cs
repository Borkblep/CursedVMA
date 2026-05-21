// Mirrors VmaPoolCreateInfo from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Parameters for <c>VmaAllocator.CreatePool</c>. A pool pins its allocations
    /// to a single Vulkan memory type and may opt into a non-default block
    /// algorithm or alignment / size constraints.
    /// </summary>
    public unsafe struct VmaPoolCreateInfo
    {
        /// <summary>Memory type index this pool will allocate from. Use one of
        /// the <c>VmaAllocator.FindMemoryTypeIndex*</c> helpers to pick a type
        /// matching your resource and usage requirements.</summary>
        public uint MemoryTypeIndex;

        /// <summary>Combination of <see cref="VmaPoolCreateFlags"/>.</summary>
        public VmaPoolCreateFlags Flags;

        /// <summary>Size, in bytes, of every <c>VkDeviceMemory</c> block in the
        /// pool. Zero asks VMA to choose a default based on the heap size.</summary>
        public ulong BlockSize;

        /// <summary>Minimum block count to keep alive. When non-zero the pool
        /// preallocates this many blocks at creation and never releases below
        /// this count.</summary>
        public nuint MinBlockCount;

        /// <summary>Maximum block count the pool may grow to. Zero means
        /// unlimited.</summary>
        public nuint MaxBlockCount;

        /// <summary>Priority hint forwarded to <c>VkMemoryPriorityAllocateInfoEXT</c>
        /// for blocks owned by this pool; only meaningful when
        /// <see cref="VmaAllocatorCreateFlags.ExtMemoryPriorityBit"/> is set.</summary>
        public float Priority;

        /// <summary>If non-zero, every allocation in this pool will have at
        /// least this alignment, in addition to the alignment Vulkan would
        /// otherwise require.</summary>
        public ulong MinAllocationAlignment;

        /// <summary>Optional pNext chain that VMA appends to every
        /// <c>VkMemoryAllocateInfo</c> in this pool. Useful for passing
        /// <c>VkExportMemoryAllocateInfoKHR</c>, dedicated-allocation structs,
        /// etc.</summary>
        public void* MemoryAllocateNext;
    }
}
