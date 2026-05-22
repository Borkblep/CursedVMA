// Ports VmaAllocator_T from vk_mem_alloc.cpp. Phase 7: core lifecycle.
// Phase 8: CreatePool / DestroyPool. Phase 9: per-allocation API
// (AllocateMemory, FreeMemory, Map/Unmap, Bind*). Phase 10: dedicated
// allocations triggered by VmaAllocationCreateFlags.DedicatedMemoryBit.
// Phase 11: allocator-wide statistics (GetStatistics, CalculateStatistics)
// and heap budgets (GetHeapBudgets).

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

        // Dedicated allocations tracked for Dispose cleanup and statistics.
        private readonly List<VmaAllocation> m_DedicatedAllocations =
            new List<VmaAllocation>();
        private readonly object m_DedicatedMutex = new object();

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
                        VkFunctions.FreeMemory(Device, alloc.DedicatedMemory, pAc);
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

            // Pool path: skip type selection, use the pool's block vector.
            // DedicatedMemoryBit is invalid here per VMA's contract; ignored.
            if (createInfo.Pool != null)
            {
                Result pr = createInfo.Pool.BlockVector.AllocatePage(
                    vkMemReq.Size, vkMemReq.Alignment,
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
                    vkMemReq.Alignment, out allocation);
                if (r != Result.Success)
                    return r;
                return FinishAllocation(allocation!, in createInfo);
            }

            var blockVector = GetDefaultBlockVector(memTypeIndex);
            if (blockVector == null)
                return Result.ErrorInitializationFailed;

            r = blockVector.AllocatePage(
                vkMemReq.Size, vkMemReq.Alignment,
                createInfo.Flags, suballocType, out allocation);
            if (r != Result.Success)
                return r;

            return FinishAllocation(allocation!, in createInfo);
        }

        private unsafe Result AllocateDedicatedMemory(
            uint memoryTypeIndex,
            ulong size,
            ulong alignment,
            out VmaAllocation? allocation)
        {
            allocation = null;
            if (size == 0)
                return Result.ErrorInitializationFailed;

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;

            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = size,
                MemoryTypeIndex = memoryTypeIndex,
            };

            Result r = VkFunctions.AllocateMemory(
                Device, in allocInfo, pAc, out DeviceMemory memory);
            if (r != Result.Success)
                return r;

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

            AllocationCallbacks ac = AllocatorCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = AllocatorCallbacks.HasValue ? &ac : null;
            VkFunctions.FreeMemory(Device, allocation.DedicatedMemory, pAc);
        }

        internal int DedicatedAllocationCount
        { get { lock (m_DedicatedMutex) return m_DedicatedAllocations.Count; } }

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
