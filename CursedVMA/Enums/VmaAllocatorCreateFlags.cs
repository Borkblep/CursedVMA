// Mirrors VmaAllocatorCreateFlagBits from vk_mem_alloc.h.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Flags for the <see cref="VmaAllocatorCreateInfo.Flags"/> field, controlling
    /// optional behaviors of a <see cref="VmaAllocator"/> at creation time.
    /// </summary>
    [Flags]
    public enum VmaAllocatorCreateFlags : uint
    {
        None = 0,

        /// <summary>
        /// Allocator and all its objects are externally synchronized; VMA omits its
        /// own internal locks. Maps to VMA_ALLOCATOR_CREATE_EXTERNALLY_SYNCHRONIZED_BIT.
        /// </summary>
        ExternallySynchronizedBit = 0x00000001,

        /// <summary>
        /// Enable use of VK_KHR_dedicated_allocation / promoted Vulkan 1.1 dedicated
        /// memory queries.
        /// </summary>
        KhrDedicatedAllocationBit = 0x00000002,

        /// <summary>
        /// Enable use of VK_KHR_bind_memory2 / promoted Vulkan 1.1 binding entry points.
        /// </summary>
        KhrBindMemory2Bit = 0x00000004,

        /// <summary>
        /// Enable use of VK_EXT_memory_budget for accurate heap budget tracking.
        /// </summary>
        ExtMemoryBudgetBit = 0x00000008,

        /// <summary>
        /// Enable use of VK_AMD_device_coherent_memory.
        /// </summary>
        AmdDeviceCoherentMemoryBit = 0x00000010,

        /// <summary>
        /// Allocations created by the allocator may be used as buffer device addresses
        /// (VkBufferDeviceAddressInfo); requires VK_KHR_buffer_device_address or core 1.2.
        /// </summary>
        BufferDeviceAddressBit = 0x00000020,

        /// <summary>
        /// Enable use of VK_EXT_memory_priority. Allocations and pools may then set
        /// a priority hint that VMA forwards to VkMemoryPriorityAllocateInfoEXT.
        /// </summary>
        ExtMemoryPriorityBit = 0x00000040,

        /// <summary>
        /// Enable use of VK_KHR_maintenance4 (which exposes lower memory requirements
        /// for buffer/image binding).
        /// </summary>
        KhrMaintenance4Bit = 0x00000080,

        /// <summary>
        /// Enable use of VK_KHR_maintenance5 (additional memory binding queries).
        /// </summary>
        KhrMaintenance5Bit = 0x00000100,

        /// <summary>
        /// Enable use of VK_KHR_external_memory_win32 for exportable memory handles
        /// on Windows. Maps to VMA_ALLOCATOR_CREATE_KHR_EXTERNAL_MEMORY_WIN32_BIT.
        /// </summary>
        KhrExternalMemoryWin32Bit = 0x00000200,
    }
}
