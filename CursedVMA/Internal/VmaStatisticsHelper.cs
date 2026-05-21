// Mirrors VmaAddDetailedStatisticsAllocation and VmaAddDetailedStatisticsUnusedRange
// from vk_mem_alloc.cpp. Both concrete algorithm implementations call these to
// build up VmaDetailedStatistics without duplicating the accumulation logic.

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
    }
}
