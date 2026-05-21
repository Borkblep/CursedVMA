// Mirrors VmaAllocationInfo from vk_mem_alloc.h.

using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Read-only snapshot of the data associated with a <see cref="VmaAllocation"/>,
    /// filled by <c>VmaAllocator.GetAllocationInfo</c>. Cheap to retrieve and
    /// safe to cache for the lifetime of the allocation, with the caveat that
    /// <see cref="MappedData"/> becomes invalid after a defragmentation move.
    /// </summary>
    public unsafe struct VmaAllocationInfo
    {
        /// <summary>Index of the Vulkan memory type backing this allocation.</summary>
        public uint MemoryType;

        /// <summary>The underlying <c>VkDeviceMemory</c> object. For suballocations,
        /// many <see cref="VmaAllocation"/>s share the same memory handle.</summary>
        public DeviceMemory Memory;

        /// <summary>Offset, in bytes, of this allocation within <see cref="Memory"/>.</summary>
        public ulong Offset;

        /// <summary>Size, in bytes, of this allocation.</summary>
        public ulong Size;

        /// <summary>Pointer to the mapped memory region if the allocation is
        /// persistently mapped (or has been mapped via
        /// <c>VmaAllocator.MapMemory</c>), otherwise null.</summary>
        public void* MappedData;

        /// <summary>User data assigned via
        /// <c>VmaAllocator.SetAllocationUserData</c> or
        /// <see cref="VmaAllocationCreateInfo.UserData"/>.</summary>
        public object? UserData;

        /// <summary>Optional human-readable name assigned via
        /// <c>VmaAllocator.SetAllocationName</c>.</summary>
        public string? Name;
    }
}
