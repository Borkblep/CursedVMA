// Mirrors VmaTotalStatistics from vk_mem_alloc.h. The two fixed-size arrays
// (one entry per memory type, one per memory heap) are represented as inline
// arrays so the whole struct remains a zero-allocation value type, matching
// the C layout. Use the [ ] indexer (or Span projection) to access entries.

using System.Runtime.CompilerServices;

namespace CursedVMA
{
    /// <summary>
    /// Inline array of <see cref="VmaDetailedStatistics"/> with one entry per
    /// Vulkan memory type (VK_MAX_MEMORY_TYPES = 32).
    /// </summary>
    [InlineArray(32)]
    public struct VmaMemoryTypeDetailedStatisticsArray
    {
#pragma warning disable IDE0044, IDE0051, CS0169
        private VmaDetailedStatistics _element0;
#pragma warning restore IDE0044, IDE0051, CS0169
    }

    /// <summary>
    /// Inline array of <see cref="VmaDetailedStatistics"/> with one entry per
    /// Vulkan memory heap (VK_MAX_MEMORY_HEAPS = 16).
    /// </summary>
    [InlineArray(16)]
    public struct VmaMemoryHeapDetailedStatisticsArray
    {
#pragma warning disable IDE0044, IDE0051, CS0169
        private VmaDetailedStatistics _element0;
#pragma warning restore IDE0044, IDE0051, CS0169
    }

    /// <summary>
    /// Snapshot of detailed statistics covering every memory type, every memory
    /// heap, and the allocator overall. Filled by
    /// <c>VmaAllocator.CalculateStatistics</c>.
    /// </summary>
    public struct VmaTotalStatistics
    {
        /// <summary>Per-memory-type detailed statistics; 32 entries.</summary>
        public VmaMemoryTypeDetailedStatisticsArray MemoryType;

        /// <summary>Per-memory-heap detailed statistics; 16 entries.</summary>
        public VmaMemoryHeapDetailedStatisticsArray MemoryHeap;

        /// <summary>Allocator-wide totals.</summary>
        public VmaDetailedStatistics Total;
    }
}
