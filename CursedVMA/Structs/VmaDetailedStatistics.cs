// Mirrors VmaDetailedStatistics from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Extended stats that include allocation size extrema and unused-range
    /// counters; more expensive to compute than <see cref="VmaStatistics"/>.
    /// </summary>
    public struct VmaDetailedStatistics
    {
        /// <summary>Basic counts and totals.</summary>
        public VmaStatistics Statistics;

        /// <summary>Number of contiguous unused (free) ranges across all blocks.</summary>
        public uint UnusedRangeCount;

        /// <summary>Smallest live allocation size, in bytes.</summary>
        public ulong AllocationSizeMin;

        /// <summary>Largest live allocation size, in bytes.</summary>
        public ulong AllocationSizeMax;

        /// <summary>Smallest contiguous unused range, in bytes.</summary>
        public ulong UnusedRangeSizeMin;

        /// <summary>Largest contiguous unused range, in bytes.</summary>
        public ulong UnusedRangeSizeMax;
    }
}
