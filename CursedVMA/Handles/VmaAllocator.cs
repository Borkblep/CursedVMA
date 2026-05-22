// Ports VmaAllocator_T from vk_mem_alloc.cpp. Phase 7 implemented the core
// lifecycle. Phase 8 adds CreatePool / DestroyPool and per-pool tracking.
// Actual per-allocation entry-points (AllocateMemory, FreeMemory, …) land
// in Phase 9.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

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

        // User-created pools tracked for DestroyPool unregistration.
        private readonly List<VmaPool> m_Pools = new List<VmaPool>();
        private readonly object m_PoolsMutex = new object();
        private uint m_NextPoolId;

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
        /// Creates a custom memory pool that allocates from a specific memory type
        /// with caller-specified block size, algorithm, and count constraints.
        /// Equivalent to <c>vmaCreatePool</c>.
        /// </summary>
        /// <param name="createInfo">Pool parameters.</param>
        /// <param name="pool">On success, the new pool handle.</param>
        /// <returns><see cref="Result.Success"/> on success;
        /// <see cref="Result.ErrorInitializationFailed"/> if the memory type
        /// index is out of range.</returns>
        public unsafe Result CreatePool(
            in VmaPoolCreateInfo createInfo,
            out VmaPool? pool)
        {
            RequireNotDisposed();
            pool = null;

            if (createInfo.MemoryTypeIndex >= MemoryTypeCount)
                return Result.ErrorInitializationFailed;

            // Determine block size: explicit from caller, or derived from heap.
            ulong blockSize;
            bool explicitBlockSize;
            if (createInfo.BlockSize != 0)
            {
                blockSize = createInfo.BlockSize;
                explicitBlockSize = true;
            }
            else
            {
                uint heapIndex = GetMemoryType(createInfo.MemoryTypeIndex).HeapIndex;
                ulong heapSize = GetMemoryHeap(heapIndex).Size;
                blockSize = CalcPreferredBlockSize(heapSize, PreferredLargeHeapBlockSize);
                explicitBlockSize = false;
            }

            // Honor IgnoreBufferImageGranularityBit: set granularity to 1
            // so the metadata skips buffer-image-granularity alignment padding.
            ulong bufferImageGranularity =
                (createInfo.Flags & VmaPoolCreateFlags.IgnoreBufferImageGranularityBit) != 0
                    ? 1ul
                    : (DeviceLimits.BufferImageGranularity != 0
                        ? DeviceLimits.BufferImageGranularity : 1ul);

            nuint maxBlockCount =
                createInfo.MaxBlockCount == 0 ? nuint.MaxValue : createInfo.MaxBlockCount;

            ulong minAllocationAlignment =
                createInfo.MinAllocationAlignment != 0 ? createInfo.MinAllocationAlignment : 1;

            var blockVector = new VmaBlockVector(
                VkFunctions, Device, AllocatorCallbacks,
                createInfo.MemoryTypeIndex,
                blockSize,
                createInfo.MinBlockCount,
                maxBlockCount,
                bufferImageGranularity,
                createInfo.Flags & VmaPoolCreateFlags.AlgorithmMask,
                explicitBlockSize,
                minAllocationAlignment,
                (nint)createInfo.MemoryAllocateNext);

            Result r = blockVector.Init();
            if (r != Result.Success)
            {
                blockVector.Destroy();
                return r;
            }

            uint id;
            lock (m_PoolsMutex)
                id = m_NextPoolId++;

            var newPool = new VmaPool(blockVector, id);

            lock (m_PoolsMutex)
                m_Pools.Add(newPool);

            pool = newPool;
            return Result.Success;
        }

        /// <summary>
        /// Unregisters a pool from this allocator and releases all of its
        /// <c>VkDeviceMemory</c> blocks. Equivalent to <c>vmaDestroyPool</c>.
        /// </summary>
        public void DestroyPool(VmaPool pool)
        {
            RequireNotDisposed();

            lock (m_PoolsMutex)
                m_Pools.Remove(pool);

            pool.Dispose();
        }

        /// <summary>
        /// Tears down all default block vectors. Pools created via
        /// <see cref="CreatePool"/> are user-owned and must be explicitly
        /// destroyed with <see cref="DestroyPool"/> before this call.
        /// Equivalent to <c>vmaDestroyAllocator</c>. Safe to call more than once.
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

        internal int PoolCount { get { lock (m_PoolsMutex) return m_Pools.Count; } }

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
            return VmaMath.AlignUp(raw, 32ul);
        }
    }
}
