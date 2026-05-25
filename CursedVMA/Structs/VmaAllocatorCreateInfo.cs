// Mirrors VmaAllocatorCreateInfo from vk_mem_alloc.h. Notable port departures:
//   * pVulkanFunctions is replaced by a Silk.NET Vk dispatch table reference.
//     A managed port does not need separate function-pointer plumbing because
//     Silk.NET already owns one dispatch table per Vk instance.
//   * pHeapSizeLimit (pointer to VK_MAX_MEMORY_HEAPS VkDeviceSize) is exposed
//     as an optional managed array.
//   * pTypeExternalMemoryHandleTypes (pointer to VK_MAX_MEMORY_TYPES handle-type
//     flags) is exposed as an optional managed array.

using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Parameters for <c>VmaAllocator.Create</c>. Mirrors the C++
    /// <c>VmaAllocatorCreateInfo</c> struct, with adjustments described in the
    /// file header.
    /// </summary>
    public unsafe struct VmaAllocatorCreateInfo
    {
        /// <summary>Combination of <see cref="VmaAllocatorCreateFlags"/>.</summary>
        public VmaAllocatorCreateFlags Flags;

        /// <summary>Silk.NET Vulkan API dispatch table; replaces VMA's
        /// <c>pVulkanFunctions</c>.</summary>
        public Vk? VulkanApi;

        /// <summary>The Vulkan instance the device was created from.</summary>
        public Instance Instance;

        /// <summary>The physical device the allocator targets.</summary>
        public PhysicalDevice PhysicalDevice;

        /// <summary>The logical device the allocator targets.</summary>
        public Device Device;

        /// <summary>Preferred size of a single <c>VkDeviceMemory</c> block in the
        /// default pool, in bytes. Zero means use VMA's default (256 MB for large
        /// heaps).</summary>
        public ulong PreferredLargeHeapBlockSize;

        /// <summary>Optional Vulkan host memory allocation callbacks
        /// (<c>VkAllocationCallbacks</c>).</summary>
        public AllocationCallbacks? AllocationCallbacks;

        /// <summary>Optional notification callbacks for VkDeviceMemory create /
        /// destroy events.</summary>
        public VmaDeviceMemoryCallbacks? DeviceMemoryCallbacks;

        /// <summary>Optional per-heap byte budget cap. If non-null the array must
        /// have one element per memory heap reported by the physical device
        /// (max 16 entries); a value of <c>ulong.MaxValue</c> in any slot means
        /// "no limit for that heap".</summary>
        public ulong[]? HeapSizeLimit;

        /// <summary>Vulkan API version the application is targeting
        /// (e.g. <c>VK_API_VERSION_1_2</c>), packed as Vulkan does.</summary>
        public uint VulkanApiVersion;

        /// <summary>Optional per-memory-type external handle types (one entry per
        /// memory type, max 32). VMA will pass these through to
        /// <c>VkExportMemoryAllocateInfoKHR</c> for matching allocations.</summary>
        public ExternalMemoryHandleTypeFlags[]? TypeExternalMemoryHandleTypes;

        /// <summary>Number of bytes to reserve as a guard margin before and after
        /// every suballocation. When non-zero, magic sentinel bytes are written on
        /// each allocation and verified by <c>VmaAllocator.CheckCorruption</c>.
        /// Equivalent to the C++ <c>VMA_DEBUG_MARGIN</c> compile-time constant.
        /// Zero (the default) disables corruption detection.</summary>
        public ulong DebugMargin;
    }
}
