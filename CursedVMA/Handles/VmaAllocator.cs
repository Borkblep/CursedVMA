// Ports VmaAllocator_T from vk_mem_alloc.cpp. Phase 7: core lifecycle.
// Phase 8: CreatePool / DestroyPool. Phase 9: per-allocation API
// (AllocateMemory, FreeMemory, Map/Unmap, Bind*). Phase 10: dedicated
// allocations triggered by VmaAllocationCreateFlags.DedicatedMemoryBit.
// Phase 11: allocator-wide statistics (GetStatistics, CalculateStatistics)
// and heap budgets (GetHeapBudgets). Phase 12: GetAllocatorInfo and JSON
// statistics dump (BuildStatsString). Phase 13: high-level convenience API
// (CreateBuffer/CreateImage), batch AllocateMemoryPages, and cache control
// (FlushAllocation/InvalidateAllocation).

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

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

        // Default priority for default (non-pool) block vectors.
        private const float DefaultMemoryPriority = 0.5f;

        // A heap smaller than this gets a block size of heapSize/8 instead.
        private const ulong SmallHeapMaxSize = 1024ul * 1024 * 1024;

        internal IVulkanFunctions VkFunctions { get; }
        internal Instance Instance { get; }
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

        // Dedicated allocations tracked for Dispose cleanup and statistics.
        private readonly List<VmaAllocation> m_DedicatedAllocations =
            new List<VmaAllocation>();
        private readonly object m_DedicatedMutex = new object();

        // Per-heap byte counters (signed for Interlocked.Add) and effective
        // size limits (real VK heap size capped by VmaAllocatorCreateInfo.HeapSizeLimit).
        private readonly long[] m_HeapBytes;
        private readonly ulong[] m_HeapSizeLimit;

        // Optional callbacks invoked after every vkAllocateMemory and before
        // every vkFreeMemory. Mirrors VmaDeviceMemoryCallbacks in C++ VMA.
        private readonly VmaDeviceMemoryCallbacks? m_DeviceMemoryCallbacks;

        private bool m_IsDisposed;

        private VmaAllocator(
            IVulkanFunctions vkFunctions,
            VmaAllocatorCreateFlags flags,
            Instance instance,
            PhysicalDevice physicalDevice,
            Device device,
            AllocationCallbacks? allocatorCallbacks,
            PhysicalDeviceMemoryProperties memoryProperties,
            PhysicalDeviceLimits deviceLimits,
            uint vulkanApiVersion,
            ulong preferredLargeHeapBlockSize,
            ulong[]? heapSizeLimit,
            VmaDeviceMemoryCallbacks? deviceMemoryCallbacks)
        {
            VkFunctions = vkFunctions;
            Flags = flags;
            Instance = instance;
            PhysicalDevice = physicalDevice;
            Device = device;
            AllocatorCallbacks = allocatorCallbacks;
            MemoryProperties = memoryProperties;
            DeviceLimits = deviceLimits;
            VulkanApiVersion = vulkanApiVersion;
            PreferredLargeHeapBlockSize = preferredLargeHeapBlockSize;
            m_DeviceMemoryCallbacks = deviceMemoryCallbacks;

            uint heapCount = memoryProperties.MemoryHeapCount;
            m_HeapBytes     = new long[heapCount];
            m_HeapSizeLimit = new ulong[heapCount];
            for (uint h = 0; h < heapCount; h++)
            {
                ulong real  = memoryProperties.MemoryHeaps[(int)h].Size;
                ulong limit = heapSizeLimit != null && h < heapSizeLimit.Length
                                && heapSizeLimit[h] != 0
                              ? heapSizeLimit[h]
                              : ulong.MaxValue;
                m_HeapSizeLimit[h] = Math.Min(real, limit);
            }
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
                createInfo.Instance,
                createInfo.PhysicalDevice,
                createInfo.Device,
                createInfo.AllocationCallbacks,
                memProps,
                devProps.Limits,
                createInfo.VulkanApiVersion,
                preferredLargeBlockSize,
                createInfo.HeapSizeLimit,
                createInfo.DeviceMemoryCallbacks);

            for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            {
                uint heapIndex = memProps.MemoryTypes[(int)i].HeapIndex;
                ulong heapSize = inst.m_HeapSizeLimit[heapIndex];
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
                    minAllocationAlignment: 1,
                    pMemoryAllocateNext: 0,
                    allocator: inst,
                    priority: DefaultMemoryPriority);

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
                ulong heapSize = m_HeapSizeLimit[heapIndex];
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
                (nint)createInfo.MemoryAllocateNext,
                allocator: this,
                priority: createInfo.Priority);

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

        // ── Phase 9: per-allocation entry-points ──────────────────────────────

        /// <summary>
        /// Selects the Vulkan memory type index that best satisfies the given
        /// requirements and allocation preferences. Equivalent to
        /// <c>vmaFindMemoryTypeIndex</c>.
        /// </summary>
        /// <param name="memoryTypeBits">Bitmask of acceptable types, typically
        /// from <see cref="MemoryRequirements.MemoryTypeBits"/>.</param>
        /// <param name="allocationCreateInfo">Usage and property-flag hints.</param>
        /// <param name="memoryTypeIndex">On success, the chosen type index.</param>
        /// <returns><see cref="Result.Success"/> when a matching type is found;
        /// <see cref="Result.ErrorFeatureNotPresent"/> otherwise.</returns>
        public Result FindMemoryTypeIndex(
            uint memoryTypeBits,
            in VmaAllocationCreateInfo allocationCreateInfo,
            out uint memoryTypeIndex)
        {
            RequireNotDisposed();
            memoryTypeIndex = uint.MaxValue;

            // MemoryTypeBits == 0 means "no filter" (allow all), matching C++ VMA.
            uint filter = allocationCreateInfo.MemoryTypeBits == 0
                ? uint.MaxValue
                : allocationCreateInfo.MemoryTypeBits;
            uint candidates = memoryTypeBits & filter;

            UsageToFlags(in allocationCreateInfo,
                out MemoryPropertyFlags required, out MemoryPropertyFlags preferred);
            required  |= allocationCreateInfo.RequiredFlags;
            preferred |= allocationCreateInfo.PreferredFlags;

            int bestScore = -1;
            for (uint i = 0; i < MemoryTypeCount; i++)
            {
                if ((candidates & (1u << (int)i)) == 0)
                    continue;

                MemoryPropertyFlags typeFlags = GetMemoryType(i).PropertyFlags;
                if ((typeFlags & required) != required)
                    continue;

                // Count how many preferred bits this type satisfies.
                int score = CountBits((uint)(typeFlags & preferred));
                if (score > bestScore)
                {
                    bestScore = score;
                    memoryTypeIndex = i;
                }
            }

            return memoryTypeIndex != uint.MaxValue
                ? Result.Success
                : Result.ErrorFeatureNotPresent;
        }

        /// <summary>
        /// Selects a memory type index suitable for an as-yet-uncreated buffer
        /// described by <paramref name="bufferCreateInfo"/>. Internally creates
        /// a throwaway <c>VkBuffer</c> just to query its memory requirements,
        /// then destroys it. Equivalent to
        /// <c>vmaFindMemoryTypeIndexForBufferInfo</c>.
        /// </summary>
        public unsafe Result FindMemoryTypeIndexForBufferInfo(
            in BufferCreateInfo bufferCreateInfo,
            in VmaAllocationCreateInfo allocationCreateInfo,
            out uint memoryTypeIndex)
        {
            RequireNotDisposed();
            memoryTypeIndex = uint.MaxValue;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateBuffer(
                Device, in bufferCreateInfo, pAc, out Silk.NET.Vulkan.Buffer probe);
            if (r != Result.Success)
                return r;

            VkFunctions.GetBufferMemoryRequirements(Device, probe, out MemoryRequirements req);
            VkFunctions.DestroyBuffer(Device, probe, pAc);

            return FindMemoryTypeIndex(
                req.MemoryTypeBits, in allocationCreateInfo, out memoryTypeIndex);
        }

        /// <summary>
        /// Selects a memory type index suitable for an as-yet-uncreated image
        /// described by <paramref name="imageCreateInfo"/>. Internally creates
        /// a throwaway <c>VkImage</c> just to query its memory requirements,
        /// then destroys it. Equivalent to
        /// <c>vmaFindMemoryTypeIndexForImageInfo</c>.
        /// </summary>
        public unsafe Result FindMemoryTypeIndexForImageInfo(
            in ImageCreateInfo imageCreateInfo,
            in VmaAllocationCreateInfo allocationCreateInfo,
            out uint memoryTypeIndex)
        {
            RequireNotDisposed();
            memoryTypeIndex = uint.MaxValue;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateImage(
                Device, in imageCreateInfo, pAc, out Image probe);
            if (r != Result.Success)
                return r;

            VkFunctions.GetImageMemoryRequirements(Device, probe, out MemoryRequirements req);
            VkFunctions.DestroyImage(Device, probe, pAc);

            return FindMemoryTypeIndex(
                req.MemoryTypeBits, in allocationCreateInfo, out memoryTypeIndex);
        }

        /// <summary>
        /// Allocates memory satisfying the given Vulkan memory requirements and
        /// VMA creation flags. When <see cref="VmaAllocationCreateInfo.Pool"/> is
        /// set the allocation is drawn from that pool; when
        /// <see cref="VmaAllocationCreateFlags.DedicatedMemoryBit"/> is set the
        /// allocation owns its own <c>VkDeviceMemory</c>; otherwise VMA selects
        /// the memory type and uses the corresponding default block vector.
        /// Equivalent to <c>vmaAllocateMemory</c>.
        /// </summary>
        public Result AllocateMemory(
            in MemoryRequirements vkMemReq,
            in VmaAllocationCreateInfo createInfo,
            out VmaAllocation? allocation)
        {
            RequireNotDisposed();
            return AllocateMemoryInternal(
                in vkMemReq, VmaSuballocationType.Unknown, in createInfo, out allocation);
        }

        /// <summary>
        /// Queries <c>vkGetBufferMemoryRequirements</c> and allocates memory
        /// suitable for binding to <paramref name="buffer"/>. Equivalent to
        /// <c>vmaAllocateMemoryForBuffer</c>.
        /// </summary>
        public Result AllocateMemoryForBuffer(
            Silk.NET.Vulkan.Buffer buffer,
            in VmaAllocationCreateInfo createInfo,
            out VmaAllocation? allocation)
        {
            RequireNotDisposed();
            VkFunctions.GetBufferMemoryRequirements(Device, buffer, out MemoryRequirements req);
            return AllocateMemoryInternal(
                in req, VmaSuballocationType.Buffer, in createInfo, out allocation);
        }

        /// <summary>
        /// Queries <c>vkGetImageMemoryRequirements</c> and allocates memory
        /// suitable for binding to <paramref name="image"/>. Uses
        /// <see cref="VmaSuballocationType.ImageOptimal"/> (the common case).
        /// Equivalent to <c>vmaAllocateMemoryForImage</c>.
        /// </summary>
        public Result AllocateMemoryForImage(
            Image image,
            in VmaAllocationCreateInfo createInfo,
            out VmaAllocation? allocation)
        {
            RequireNotDisposed();
            VkFunctions.GetImageMemoryRequirements(Device, image, out MemoryRequirements req);
            return AllocateMemoryInternal(
                in req, VmaSuballocationType.ImageOptimal, in createInfo, out allocation);
        }

        /// <summary>
        /// Releases an allocation and returns its memory to the pool. Null-safe.
        /// Dedicated allocations have their <c>VkDeviceMemory</c> freed directly.
        /// Equivalent to <c>vmaFreeMemory</c>.
        /// </summary>
        public void FreeMemory(VmaAllocation? allocation)
        {
            RequireNotDisposed();
            if (allocation == null)
                return;
            if (allocation.IsDedicated)
                FreeDedicatedMemory(allocation);
            else
                allocation.OwningBlockVector.Free(allocation);
        }

        /// <summary>
        /// Fills <paramref name="info"/> with extended allocation state including
        /// the parent block size and a dedicated-allocation flag. Equivalent to
        /// <c>vmaGetAllocationInfo2</c>.
        /// </summary>
        public unsafe void GetAllocationInfo2(VmaAllocation allocation, out VmaAllocationInfo2 info)
        {
            RequireNotDisposed();
            allocation.GetInfo(out VmaAllocationInfo baseInfo);
            info = new VmaAllocationInfo2
            {
                AllocationInfo = baseInfo,
                BlockSize = allocation.IsDedicated
                    ? allocation.Size
                    : allocation.Block.Metadata.GetSize(),
                DedicatedMemory = allocation.IsDedicated,
            };
        }

        /// <summary>
        /// Fills <paramref name="info"/> with a snapshot of the allocation's
        /// current state. Equivalent to <c>vmaGetAllocationInfo</c>.
        /// </summary>
        public unsafe void GetAllocationInfo(VmaAllocation allocation, out VmaAllocationInfo info)
        {
            RequireNotDisposed();
            allocation.GetInfo(out info);
        }

        /// <summary>
        /// Attaches arbitrary user data to an allocation. Equivalent to
        /// <c>vmaSetAllocationUserData</c>.
        /// </summary>
        public void SetAllocationUserData(VmaAllocation allocation, object? userData)
        {
            RequireNotDisposed();
            allocation.UserData = userData;
        }

        /// <summary>
        /// Attaches a debug name to an allocation. Equivalent to
        /// <c>vmaSetAllocationName</c>.
        /// </summary>
        public void SetAllocationName(VmaAllocation allocation, string? name)
        {
            RequireNotDisposed();
            allocation.Name = name;
        }

        /// <summary>
        /// Maps the allocation's memory and returns a host-accessible pointer.
        /// Reference-counted: each <see cref="MapMemory"/> must be paired with
        /// an <see cref="UnmapMemory"/>. Equivalent to <c>vmaMapMemory</c>.
        /// </summary>
        public unsafe Result MapMemory(VmaAllocation allocation, out void* ppData)
        {
            RequireNotDisposed();
            return allocation.Map(VkFunctions, Device, out ppData);
        }

        /// <summary>
        /// Decrements the allocation's map reference count; calls
        /// <c>vkUnmapMemory</c> when it reaches zero. Equivalent to
        /// <c>vmaUnmapMemory</c>.
        /// </summary>
        public void UnmapMemory(VmaAllocation allocation)
        {
            RequireNotDisposed();
            allocation.Unmap(VkFunctions, Device);
        }

        /// <summary>
        /// Binds a buffer to this allocation's memory at
        /// <c>allocation.Offset</c>. Equivalent to <c>vmaBindBufferMemory</c>.
        /// </summary>
        public unsafe Result BindBufferMemory(
            VmaAllocation allocation,
            Silk.NET.Vulkan.Buffer buffer)
        {
            RequireNotDisposed();
            return allocation.BindBufferMemory(VkFunctions, Device, 0, buffer, null);
        }

        /// <summary>
        /// Binds a buffer to this allocation's memory at
        /// <c>allocation.Offset + allocationLocalOffset</c>, forwarding
        /// <paramref name="pNext"/> to <c>vkBindBufferMemory2</c>. Equivalent
        /// to <c>vmaBindBufferMemory2</c>.
        /// </summary>
        public unsafe Result BindBufferMemory2(
            VmaAllocation allocation,
            ulong allocationLocalOffset,
            Silk.NET.Vulkan.Buffer buffer,
            void* pNext)
        {
            RequireNotDisposed();
            return allocation.BindBufferMemory(
                VkFunctions, Device, allocationLocalOffset, buffer, pNext);
        }

        /// <summary>
        /// Binds an image to this allocation's memory at
        /// <c>allocation.Offset</c>. Equivalent to <c>vmaBindImageMemory</c>.
        /// </summary>
        public unsafe Result BindImageMemory(VmaAllocation allocation, Image image)
        {
            RequireNotDisposed();
            return allocation.BindImageMemory(VkFunctions, Device, 0, image, null);
        }

        /// <summary>
        /// Binds an image to this allocation's memory at
        /// <c>allocation.Offset + allocationLocalOffset</c>, forwarding
        /// <paramref name="pNext"/> to <c>vkBindImageMemory2</c>. Equivalent
        /// to <c>vmaBindImageMemory2</c>.
        /// </summary>
        public unsafe Result BindImageMemory2(
            VmaAllocation allocation,
            ulong allocationLocalOffset,
            Image image,
            void* pNext)
        {
            RequireNotDisposed();
            return allocation.BindImageMemory(
                VkFunctions, Device, allocationLocalOffset, image, pNext);
        }

        // ── Phase 11: allocator-wide statistics and heap budgets ─────────────

        /// <summary>
        /// Fills per-memory-type lightweight statistics across all block
        /// vectors, custom pools, and dedicated allocations. Equivalent to
        /// <c>vmaGetStatistics</c>. <paramref name="outStats"/> should have at
        /// least <see cref="MemoryTypeCount"/> entries; extra entries are
        /// zeroed.
        /// </summary>
        public void GetStatistics(Span<VmaStatistics> outStats)
        {
            RequireNotDisposed();
            outStats.Clear();

            uint count = Math.Min((uint)outStats.Length, MemoryTypeCount);
            for (uint i = 0; i < count; i++)
                m_pBlockVectors[i]?.AddStatistics(ref outStats[(int)i]);

            lock (m_PoolsMutex)
            {
                foreach (var pool in m_Pools)
                {
                    uint typeIndex = pool.BlockVector.MemoryTypeIndex;
                    if (typeIndex < count)
                        pool.BlockVector.AddStatistics(ref outStats[(int)typeIndex]);
                }
            }

            lock (m_DedicatedMutex)
            {
                foreach (var alloc in m_DedicatedAllocations)
                {
                    uint typeIndex = alloc.MemoryTypeIndex;
                    if (typeIndex < count)
                    {
                        outStats[(int)typeIndex].BlockCount++;
                        outStats[(int)typeIndex].BlockBytes      += alloc.Size;
                        outStats[(int)typeIndex].AllocationCount++;
                        outStats[(int)typeIndex].AllocationBytes += alloc.Size;
                    }
                }
            }
        }

        /// <summary>
        /// Fills <paramref name="stats"/> with detailed statistics broken down
        /// by memory type, memory heap, and the allocator total. Equivalent to
        /// <c>vmaCalculateStatistics</c>.
        /// </summary>
        public void CalculateStatistics(out VmaTotalStatistics stats)
        {
            RequireNotDisposed();
            stats = default;

            for (uint typeIndex = 0; typeIndex < MemoryTypeCount; typeIndex++)
            {
                VmaDetailedStatistics typeStats = default;

                m_pBlockVectors[typeIndex]?.AddDetailedStatistics(ref typeStats);

                lock (m_PoolsMutex)
                {
                    foreach (var pool in m_Pools)
                        if (pool.BlockVector.MemoryTypeIndex == typeIndex)
                            pool.BlockVector.AddDetailedStatistics(ref typeStats);
                }

                lock (m_DedicatedMutex)
                {
                    foreach (var alloc in m_DedicatedAllocations)
                    {
                        if (alloc.MemoryTypeIndex == typeIndex)
                        {
                            typeStats.Statistics.BlockCount++;
                            typeStats.Statistics.BlockBytes += alloc.Size;
                            VmaStatisticsHelper.AddDetailedStatisticsAllocation(ref typeStats, alloc.Size);
                        }
                    }
                }

                uint heapIndex = GetMemoryType(typeIndex).HeapIndex;
                VmaStatisticsHelper.MergeDetailedStatistics(
                    ref stats.MemoryType[(int)typeIndex], in typeStats);
                VmaStatisticsHelper.MergeDetailedStatistics(
                    ref stats.MemoryHeap[(int)heapIndex], in typeStats);
                VmaStatisticsHelper.MergeDetailedStatistics(ref stats.Total, in typeStats);
            }
        }

        /// <summary>
        /// Fills per-heap budget information. Statistics are aggregated across
        /// all memory types that belong to each heap. Without
        /// <c>VK_EXT_memory_budget</c> the budget is estimated as 80% of the
        /// heap size and the usage equals the currently allocated block bytes.
        /// Equivalent to <c>vmaGetHeapBudgets</c>.
        /// <paramref name="outBudgets"/> should have at least
        /// <see cref="MemoryHeapCount"/> entries; extra entries are zeroed.
        /// </summary>
        public void GetHeapBudgets(Span<VmaBudget> outBudgets)
        {
            RequireNotDisposed();
            outBudgets.Clear();

            Span<VmaStatistics> typeStats = stackalloc VmaStatistics[(int)MemoryTypeCount];
            GetStatistics(typeStats);

            uint heapCount = Math.Min((uint)outBudgets.Length, MemoryProperties.MemoryHeapCount);
            for (uint heapIndex = 0; heapIndex < heapCount; heapIndex++)
            {
                outBudgets[(int)heapIndex] = default;

                for (uint typeIndex = 0; typeIndex < MemoryTypeCount; typeIndex++)
                {
                    if (GetMemoryType(typeIndex).HeapIndex == heapIndex)
                    {
                        VmaStatisticsHelper.MergeStatistics(
                            ref outBudgets[(int)heapIndex].Statistics,
                            in typeStats[(int)typeIndex]);
                    }
                }

                ulong heapSize = GetMemoryHeap(heapIndex).Size;
                outBudgets[(int)heapIndex].Usage   = outBudgets[(int)heapIndex].Statistics.BlockBytes;
                outBudgets[(int)heapIndex].Budget  = heapSize * 8 / 10;
            }
        }

        // ── Phase 12: allocator info + JSON statistics dump ──────────────────

        /// <summary>
        /// Returns a snapshot of the Vulkan handles this allocator was created
        /// with. Equivalent to <c>vmaGetAllocatorInfo</c>.
        /// </summary>
        public void GetAllocatorInfo(out VmaAllocatorInfo info)
        {
            RequireNotDisposed();
            info = new VmaAllocatorInfo
            {
                Instance       = Instance,
                PhysicalDevice = PhysicalDevice,
                Device         = Device,
            };
        }

        /// <summary>
        /// Builds a JSON string describing the allocator's state — general
        /// device info, total statistics, per-heap and per-type breakdown, and
        /// custom pools. When <paramref name="detailedMap"/> is true the output
        /// also lists per-block suballocation offsets and sizes. Equivalent to
        /// <c>vmaBuildStatsString</c> (no separate free call needed — the
        /// returned string is GC-managed).
        /// </summary>
        public string BuildStatsString(bool detailedMap)
        {
            RequireNotDisposed();

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream,
                new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                WriteGeneralSection(writer);

                CalculateStatistics(out var total);
                writer.WritePropertyName("Total");
                WriteDetailedStatistics(writer, in total.Total);

                WriteMemoryHeapsSection(writer, in total);
                WriteMemoryTypesSection(writer, in total, detailedMap);
                WritePoolsSection(writer, detailedMap);

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void WriteGeneralSection(Utf8JsonWriter writer)
        {
            writer.WritePropertyName("General");
            writer.WriteStartObject();
            writer.WriteString("API", "Vulkan");
            writer.WriteString("apiVersion", FormatApiVersion(VulkanApiVersion));
            writer.WriteNumber("maxMemoryAllocationCount", DeviceLimits.MaxMemoryAllocationCount);
            writer.WriteNumber("bufferImageGranularity",   DeviceLimits.BufferImageGranularity);
            writer.WriteNumber("nonCoherentAtomSize",      DeviceLimits.NonCoherentAtomSize);
            writer.WriteNumber("memoryHeapCount",          MemoryHeapCount);
            writer.WriteNumber("memoryTypeCount",          MemoryTypeCount);
            writer.WriteEndObject();
        }

        private void WriteMemoryHeapsSection(Utf8JsonWriter writer, in VmaTotalStatistics total)
        {
            Span<VmaBudget> budgets = stackalloc VmaBudget[(int)MemoryHeapCount];
            GetHeapBudgets(budgets);

            writer.WritePropertyName("MemoryHeaps");
            writer.WriteStartArray();
            for (uint h = 0; h < MemoryHeapCount; h++)
            {
                MemoryHeap heap = GetMemoryHeap(h);
                writer.WriteStartObject();
                writer.WriteNumber("Index", h);
                writer.WriteNumber("Size", heap.Size);
                writer.WritePropertyName("Flags");
                WriteMemoryHeapFlags(writer, heap.Flags);
                writer.WriteNumber("BudgetBytes", budgets[(int)h].Budget);
                writer.WriteNumber("UsageBytes",  budgets[(int)h].Usage);
                writer.WritePropertyName("Stats");
                WriteDetailedStatistics(writer, in total.MemoryHeap[(int)h]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private void WriteMemoryTypesSection(Utf8JsonWriter writer,
            in VmaTotalStatistics total, bool detailedMap)
        {
            writer.WritePropertyName("MemoryTypes");
            writer.WriteStartArray();
            for (uint t = 0; t < MemoryTypeCount; t++)
            {
                MemoryType mt = GetMemoryType(t);
                writer.WriteStartObject();
                writer.WriteNumber("Index", t);
                writer.WriteNumber("HeapIndex", mt.HeapIndex);
                writer.WritePropertyName("Flags");
                WriteMemoryPropertyFlags(writer, mt.PropertyFlags);
                writer.WritePropertyName("Stats");
                WriteDetailedStatistics(writer, in total.MemoryType[(int)t]);

                if (detailedMap)
                {
                    var bv = m_pBlockVectors[t];
                    if (bv != null)
                        WriteBlockArray(writer, bv.GetBlockSnapshot());

                    writer.WritePropertyName("DedicatedAllocations");
                    writer.WriteStartArray();
                    lock (m_DedicatedMutex)
                    {
                        foreach (var alloc in m_DedicatedAllocations)
                        {
                            if (alloc.MemoryTypeIndex != t) continue;
                            WriteDedicatedAllocation(writer, alloc);
                        }
                    }
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private void WritePoolsSection(Utf8JsonWriter writer, bool detailedMap)
        {
            writer.WritePropertyName("Pools");
            writer.WriteStartArray();
            lock (m_PoolsMutex)
            {
                foreach (var pool in m_Pools)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("Id", pool.Id);
                    if (pool.Name != null)
                        writer.WriteString("Name", pool.Name);
                    writer.WriteNumber("MemoryTypeIndex", pool.BlockVector.MemoryTypeIndex);
                    writer.WriteNumber("BlockSize", pool.BlockVector.PreferredBlockSize);

                    pool.CalculateStatistics(out var poolStats);
                    writer.WritePropertyName("Stats");
                    WriteDetailedStatistics(writer, in poolStats);

                    if (detailedMap)
                        WriteBlockArray(writer, pool.BlockVector.GetBlockSnapshot());

                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }

        private static void WriteBlockArray(Utf8JsonWriter writer, VmaDeviceMemoryBlock[] blocks)
        {
            writer.WritePropertyName("Blocks");
            writer.WriteStartArray();
            foreach (var block in blocks)
                WriteBlockDetailed(writer, block);
            writer.WriteEndArray();
        }

        private static void WriteBlockDetailed(Utf8JsonWriter writer, VmaDeviceMemoryBlock block)
        {
            writer.WriteStartObject();
            writer.WriteNumber("Id", block.Id);
            writer.WriteNumber("Size", block.Metadata.GetSize());
            writer.WriteNumber("AllocationCount", (ulong)block.Metadata.GetAllocationCount());
            writer.WriteNumber("FreeBytes", block.Metadata.GetSumFreeSize());
            writer.WriteNumber("MapCount", block.MapCount);

            writer.WritePropertyName("Suballocations");
            writer.WriteStartArray();
            ulong handle = block.Metadata.GetAllocationListBegin();
            while (handle != 0)
            {
                block.Metadata.GetAllocationInfo(handle, out var info);
                writer.WriteStartObject();
                writer.WriteNumber("Offset", info.Offset);
                writer.WriteNumber("Size", info.Size);
                writer.WriteEndObject();
                handle = block.Metadata.GetNextAllocation(handle);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        private static void WriteDedicatedAllocation(Utf8JsonWriter writer, VmaAllocation alloc)
        {
            writer.WriteStartObject();
            writer.WriteNumber("Size", alloc.Size);
            if (alloc.Name != null)
                writer.WriteString("Name", alloc.Name);
            writer.WriteEndObject();
        }

        private static void WriteDetailedStatistics(Utf8JsonWriter writer, in VmaDetailedStatistics s)
        {
            writer.WriteStartObject();
            writer.WriteNumber("BlockCount",         s.Statistics.BlockCount);
            writer.WriteNumber("BlockBytes",         s.Statistics.BlockBytes);
            writer.WriteNumber("AllocationCount",    s.Statistics.AllocationCount);
            writer.WriteNumber("AllocationBytes",    s.Statistics.AllocationBytes);
            writer.WriteNumber("UnusedRangeCount",   s.UnusedRangeCount);
            writer.WriteNumber("AllocationSizeMin",  s.AllocationSizeMin);
            writer.WriteNumber("AllocationSizeMax",  s.AllocationSizeMax);
            writer.WriteNumber("UnusedRangeSizeMin", s.UnusedRangeSizeMin);
            writer.WriteNumber("UnusedRangeSizeMax", s.UnusedRangeSizeMax);
            writer.WriteEndObject();
        }

        private static void WriteMemoryPropertyFlags(Utf8JsonWriter writer, MemoryPropertyFlags f)
        {
            writer.WriteStartArray();
            if ((f & MemoryPropertyFlags.DeviceLocalBit)     != 0) writer.WriteStringValue("DeviceLocal");
            if ((f & MemoryPropertyFlags.HostVisibleBit)     != 0) writer.WriteStringValue("HostVisible");
            if ((f & MemoryPropertyFlags.HostCoherentBit)    != 0) writer.WriteStringValue("HostCoherent");
            if ((f & MemoryPropertyFlags.HostCachedBit)      != 0) writer.WriteStringValue("HostCached");
            if ((f & MemoryPropertyFlags.LazilyAllocatedBit) != 0) writer.WriteStringValue("LazilyAllocated");
            if ((f & MemoryPropertyFlags.ProtectedBit)       != 0) writer.WriteStringValue("Protected");
            writer.WriteEndArray();
        }

        private static void WriteMemoryHeapFlags(Utf8JsonWriter writer, MemoryHeapFlags f)
        {
            writer.WriteStartArray();
            if ((f & MemoryHeapFlags.DeviceLocalBit)   != 0) writer.WriteStringValue("DeviceLocal");
            if ((f & MemoryHeapFlags.MultiInstanceBit) != 0) writer.WriteStringValue("MultiInstance");
            writer.WriteEndArray();
        }

        // VK_MAKE_API_VERSION packs major/minor/patch into a single uint.
        private static string FormatApiVersion(uint v)
        {
            if (v == 0) return "0.0.0";
            uint major = (v >> 22) & 0x7Fu;
            uint minor = (v >> 12) & 0x3FFu;
            uint patch =  v        & 0xFFFu;
            return $"{major}.{minor}.{patch}";
        }

        // ── Phase 13: high-level convenience and cache control ───────────────

        /// <summary>
        /// Creates a <c>VkBuffer</c>, allocates memory for it, and binds the
        /// buffer to that memory. On failure all partial state is rolled back.
        /// Equivalent to <c>vmaCreateBuffer</c>.
        /// </summary>
        public unsafe Result CreateBuffer(
            in BufferCreateInfo bufferCreateInfo,
            in VmaAllocationCreateInfo allocationCreateInfo,
            out Silk.NET.Vulkan.Buffer buffer,
            out VmaAllocation? allocation,
            out VmaAllocationInfo allocationInfo)
        {
            RequireNotDisposed();
            buffer         = default;
            allocation     = null;
            allocationInfo = default;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateBuffer(Device, in bufferCreateInfo, pAc, out buffer);
            if (r != Result.Success)
                return r;

            r = AllocateMemoryForBuffer(buffer, in allocationCreateInfo, out allocation);
            if (r != Result.Success)
            {
                VkFunctions.DestroyBuffer(Device, buffer, pAc);
                buffer = default;
                return r;
            }

            // Honor DontBindBit: caller will bind the buffer manually.
            if ((allocationCreateInfo.Flags & VmaAllocationCreateFlags.DontBindBit) == 0)
            {
                r = BindBufferMemory(allocation!, buffer);
                if (r != Result.Success)
                {
                    FreeMemory(allocation);
                    VkFunctions.DestroyBuffer(Device, buffer, pAc);
                    buffer     = default;
                    allocation = null;
                    return r;
                }
            }

            allocation!.GetInfo(out allocationInfo);
            return Result.Success;
        }

        /// <summary>
        /// Destroys a buffer created by <see cref="CreateBuffer"/> and frees its
        /// allocation. Either argument may be null/default. Equivalent to
        /// <c>vmaDestroyBuffer</c>.
        /// </summary>
        public unsafe void DestroyBuffer(Silk.NET.Vulkan.Buffer buffer, VmaAllocation? allocation)
        {
            RequireNotDisposed();
            if (buffer.Handle != 0ul)
            {
                AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
                AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;
                VkFunctions.DestroyBuffer(Device, buffer, pAc);
            }
            if (allocation != null)
                FreeMemory(allocation);
        }

        /// <summary>
        /// Creates a <c>VkImage</c>, allocates memory for it, and binds the
        /// image to that memory. On failure all partial state is rolled back.
        /// Equivalent to <c>vmaCreateImage</c>.
        /// </summary>
        public unsafe Result CreateImage(
            in ImageCreateInfo imageCreateInfo,
            in VmaAllocationCreateInfo allocationCreateInfo,
            out Image image,
            out VmaAllocation? allocation,
            out VmaAllocationInfo allocationInfo)
        {
            RequireNotDisposed();
            image          = default;
            allocation     = null;
            allocationInfo = default;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateImage(Device, in imageCreateInfo, pAc, out image);
            if (r != Result.Success)
                return r;

            r = AllocateMemoryForImage(image, in allocationCreateInfo, out allocation);
            if (r != Result.Success)
            {
                VkFunctions.DestroyImage(Device, image, pAc);
                image = default;
                return r;
            }

            // Honor DontBindBit: caller will bind the image manually.
            if ((allocationCreateInfo.Flags & VmaAllocationCreateFlags.DontBindBit) == 0)
            {
                r = BindImageMemory(allocation!, image);
                if (r != Result.Success)
                {
                    FreeMemory(allocation);
                    VkFunctions.DestroyImage(Device, image, pAc);
                    image      = default;
                    allocation = null;
                    return r;
                }
            }

            allocation!.GetInfo(out allocationInfo);
            return Result.Success;
        }

        /// <summary>
        /// Destroys an image created by <see cref="CreateImage"/> and frees its
        /// allocation. Either argument may be null/default. Equivalent to
        /// <c>vmaDestroyImage</c>.
        /// </summary>
        public unsafe void DestroyImage(Image image, VmaAllocation? allocation)
        {
            RequireNotDisposed();
            if (image.Handle != 0ul)
            {
                AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
                AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;
                VkFunctions.DestroyImage(Device, image, pAc);
            }
            if (allocation != null)
                FreeMemory(allocation);
        }

        /// <summary>
        /// Batch-allocates memory for several resources with all-or-nothing
        /// semantics. On any per-element failure, every allocation already made
        /// in the batch is freed. Equivalent to <c>vmaAllocateMemoryPages</c>.
        /// </summary>
        public Result AllocateMemoryPages(
            ReadOnlySpan<MemoryRequirements> memoryRequirements,
            ReadOnlySpan<VmaAllocationCreateInfo> createInfos,
            Span<VmaAllocation?> allocations)
        {
            RequireNotDisposed();
            if (memoryRequirements.Length != allocations.Length ||
                createInfos.Length        != allocations.Length)
                return Result.ErrorInitializationFailed;

            int allocCount = allocations.Length;
            for (int i = 0; i < allocCount; i++)
            {
                Result r = AllocateMemoryInternal(
                    in memoryRequirements[i],
                    VmaSuballocationType.Unknown,
                    in createInfos[i],
                    out var a);
                if (r != Result.Success)
                {
                    for (int j = 0; j < i; j++)
                    {
                        FreeMemory(allocations[j]);
                        allocations[j] = null;
                    }
                    allocations[i] = null;
                    return r;
                }
                allocations[i] = a;
            }
            return Result.Success;
        }

        /// <summary>
        /// Frees every allocation in <paramref name="allocations"/>; null
        /// entries are ignored. Equivalent to <c>vmaFreeMemoryPages</c>.
        /// </summary>
        public void FreeMemoryPages(ReadOnlySpan<VmaAllocation?> allocations)
        {
            RequireNotDisposed();
            for (int i = 0; i < allocations.Length; i++)
                if (allocations[i] != null)
                    FreeMemory(allocations[i]);
        }

        /// <summary>
        /// Flushes a host-write of <paramref name="size"/> bytes at
        /// <paramref name="offset"/> within <paramref name="allocation"/>. No-op
        /// when the backing memory type is <c>HostCoherent</c>; otherwise the
        /// range is aligned to <c>nonCoherentAtomSize</c> and forwarded to
        /// <c>vkFlushMappedMemoryRanges</c>. Pass <c>Vk.WholeSize</c> as
        /// <paramref name="size"/> to cover the whole allocation. Equivalent
        /// to <c>vmaFlushAllocation</c>.
        /// </summary>
        public unsafe Result FlushAllocation(
            VmaAllocation allocation, ulong offset, ulong size)
        {
            RequireNotDisposed();
            if (!IsMemoryTypeNonCoherent(allocation.MemoryTypeIndex))
                return Result.Success;

            MappedMemoryRange range = BuildMappedMemoryRange(allocation, offset, size);
            return VkFunctions.FlushMappedMemoryRanges(Device, 1, &range);
        }

        /// <summary>
        /// Invalidates a CPU cache region before reading host-visible memory.
        /// Symmetric to <see cref="FlushAllocation"/>; equivalent to
        /// <c>vmaInvalidateAllocation</c>.
        /// </summary>
        public unsafe Result InvalidateAllocation(
            VmaAllocation allocation, ulong offset, ulong size)
        {
            RequireNotDisposed();
            if (!IsMemoryTypeNonCoherent(allocation.MemoryTypeIndex))
                return Result.Success;

            MappedMemoryRange range = BuildMappedMemoryRange(allocation, offset, size);
            return VkFunctions.InvalidateMappedMemoryRanges(Device, 1, &range);
        }

        private bool IsMemoryTypeNonCoherent(uint memoryTypeIndex)
        {
            MemoryPropertyFlags f = GetMemoryType(memoryTypeIndex).PropertyFlags;
            return (f & MemoryPropertyFlags.HostVisibleBit)  != 0
                && (f & MemoryPropertyFlags.HostCoherentBit) == 0;
        }

        // Builds the VkMappedMemoryRange for a flush/invalidate.
        //   * Offset is rounded down to the nonCoherentAtomSize boundary.
        //   * Size is rounded up; for VK_WHOLE_SIZE it covers the whole alloc.
        //   * For block-backed allocations the allocation's offset within the
        //     VkDeviceMemory is added back in.
        private MappedMemoryRange BuildMappedMemoryRange(
            VmaAllocation allocation, ulong offset, ulong size)
        {
            ulong atomSize  = DeviceLimits.NonCoherentAtomSize != 0
                ? DeviceLimits.NonCoherentAtomSize : 1ul;
            ulong allocSize = allocation.Size;

            ulong alignedOffset = (offset / atomSize) * atomSize;
            ulong rangeSize;
            if (size == Vk.WholeSize)
            {
                rangeSize = allocSize - alignedOffset;
            }
            else
            {
                ulong unaligned = size + (offset - alignedOffset);
                rangeSize = ((unaligned + atomSize - 1) / atomSize) * atomSize;
                if (rangeSize > allocSize - alignedOffset)
                    rangeSize = allocSize - alignedOffset;
            }

            ulong absOffset = alignedOffset;
            if (!allocation.IsDedicated)
                absOffset += allocation.Offset;

            return new MappedMemoryRange
            {
                SType  = StructureType.MappedMemoryRange,
                Memory = allocation.Memory,
                Offset = absOffset,
                Size   = rangeSize,
            };
        }

        // ── Phase 14: defragmentation ────────────────────────────────────────

        /// <summary>
        /// Starts a defragmentation session. Each pass is executed by calling
        /// <see cref="BeginDefragmentationPass"/> / <see cref="EndDefragmentationPass"/>
        /// in a loop until <see cref="EndDefragmentationPass"/> returns 0 moves.
        /// Finish with <see cref="EndDefragmentation"/>. Equivalent to
        /// <c>vmaBeginDefragmentation</c>.
        /// </summary>
        public Result BeginDefragmentation(
            in VmaDefragmentationInfo defragmentationInfo,
            out VmaDefragmentationContext context)
        {
            RequireNotDisposed();
            context = new VmaDefragmentationContext(this, in defragmentationInfo);
            return Result.Success;
        }

        /// <summary>
        /// Ends a defragmentation session, collects accumulated statistics into
        /// <paramref name="defragmentationStats"/>, and disposes the context.
        /// Equivalent to <c>vmaEndDefragmentation</c>.
        /// </summary>
        public void EndDefragmentation(
            VmaDefragmentationContext context,
            out VmaDefragmentationStats defragmentationStats)
        {
            RequireNotDisposed();
            context.GetStats(out defragmentationStats);
            context.Dispose();
        }

        /// <summary>
        /// Plans the next defragmentation pass: selects the emptiest non-empty
        /// block, attempts to fit each of its (unmapped) allocations into other
        /// existing blocks, and returns the proposed move list in
        /// <paramref name="passInfo"/>. The caller should copy data between each
        /// <see cref="VmaDefragmentationMove.SrcAllocation"/> and
        /// <see cref="VmaDefragmentationMove.DstTmpAllocation"/>, then mark the
        /// <see cref="VmaDefragmentationMove.Operation"/> accordingly before
        /// calling <see cref="EndDefragmentationPass"/>. Equivalent to
        /// <c>vmaBeginDefragmentationPass</c>.
        /// </summary>
        public Result BeginDefragmentationPass(
            VmaDefragmentationContext context,
            out VmaDefragmentationPassMoveInfo passInfo)
        {
            RequireNotDisposed();
            return context.BeginPass(out passInfo);
        }

        /// <summary>
        /// Applies the caller's decisions from a defragmentation pass.
        /// <see cref="VmaDefragmentationMoveOperation.Copy"/> swaps the source
        /// allocation to the destination location and frees the old slot;
        /// <see cref="VmaDefragmentationMoveOperation.Ignore"/> releases the
        /// reserved temporary allocation; <see cref="VmaDefragmentationMoveOperation.Destroy"/>
        /// frees both the source slot and the temporary allocation. Equivalent
        /// to <c>vmaEndDefragmentationPass</c>.
        /// </summary>
        public Result EndDefragmentationPass(
            VmaDefragmentationContext context,
            ref VmaDefragmentationPassMoveInfo passInfo)
        {
            RequireNotDisposed();
            return context.EndPass(ref passInfo);
        }

        // ── Tears down all default block vectors ──────────────────────────────

        /// <summary>
        /// Tears down all default block vectors and frees every still-live
        /// dedicated allocation. Pools created via <see cref="CreatePool"/> are
        /// user-owned and must be explicitly destroyed with
        /// <see cref="DestroyPool"/> before this call. Equivalent to
        /// <c>vmaDestroyAllocator</c>. Safe to call more than once.
        /// </summary>
        public unsafe void Dispose()
        {
            if (m_IsDisposed) return;
            m_IsDisposed = true;

            // Free any dedicated allocations the caller didn't release.
            lock (m_DedicatedMutex)
            {
                if (m_DedicatedAllocations.Count > 0)
                {
                    AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
                    AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;
                    foreach (var alloc in m_DedicatedAllocations)
                    {
                        NotifyDeviceMemoryFreed(alloc.MemoryTypeIndex, alloc.DedicatedMemory, alloc.Size);
                        VkFunctions.FreeMemory(Device, alloc.DedicatedMemory, pAc);
                        ReleaseHeapBytes(alloc.MemoryTypeIndex, alloc.Size);
                    }
                    m_DedicatedAllocations.Clear();
                }
            }

            for (int i = 0; i < m_pBlockVectors.Length; i++)
            {
                m_pBlockVectors[i]?.Destroy();
                m_pBlockVectors[i] = null;
            }
        }

        // --- Internal accessors ---

        internal uint MemoryTypeCount => MemoryProperties.MemoryTypeCount;
        internal uint MemoryHeapCount => MemoryProperties.MemoryHeapCount;

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

        // --- Private allocation helpers ---

        private Result AllocateMemoryInternal(
            in MemoryRequirements vkMemReq,
            VmaSuballocationType suballocType,
            in VmaAllocationCreateInfo createInfo,
            out VmaAllocation? allocation)
        {
            allocation = null;

            // Effective alignment: caller's MinAlignment hint vs. the
            // resource's required alignment; the larger wins.
            ulong alignment = Math.Max(vkMemReq.Alignment, createInfo.MinAlignment);

            // Pool path: skip type selection, use the pool's block vector.
            // DedicatedMemoryBit is invalid here per VMA's contract; ignored.
            if (createInfo.Pool != null)
            {
                Result pr = createInfo.Pool.BlockVector.AllocatePage(
                    vkMemReq.Size, alignment,
                    createInfo.Flags, suballocType, out allocation);
                if (pr != Result.Success)
                    return pr;
                return FinishAllocation(allocation!, in createInfo);
            }

            // Default path: select memory type first.
            Result r = FindMemoryTypeIndex(vkMemReq.MemoryTypeBits, in createInfo,
                out uint memTypeIndex);
            if (r != Result.Success)
                return r;

            // Dedicated path bypasses the block vector entirely.
            if ((createInfo.Flags & VmaAllocationCreateFlags.DedicatedMemoryBit) != 0)
            {
                r = AllocateDedicatedMemory(memTypeIndex, vkMemReq.Size,
                    alignment, createInfo.Priority, out allocation);
                if (r != Result.Success)
                    return r;
                return FinishAllocation(allocation!, in createInfo);
            }

            var blockVector = GetDefaultBlockVector(memTypeIndex);
            if (blockVector == null)
                return Result.ErrorInitializationFailed;

            r = blockVector.AllocatePage(
                vkMemReq.Size, alignment,
                createInfo.Flags, suballocType, out allocation);
            if (r != Result.Success)
                return r;

            return FinishAllocation(allocation!, in createInfo);
        }

        private unsafe Result AllocateDedicatedMemory(
            uint memoryTypeIndex,
            ulong size,
            ulong alignment,
            float priority,
            out VmaAllocation? allocation)
        {
            allocation = null;
            if (size == 0)
                return Result.ErrorInitializationFailed;

            if (!TryReserveHeapBytes(memoryTypeIndex, size))
                return Result.ErrorOutOfDeviceMemory;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            // Build pNext chain: MemoryAllocateFlagsInfo (device address) and
            // MemoryPriorityAllocateInfoEXT, with priority at the head.
            nint pNextChain = 0;

            MemoryAllocateFlagsInfo flagsInfo = default;
            if ((Flags & VmaAllocatorCreateFlags.BufferDeviceAddressBit) != 0)
            {
                flagsInfo = new MemoryAllocateFlagsInfo
                {
                    SType  = StructureType.MemoryAllocateFlagsInfo,
                    PNext  = (void*)pNextChain,
                    Flags  = MemoryAllocateFlags.AddressBit,
                };
                pNextChain = (nint)(&flagsInfo);
            }

            MemoryPriorityAllocateInfoEXT priorityInfo = default;
            if ((Flags & VmaAllocatorCreateFlags.ExtMemoryPriorityBit) != 0)
            {
                priorityInfo = new MemoryPriorityAllocateInfoEXT
                {
                    SType    = StructureType.MemoryPriorityAllocateInfoExt,
                    PNext    = (void*)pNextChain,
                    Priority = priority,
                };
                pNextChain = (nint)(&priorityInfo);
            }

            var allocInfo = new MemoryAllocateInfo
            {
                SType           = StructureType.MemoryAllocateInfo,
                PNext           = (void*)pNextChain,
                AllocationSize  = size,
                MemoryTypeIndex = memoryTypeIndex,
            };

            Result r = VkFunctions.AllocateMemory(
                Device, in allocInfo, pAc, out DeviceMemory memory);
            if (r != Result.Success)
            {
                ReleaseHeapBytes(memoryTypeIndex, size);
                return r;
            }

            NotifyDeviceMemoryAllocated(memoryTypeIndex, memory, size);

            allocation = VmaAllocation.CreateDedicatedAllocation(
                memory, size, alignment, memoryTypeIndex);

            lock (m_DedicatedMutex)
                m_DedicatedAllocations.Add(allocation);

            return Result.Success;
        }

        private unsafe void FreeDedicatedMemory(VmaAllocation allocation)
        {
            lock (m_DedicatedMutex)
                m_DedicatedAllocations.Remove(allocation);

            NotifyDeviceMemoryFreed(allocation.MemoryTypeIndex, allocation.DedicatedMemory, allocation.Size);
            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;
            VkFunctions.FreeMemory(Device, allocation.DedicatedMemory, pAc);
            ReleaseHeapBytes(allocation.MemoryTypeIndex, allocation.Size);
        }

        // ── Heap-size-limit accounting ───────────────────────────────────────

        /// <summary>
        /// Attempt to reserve <paramref name="size"/> bytes against the heap
        /// backing memory type <paramref name="memoryTypeIndex"/>. Returns
        /// false (without modifying the counter) if the reservation would
        /// exceed the effective heap size limit. Used by
        /// <see cref="VmaBlockVector"/> and the dedicated-allocation path.
        /// </summary>
        internal bool TryReserveHeapBytes(uint memoryTypeIndex, ulong size)
        {
            uint heapIndex = GetMemoryType(memoryTypeIndex).HeapIndex;
            long after = Interlocked.Add(ref m_HeapBytes[heapIndex], (long)size);
            if ((ulong)after > m_HeapSizeLimit[heapIndex])
            {
                Interlocked.Add(ref m_HeapBytes[heapIndex], -(long)size);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Release a previously reserved <paramref name="size"/> bytes from the
        /// heap counter for <paramref name="memoryTypeIndex"/>'s heap.
        /// </summary>
        internal void ReleaseHeapBytes(uint memoryTypeIndex, ulong size)
        {
            uint heapIndex = GetMemoryType(memoryTypeIndex).HeapIndex;
            Interlocked.Add(ref m_HeapBytes[heapIndex], -(long)size);
        }

        internal int DedicatedAllocationCount
        { get { lock (m_DedicatedMutex) return m_DedicatedAllocations.Count; } }

        /// <summary>
        /// Fires the <see cref="VmaDeviceMemoryCallbacks.PfnAllocate"/> callback
        /// (if registered) after a successful <c>vkAllocateMemory</c>. Called by
        /// <see cref="VmaBlockVector"/> and the dedicated-allocation path.
        /// </summary>
        internal void NotifyDeviceMemoryAllocated(uint memTypeIndex, DeviceMemory memory, ulong size)
            => m_DeviceMemoryCallbacks?.PfnAllocate?.Invoke(
                this, memTypeIndex, memory, size, m_DeviceMemoryCallbacks.UserData);

        /// <summary>
        /// Fires the <see cref="VmaDeviceMemoryCallbacks.PfnFree"/> callback
        /// (if registered) just before <c>vkFreeMemory</c>. Called by
        /// <see cref="VmaBlockVector"/> and the dedicated-free path.
        /// </summary>
        internal void NotifyDeviceMemoryFreed(uint memTypeIndex, DeviceMemory memory, ulong size)
            => m_DeviceMemoryCallbacks?.PfnFree?.Invoke(
                this, memTypeIndex, memory, size, m_DeviceMemoryCallbacks.UserData);

        // ── Phase 16: aliasing resources ─────────────────────────────────────

        /// <summary>
        /// Creates a <c>VkBuffer</c> bound to <paramref name="allocation"/>'s
        /// memory at its current offset. The buffer aliases the allocation's
        /// memory and must be destroyed by the caller; the allocation is not
        /// affected. Equivalent to <c>vmaCreateAliasingBuffer</c>.
        /// </summary>
        public unsafe Result CreateAliasingBuffer(
            VmaAllocation allocation,
            in BufferCreateInfo bufferCreateInfo,
            out Silk.NET.Vulkan.Buffer buffer)
        {
            RequireNotDisposed();
            buffer = default;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateBuffer(Device, in bufferCreateInfo, pAc, out buffer);
            if (r != Result.Success)
                return r;

            r = BindBufferMemory(allocation, buffer);
            if (r != Result.Success)
            {
                VkFunctions.DestroyBuffer(Device, buffer, pAc);
                buffer = default;
            }
            return r;
        }

        /// <summary>
        /// Creates a <c>VkBuffer</c> bound to <paramref name="allocation"/>'s
        /// memory at <c>allocation.Offset + allocationLocalOffset</c>, using
        /// <c>vkBindBufferMemory2</c>. Equivalent to <c>vmaCreateAliasingBuffer2</c>.
        /// </summary>
        public unsafe Result CreateAliasingBuffer2(
            VmaAllocation allocation,
            ulong allocationLocalOffset,
            in BufferCreateInfo bufferCreateInfo,
            out Silk.NET.Vulkan.Buffer buffer)
        {
            RequireNotDisposed();
            buffer = default;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateBuffer(Device, in bufferCreateInfo, pAc, out buffer);
            if (r != Result.Success)
                return r;

            r = BindBufferMemory2(allocation, allocationLocalOffset, buffer, null);
            if (r != Result.Success)
            {
                VkFunctions.DestroyBuffer(Device, buffer, pAc);
                buffer = default;
            }
            return r;
        }

        /// <summary>
        /// Creates a <c>VkImage</c> bound to <paramref name="allocation"/>'s
        /// memory at its current offset. The image aliases the allocation's
        /// memory and must be destroyed by the caller; the allocation is not
        /// affected. Equivalent to <c>vmaCreateAliasingImage</c>.
        /// </summary>
        public unsafe Result CreateAliasingImage(
            VmaAllocation allocation,
            in ImageCreateInfo imageCreateInfo,
            out Image image)
        {
            RequireNotDisposed();
            image = default;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateImage(Device, in imageCreateInfo, pAc, out image);
            if (r != Result.Success)
                return r;

            r = BindImageMemory(allocation, image);
            if (r != Result.Success)
            {
                VkFunctions.DestroyImage(Device, image, pAc);
                image = default;
            }
            return r;
        }

        /// <summary>
        /// Creates a <c>VkImage</c> bound to <paramref name="allocation"/>'s
        /// memory at <c>allocation.Offset + allocationLocalOffset</c>, using
        /// <c>vkBindImageMemory2</c>. Equivalent to <c>vmaCreateAliasingImage2</c>.
        /// </summary>
        public unsafe Result CreateAliasingImage2(
            VmaAllocation allocation,
            ulong allocationLocalOffset,
            in ImageCreateInfo imageCreateInfo,
            out Image image)
        {
            RequireNotDisposed();
            image = default;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            Result r = VkFunctions.CreateImage(Device, in imageCreateInfo, pAc, out image);
            if (r != Result.Success)
                return r;

            r = BindImageMemory2(allocation, allocationLocalOffset, image, null);
            if (r != Result.Success)
            {
                VkFunctions.DestroyImage(Device, image, pAc);
                image = default;
            }
            return r;
        }

        private unsafe Result FinishAllocation(
            VmaAllocation allocation,
            in VmaAllocationCreateInfo createInfo)
        {
            if (createInfo.UserData != null)
                allocation.UserData = createInfo.UserData;

            if ((createInfo.Flags & VmaAllocationCreateFlags.MappedBit) != 0)
            {
                Result r = MapMemory(allocation, out _);
                if (r != Result.Success)
                {
                    FreeMemory(allocation);
                    return r;
                }
            }
            return Result.Success;
        }

        // Maps VmaMemoryUsage (plus host-access flags) to Vulkan required/preferred
        // property flags. Mirrors vma_usage_to_required_preferred_flags in C++ VMA.
        private static void UsageToFlags(
            in VmaAllocationCreateInfo createInfo,
            out MemoryPropertyFlags required,
            out MemoryPropertyFlags preferred)
        {
            required  = MemoryPropertyFlags.None;
            preferred = MemoryPropertyFlags.None;

            bool hostSeqWrite = (createInfo.Flags & VmaAllocationCreateFlags.HostAccessSequentialWriteBit) != 0;
            bool hostRandom   = (createInfo.Flags & VmaAllocationCreateFlags.HostAccessRandomBit)          != 0;

            switch (createInfo.Usage)
            {
                case VmaMemoryUsage.GpuOnly:
                    preferred |= MemoryPropertyFlags.DeviceLocalBit;
                    break;

                case VmaMemoryUsage.CpuOnly:
                    required |= MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                    break;

                case VmaMemoryUsage.CpuToGpu:
                    required  |= MemoryPropertyFlags.HostVisibleBit;
                    preferred |= MemoryPropertyFlags.DeviceLocalBit;
                    break;

                case VmaMemoryUsage.GpuToCpu:
                    required  |= MemoryPropertyFlags.HostVisibleBit;
                    preferred |= MemoryPropertyFlags.HostCachedBit;
                    break;

                case VmaMemoryUsage.CpuCopy:
                    required |= MemoryPropertyFlags.HostVisibleBit;
                    break;

                case VmaMemoryUsage.GpuLazilyAllocated:
                    required |= MemoryPropertyFlags.LazilyAllocatedBit;
                    break;

                case VmaMemoryUsage.Auto:
                case VmaMemoryUsage.AutoPreferDevice:
                case VmaMemoryUsage.AutoPreferHost:
                    if (hostSeqWrite || hostRandom)
                    {
                        required |= MemoryPropertyFlags.HostVisibleBit;
                        preferred |= hostRandom
                            ? MemoryPropertyFlags.HostCachedBit
                            : MemoryPropertyFlags.HostCoherentBit;
                    }
                    if (createInfo.Usage == VmaMemoryUsage.AutoPreferDevice)
                        preferred |= MemoryPropertyFlags.DeviceLocalBit;
                    else if (createInfo.Usage == VmaMemoryUsage.AutoPreferHost)
                        preferred |= MemoryPropertyFlags.HostVisibleBit;
                    else if (!hostSeqWrite && !hostRandom)
                        preferred |= MemoryPropertyFlags.DeviceLocalBit;
                    break;
            }
        }

        private static int CountBits(uint v)
        {
            int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
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
