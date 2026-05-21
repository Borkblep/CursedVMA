// Mirrors VmaAllocatorInfo from vk_mem_alloc.h.

using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Read-only snapshot of the three Vulkan handles an allocator was created
    /// with. Filled by <c>VmaAllocator.GetAllocatorInfo</c>.
    /// </summary>
    public struct VmaAllocatorInfo
    {
        /// <summary>Vulkan instance.</summary>
        public Instance Instance;

        /// <summary>Physical device.</summary>
        public PhysicalDevice PhysicalDevice;

        /// <summary>Logical device.</summary>
        public Device Device;
    }
}
