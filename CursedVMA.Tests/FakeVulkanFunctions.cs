// Shared fake IVulkanFunctions used across all test fixtures that need Vulkan
// dispatch without a real GPU. Call-count fields are public so tests can assert
// on how many times each entry-point was invoked. Configurable return values
// allow failure-path testing.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace CursedVMA.Tests
{
    internal sealed unsafe class FakeVulkanFunctions : IVulkanFunctions
    {
        // Pinned buffer returned by every MapMemory call.
        private static readonly byte[] s_MappedBuffer = new byte[4096];
        private static readonly GCHandle s_MappedHandle =
            GCHandle.Alloc(s_MappedBuffer, GCHandleType.Pinned);

        // Call counters — checked by tests.
        public int AllocateMemoryCallCount;
        public int FreeMemoryCallCount;
        public int MapMemoryCallCount;
        public int UnmapMemoryCallCount;
        public int BindBufferMemoryCallCount;
        public int BindBufferMemory2CallCount;
        public int BindImageMemoryCallCount;
        public int BindImageMemory2CallCount;

        // Configurable return values for failure-path tests.
        public Result AllocateMemoryResult = Result.Success;

        // Configurable responses for physical-device queries.
        public PhysicalDeviceMemoryProperties MemoryProperties;
        public PhysicalDeviceProperties DeviceProperties = default;

        private ulong m_NextMemoryHandle = 1;

        public Result AllocateMemory(
            Device device,
            in MemoryAllocateInfo allocateInfo,
            AllocationCallbacks* pAllocator,
            out DeviceMemory memory)
        {
            AllocateMemoryCallCount++;
            if (AllocateMemoryResult != Result.Success)
            {
                memory = default;
                return AllocateMemoryResult;
            }
            memory = new DeviceMemory(m_NextMemoryHandle++);
            return Result.Success;
        }

        public void FreeMemory(
            Device device,
            DeviceMemory memory,
            AllocationCallbacks* pAllocator)
            => FreeMemoryCallCount++;

        public Result MapMemory(
            Device device,
            DeviceMemory memory,
            ulong offset,
            ulong size,
            MemoryMapFlags flags,
            out void* ppData)
        {
            MapMemoryCallCount++;
            ppData = (void*)s_MappedHandle.AddrOfPinnedObject();
            return Result.Success;
        }

        public void UnmapMemory(Device device, DeviceMemory memory)
            => UnmapMemoryCallCount++;

        public Result FlushMappedMemoryRanges(
            Device device, uint memoryRangeCount, MappedMemoryRange* pMemoryRanges)
            => Result.Success;

        public Result InvalidateMappedMemoryRanges(
            Device device, uint memoryRangeCount, MappedMemoryRange* pMemoryRanges)
            => Result.Success;

        public Result BindBufferMemory(
            Device device, Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory, ulong memoryOffset)
        { BindBufferMemoryCallCount++; return Result.Success; }

        public Result BindBufferMemory2(
            Device device, uint bindInfoCount, BindBufferMemoryInfo* pBindInfos)
        { BindBufferMemory2CallCount++; return Result.Success; }

        public Result BindImageMemory(
            Device device, Image image, DeviceMemory memory, ulong memoryOffset)
        { BindImageMemoryCallCount++; return Result.Success; }

        public Result BindImageMemory2(
            Device device, uint bindInfoCount, BindImageMemoryInfo* pBindInfos)
        { BindImageMemory2CallCount++; return Result.Success; }

        public void GetBufferMemoryRequirements(
            Device device, Silk.NET.Vulkan.Buffer buffer, out MemoryRequirements r)
            => r = default;

        public void GetImageMemoryRequirements(
            Device device, Image image, out MemoryRequirements r)
            => r = default;

        public void GetPhysicalDeviceMemoryProperties(
            PhysicalDevice pd, out PhysicalDeviceMemoryProperties r)
            => r = MemoryProperties;

        public void GetPhysicalDeviceProperties(
            PhysicalDevice pd, out PhysicalDeviceProperties r)
            => r = DeviceProperties;
    }
}
