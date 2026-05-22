// Mirrors VmaAddDetailedStatisticsAllocation, VmaAddDetailedStatisticsUnusedRange,
// VmaMergeStatistics, and VmaAddStatistics from vk_mem_alloc.cpp. Both concrete
// algorithm implementations and the allocator aggregation paths call these.

using System.Runtime.CompilerServices;

namespace CursedVMA.Internal
{
    internal static class VmaStatisticsHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddDetailedStatisticsAllocation(ref VmaDetailedStatistics stats, ulong size)
        {
            stats.Statistics.AllocationCount++;
            stats.Statistics.AllocationBytes += size;
            if (stats.Statistics.AllocationCount == 1 || stats.AllocationSizeMin > size)
                stats.AllocationSizeMin = size;
            if (stats.AllocationSizeMax < size)
                stats.AllocationSizeMax = size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddDetailedStatisticsUnusedRange(ref VmaDetailedStatistics stats, ulong size)
        {
            stats.UnusedRangeCount++;
            if (stats.UnusedRangeCount == 1 || stats.UnusedRangeSizeMin > size)
                stats.UnusedRangeSizeMin = size;
            if (stats.UnusedRangeSizeMax < size)
                stats.UnusedRangeSizeMax = size;
        }

        /// <summary>
        /// Accumulates <paramref name="src"/> into <paramref name="dst"/>,
        /// respecting the uninitialized-zero sentinel for min values.
        /// Mirrors <c>VmaMergeStatistics</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MergeDetailedStatistics(ref VmaDetailedStatistics dst, in VmaDetailedStatistics src)
        {
            // Capture old counts before adding so min-sentinel logic is correct.
            uint oldDstAllocCount   = dst.Statistics.AllocationCount;
            uint oldDstUnusedCount  = dst.UnusedRangeCount;

            dst.Statistics.BlockCount      += src.Statistics.BlockCount;
            dst.Statistics.AllocationCount += src.Statistics.AllocationCount;
            dst.Statistics.BlockBytes      += src.Statistics.BlockBytes;
            dst.Statistics.AllocationBytes += src.Statistics.AllocationBytes;
            dst.UnusedRangeCount           += src.UnusedRangeCount;

            if (src.Statistics.AllocationCount > 0)
            {
                if (oldDstAllocCount == 0 || src.AllocationSizeMin < dst.AllocationSizeMin)
                    dst.AllocationSizeMin = src.AllocationSizeMin;
                if (src.AllocationSizeMax > dst.AllocationSizeMax)
                    dst.AllocationSizeMax = src.AllocationSizeMax;
            }

            if (src.UnusedRangeCount > 0)
            {
                if (oldDstUnusedCount == 0 || src.UnusedRangeSizeMin < dst.UnusedRangeSizeMin)
                    dst.UnusedRangeSizeMin = src.UnusedRangeSizeMin;
                if (src.UnusedRangeSizeMax > dst.UnusedRangeSizeMax)
                    dst.UnusedRangeSizeMax = src.UnusedRangeSizeMax;
            }
        }

        /// <summary>
        /// Accumulates <paramref name="src"/> lightweight stats into
        /// <paramref name="dst"/>. Mirrors <c>VmaAddStatistics</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MergeStatistics(ref VmaStatistics dst, in VmaStatistics src)
        {
            dst.BlockCount      += src.BlockCount;
            dst.AllocationCount += src.AllocationCount;
            dst.BlockBytes      += src.BlockBytes;
            dst.AllocationBytes += src.AllocationBytes;
        }
    }
}
