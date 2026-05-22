// Internal Vulkan dispatch layer; consumed by VmaDeviceMemoryBlock and (in
// later phases) VmaAllocator. Wraps every Vulkan entry-point that the port
// needs to invoke directly, allowing test doubles to substitute for real GPU
// hardware without changing allocation logic.

using Silk.NET.Vulkan;

namespace CursedVMA.Internal
{
    internal unsafe interface IVulkanFunctions
    {
        // --- Memory lifecycle ---

        Result AllocateMemory(
            Device device,
            in MemoryAllocateInfo allocateInfo,
            AllocationCallbacks* pAllocator,
            out DeviceMemory memory);

        void FreeMemory(
            Device device,
            DeviceMemory memory,
            AllocationCallbacks* pAllocator);

        // --- Mapping ---

        Result MapMemory(
            Device device,
            DeviceMemory memory,
            ulong offset,
            ulong size,
            MemoryMapFlags flags,
            out void* ppData);

        void UnmapMemory(
            Device device,
            DeviceMemory memory);

        Result FlushMappedMemoryRanges(
            Device device,
            uint memoryRangeCount,
            MappedMemoryRange* pMemoryRanges);

        Result InvalidateMappedMemoryRanges(
            Device device,
            uint memoryRangeCount,
            MappedMemoryRange* pMemoryRanges);

        // --- Buffer/image binding ---

        Result BindBufferMemory(
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory,
            ulong memoryOffset);

        Result BindBufferMemory2(
            Device device,
            uint bindInfoCount,
            BindBufferMemoryInfo* pBindInfos);

        Result BindImageMemory(
            Device device,
            Image image,
            DeviceMemory memory,
            ulong memoryOffset);

        Result BindImageMemory2(
            Device device,
            uint bindInfoCount,
            BindImageMemoryInfo* pBindInfos);

        // --- Resource memory requirements ---

        void GetBufferMemoryRequirements(
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            out MemoryRequirements memoryRequirements);

        void GetImageMemoryRequirements(
            Device device,
            Image image,
            out MemoryRequirements memoryRequirements);

        // --- Resource lifecycle ---

        Result CreateBuffer(
            Device device,
            in BufferCreateInfo createInfo,
            AllocationCallbacks* pAllocator,
            out Silk.NET.Vulkan.Buffer buffer);

        void DestroyBuffer(
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            AllocationCallbacks* pAllocator);

        Result CreateImage(
            Device device,
            in ImageCreateInfo createInfo,
            AllocationCallbacks* pAllocator,
            out Image image);

        void DestroyImage(
            Device device,
            Image image,
            AllocationCallbacks* pAllocator);

        // --- Physical device queries ---

        void GetPhysicalDeviceMemoryProperties(
            PhysicalDevice physicalDevice,
            out PhysicalDeviceMemoryProperties memoryProperties);

        void GetPhysicalDeviceProperties(
            PhysicalDevice physicalDevice,
            out PhysicalDeviceProperties properties);
    }
}
