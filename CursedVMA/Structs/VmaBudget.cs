// Mirrors VmaBudget from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Combined statistics plus OS-reported budget for a single Vulkan memory
    /// heap. Filled by <c>VmaAllocator.GetHeapBudgets</c>. When the allocator
    /// was created with <see cref="VmaAllocatorCreateFlags.ExtMemoryBudgetBit"/>
    /// the budget values come from VK_EXT_memory_budget; otherwise they fall
    /// back to estimates derived from <c>VkPhysicalDeviceMemoryProperties</c>.
    /// </summary>
    public struct VmaBudget
    {
        /// <summary>Allocator-internal counts and totals for the heap.</summary>
        public VmaStatistics Statistics;

        /// <summary>Estimated number of bytes the process is currently using
        /// on this heap, as reported by the OS / driver.</summary>
        public ulong Usage;

        /// <summary>Maximum number of bytes the process is allowed to use on
        /// this heap (the soft budget).</summary>
        public ulong Budget;
    }
}
