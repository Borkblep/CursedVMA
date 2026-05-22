// Ports VmaAllocator_T from vk_mem_alloc.cpp. Phase 7 implements the full
// lifecycle: Create (validate params, query device memory properties, build
// per-memory-type VmaBlockVectors) and Dispose (tear down all block vectors).
// Actual memory allocation entry-points (AllocateMemory, FreeMemory, …) land
// in Phase 9.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System;

namespace CursedVMA
{
    /// <summary>
    /// Top-level VMA object; manages all memory allocations for a single
    /// <c>VkDevice</c>. Equivalent to the C++ <c>VmaAllocator_T</c>.
    /// </summary>
    public sealed class VmaAllocator : IDisposable
    {
        // Default preferred block size when the caller passes zero.
        private const ulong DefaultPreferredLargeHeapBlockSize = 256ul * 1024 * 1024;

        // A heap smaller than this gets a block size of heapSize/8 instead.
        private const ulong SmallHeapMaxSize = 1024ul * 1024 * 1024;

        internal IVulkanFunctions VkFunctions { get; }
        internal PhysicalDevice PhysicalDevice { get; }
        internal Device Device { get; }
        internal AllocationCallbacks? AllocatorCallbacks { get; }
        internal PhysicalDeviceMemoryProperties MemoryProperties { get; }
        internal PhysicalDeviceLimits DeviceLimits { get; }
        internal VmaAllocatorCreateFlags Flags { get; }
        internal uint VulkanApiVersion { get; }
        internal ulong PreferredLargeHeapBlockSize { get; }

        // One default block vector per Vulkan memory type (up to VK_MAX_MEMORY_TYPES).
        private readonly VmaBlockVector?[] m_pBlockVectors =
            new VmaBlockVector[Vk.MaxMemoryTypes];

        private bool m_IsDisposed;

        private VmaAllocator(
            IVulkanFunctions vkFunctions,
            VmaAllocatorCreateFlags flags,
            PhysicalDevice physicalDevice,
            Device device,
            AllocationCallbacks? allocatorCallbacks,
            PhysicalDeviceMemoryProperties memoryProperties,
            PhysicalDeviceLimits deviceLimits,
            uint vulkanApiVersion,
            ulong preferredLargeHeapBlockSize)
        {
            VkFunctions = vkFunctions;
            Flags = flags;
            PhysicalDevice = physicalDevice;
            Device = device;
            AllocatorCallbacks = allocatorCallbacks;
            MemoryProperties = memoryProperties;
            DeviceLimits = deviceLimits;
            VulkanApiVersion = vulkanApiVersion;
            PreferredLargeHeapBlockSize = preferredLargeHeapBlockSize;
        }

        /// <summary>
        /// Creates a new allocator. Queries the physical device for memory
        /// properties, then creates a <see cref="VmaBlockVector"/> for each
        /// memory type. Equivalent to <c>vmaCreateAllocator</c>.
        /// </summary>
        /// <returns><see cref="Result.Success"/> on success;
        /// <see cref="Result.ErrorInitializationFailed"/> when required
        /// parameters are missing.</returns>
        public static Result Create(
            in VmaAllocatorCreateInfo createInfo,
            out VmaAllocator? allocator)
        {
            allocator = null;
            if (createInfo.VulkanApi == null)
                return Result.ErrorInitializationFailed;
            if (createInfo.PhysicalDevice.Handle == default)
                return Result.ErrorInitializationFailed;
            if (createInfo.Device.Handle == default)
                return Result.ErrorInitializationFailed;

            var vkFunctions = new VmaVulkanFunctions(createInfo.VulkanApi);
            return Create(vkFunctions, in createInfo, out allocator);
        }

        /// <summary>
        /// Internal overload that accepts a pre-built <see cref="IVulkanFunctions"/>
        /// directly; used by tests that inject a fake dispatch table.
        /// </summary>
        internal static Result Create(
            IVulkanFunctions vkFunctions,
            in VmaAllocatorCreateInfo createInfo,
            out VmaAllocator? allocator)
        {
            allocator = null;

            if (createInfo.PhysicalDevice.Handle == default)
                return Result.ErrorInitializationFailed;
            if (createInfo.Device.Handle == default)
                return Result.ErrorInitializationFailed;

            vkFunctions.GetPhysicalDeviceMemoryProperties(
                createInfo.PhysicalDevice, out PhysicalDeviceMemoryProperties memProps);
            vkFunctions.GetPhysicalDeviceProperties(
                createInfo.PhysicalDevice, out PhysicalDeviceProperties devProps);

            ulong preferredLargeBlockSize =
                createInfo.PreferredLargeHeapBlockSize == 0
                    ? DefaultPreferredLargeHeapBlockSize
                    : createInfo.PreferredLargeHeapBlockSize;

            // bufferImageGranularity of 0 would break alignment math; clamp to 1.
            ulong bufferImageGranularity =
                devProps.Limits.BufferImageGranularity != 0
                    ? devProps.Limits.BufferImageGranularity
                    : 1;

            var inst = new VmaAllocator(
                vkFunctions,
                createInfo.Flags,
                createInfo.PhysicalDevice,
                createInfo.Device,
                createInfo.AllocationCallbacks,
                memProps,
                devProps.Limits,
                createInfo.VulkanApiVersion,
                preferredLargeBlockSize);

            for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            {
                uint heapIndex = memProps.MemoryTypes[(int)i].HeapIndex;
                ulong heapSize = memProps.MemoryHeaps[(int)heapIndex].Size;
                ulong blockSize = CalcPreferredBlockSize(heapSize, preferredLargeBlockSize);

                inst.m_pBlockVectors[i] = new VmaBlockVector(
                    vkFunctions,
                    createInfo.Device,
                    createInfo.AllocationCallbacks,
                    memoryTypeIndex: i,
                    preferredBlockSize: blockSize,
                    minBlockCount: 0,
                    maxBlockCount: nuint.MaxValue,
                    bufferImageGranularity: bufferImageGranularity,
                    algorithm: VmaPoolCreateFlags.None,
                    explicitBlockSize: false,
                    minAllocationAlignment: 1);

                Result r = inst.m_pBlockVectors[i]!.Init();
                if (r != Result.Success)
                {
                    inst.Dispose();
                    return r;
                }
            }

            allocator = inst;
            return Result.Success;
        }

        /// <summary>
        /// Tears down all block vectors and frees their backing
        /// <c>VkDeviceMemory</c> objects. Equivalent to
        /// <c>vmaDestroyAllocator</c>. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (m_IsDisposed) return;
            m_IsDisposed = true;

            for (int i = 0; i < m_pBlockVectors.Length; i++)
            {
                m_pBlockVectors[i]?.Destroy();
                m_pBlockVectors[i] = null;
            }
        }

        // --- Internal accessors used by later phases ---

        internal uint MemoryTypeCount => MemoryProperties.MemoryTypeCount;

        internal MemoryType GetMemoryType(uint index)
            => MemoryProperties.MemoryTypes[(int)index];

        internal MemoryHeap GetMemoryHeap(uint index)
            => MemoryProperties.MemoryHeaps[(int)index];

        internal VmaBlockVector? GetDefaultBlockVector(uint memoryTypeIndex)
            => m_pBlockVectors[memoryTypeIndex];

        private void RequireNotDisposed()
        {
            if (m_IsDisposed)
                throw new ObjectDisposedException(nameof(VmaAllocator));
        }

        // --- Helpers ---

        private static ulong CalcPreferredBlockSize(
            ulong heapSize,
            ulong preferredLargeHeapBlockSize)
        {
            bool isSmallHeap = heapSize <= SmallHeapMaxSize;
            ulong raw = isSmallHeap ? heapSize / 8 : preferredLargeHeapBlockSize;
            // Round up to a 32-byte boundary, matching VMA's VmaAlignUp call.
            return VmaMath.AlignUp(raw, 32ul);
        }
    }
}
