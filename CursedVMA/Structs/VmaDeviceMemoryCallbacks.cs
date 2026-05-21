// Mirrors VmaDeviceMemoryCallbacks (and the matching PFN typedefs) from
// vk_mem_alloc.h. Modeled as a managed class because the callback slots hold
// managed delegate references and the struct is referenced by nullable
// pointer in the C API.

using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Delegate invoked by VMA after it has allocated a new <c>VkDeviceMemory</c>
    /// object. Counterpart of <c>PFN_vmaAllocateDeviceMemoryFunction</c>.
    /// </summary>
    public delegate void VmaAllocateDeviceMemoryDelegate(
        VmaAllocator allocator,
        uint memoryType,
        DeviceMemory memory,
        ulong size,
        object? userData);

    /// <summary>
    /// Delegate invoked by VMA just before it frees a <c>VkDeviceMemory</c>
    /// object. Counterpart of <c>PFN_vmaFreeDeviceMemoryFunction</c>.
    /// </summary>
    public delegate void VmaFreeDeviceMemoryDelegate(
        VmaAllocator allocator,
        uint memoryType,
        DeviceMemory memory,
        ulong size,
        object? userData);

    /// <summary>
    /// Optional allocate / free notification callbacks. Pass to
    /// <see cref="VmaAllocatorCreateInfo.DeviceMemoryCallbacks"/> to be notified
    /// whenever the allocator creates or destroys a backing
    /// <c>VkDeviceMemory</c> object.
    /// </summary>
    public sealed class VmaDeviceMemoryCallbacks
    {
        /// <summary>Called after a <c>VkDeviceMemory</c> object has been created.</summary>
        public VmaAllocateDeviceMemoryDelegate? PfnAllocate { get; init; }

        /// <summary>Called before a <c>VkDeviceMemory</c> object is freed.</summary>
        public VmaFreeDeviceMemoryDelegate? PfnFree { get; init; }

        /// <summary>Arbitrary user data passed through to both callbacks.</summary>
        public object? UserData { get; init; }
    }
}
