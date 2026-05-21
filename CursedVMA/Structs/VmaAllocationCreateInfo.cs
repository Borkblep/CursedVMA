// Mirrors VmaAllocationCreateInfo from vk_mem_alloc.h.

using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Parameters that drive memory-type selection and per-allocation behavior
    /// for the various <c>VmaAllocator</c> allocation methods.
    /// </summary>
    public struct VmaAllocationCreateInfo
    {
        /// <summary>Combination of <see cref="VmaAllocationCreateFlags"/>.</summary>
        public VmaAllocationCreateFlags Flags;

        /// <summary>Intended usage hint; the AUTO variants are the recommended
        /// choice in VMA 3.x.</summary>
        public VmaMemoryUsage Usage;

        /// <summary>Memory property flags VMA <em>must</em> satisfy.</summary>
        public MemoryPropertyFlags RequiredFlags;

        /// <summary>Additional memory property flags VMA should prefer if possible.</summary>
        public MemoryPropertyFlags PreferredFlags;

        /// <summary>Bitmask of acceptable memory type indices; bit <c>i</c> set
        /// means memory type <c>i</c> is allowed. Use <c>uint.MaxValue</c> to
        /// permit any type.</summary>
        public uint MemoryTypeBits;

        /// <summary>Optional pool to allocate from. When null, the default pool
        /// for the chosen memory type is used.</summary>
        public VmaPool? Pool;

        /// <summary>Arbitrary user data stored alongside the allocation.</summary>
        public object? UserData;

        /// <summary>Allocation priority forwarded to
        /// <c>VkMemoryPriorityAllocateInfoEXT</c>; only meaningful when
        /// <see cref="VmaAllocatorCreateFlags.ExtMemoryPriorityBit"/> is set on
        /// the allocator. Range 0.0f (low) .. 1.0f (high).</summary>
        public float Priority;
    }
}
