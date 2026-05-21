// Ports VmaVirtualBlock_T from vk_mem_alloc.cpp. A self-contained suballocation
// engine over an abstract address space; no Vulkan dispatch is involved, just
// a VmaBlockMetadata (Linear or TLSF) selected by the create-info flags.
//
// The C API exposes ten free-functions (vmaCreateVirtualBlock, vmaVirtualAllocate,
// vmaVirtualFree, …). In this managed port they are presented as instance
// methods on the class, with a static Create factory in place of vmaCreate*.

using CursedVMA.Internal;
using CursedVMA.Internal.Algorithms;
using Silk.NET.Vulkan;
using System;

namespace CursedVMA
{
    /// <summary>
    /// Self-contained suballocation engine over an abstract address space, with
    /// no Vulkan dependency. Used for sub-dividing large GPU buffers or pre-built
    /// memory regions using the same TLSF or linear algorithms VMA uses for real
    /// device memory. Equivalent to the C++ <c>VmaVirtualBlock_T</c>.
    /// </summary>
    public sealed class VmaVirtualBlock : IDisposable
    {
        private VmaBlockMetadata? m_Metadata;

        private VmaVirtualBlock(VmaBlockMetadata metadata)
        {
            m_Metadata = metadata;
        }

        /// <summary>
        /// Creates a new virtual block. Equivalent to <c>vmaCreateVirtualBlock</c>.
        /// </summary>
        /// <param name="createInfo">Block parameters. <see cref="VmaVirtualBlockCreateInfo.Size"/>
        /// must be greater than zero.</param>
        /// <param name="virtualBlock">On success, the new block; otherwise <c>null</c>.</param>
        /// <returns><see cref="Result.Success"/> on success;
        /// <see cref="Result.ErrorInitializationFailed"/> if the parameters are invalid.</returns>
        public static Result Create(
            in VmaVirtualBlockCreateInfo createInfo,
            out VmaVirtualBlock? virtualBlock)
        {
            if (createInfo.Size == 0)
            {
                virtualBlock = null;
                return Result.ErrorInitializationFailed;
            }

            bool linear = (createInfo.Flags & VmaVirtualBlockCreateFlags.LinearAlgorithmBit) != 0;
            VmaBlockMetadata metadata = linear
                ? new VmaBlockMetadataLinear(bufferImageGranularity: 1, isVirtual: true)
                : new VmaBlockMetadataTlsf(bufferImageGranularity: 1, isVirtual: true);
            metadata.Init(createInfo.Size);

            virtualBlock = new VmaVirtualBlock(metadata);
            return Result.Success;
        }

        /// <summary>
        /// Releases the block's internal data structures. Equivalent to
        /// <c>vmaDestroyVirtualBlock</c>. Matching the C++ behavior, this does
        /// not throw when allocations are still live — the block is torn down
        /// either way. The C++ port asserts in debug builds; for parity, debug
        /// callers should check <see cref="IsEmpty"/> first.
        /// </summary>
        public void Dispose()
        {
            if (m_Metadata == null) return;
            System.Diagnostics.Debug.Assert(m_Metadata.IsEmpty(),
                "VmaVirtualBlock disposed with live allocations.");
            m_Metadata = null;
        }

        /// <summary>
        /// Returns true when the block holds no live allocations. Equivalent to
        /// <c>vmaIsVirtualBlockEmpty</c>.
        /// </summary>
        public bool IsEmpty()
        {
            return RequireMetadata().IsEmpty();
        }

        /// <summary>
        /// Fills <paramref name="info"/> with the offset, size, and user data of a
        /// previously-returned allocation. Equivalent to <c>vmaGetVirtualAllocationInfo</c>.
        /// </summary>
        public void GetAllocationInfo(
            VmaVirtualAllocation allocation,
            out VmaVirtualAllocationInfo info)
        {
            RequireMetadata().GetAllocationInfo(allocation.Handle, out info);
        }

        /// <summary>
        /// Attempts to carve a new allocation out of the block. Equivalent to
        /// <c>vmaVirtualAllocate</c>.
        /// </summary>
        /// <param name="createInfo">Allocation parameters; <see cref="VmaVirtualAllocationCreateInfo.Size"/>
        /// must be greater than zero and <see cref="VmaVirtualAllocationCreateInfo.Alignment"/>
        /// must be a power of two or zero.</param>
        /// <param name="allocation">On success, the allocation handle.</param>
        /// <param name="offset">On success, the allocation's byte offset within the block.</param>
        /// <returns><see cref="Result.Success"/> on success;
        /// <see cref="Result.ErrorOutOfDeviceMemory"/> if the block has no room.</returns>
        public Result Allocate(
            in VmaVirtualAllocationCreateInfo createInfo,
            out VmaVirtualAllocation allocation,
            out ulong offset)
        {
            var metadata = RequireMetadata();

            if (createInfo.Size == 0)
            {
                allocation = VmaVirtualAllocation.Null;
                offset = 0;
                return Result.ErrorInitializationFailed;
            }

            ulong alignment = createInfo.Alignment == 0 ? 1 : createInfo.Alignment;
            if ((alignment & (alignment - 1)) != 0)
            {
                allocation = VmaVirtualAllocation.Null;
                offset = 0;
                return Result.ErrorInitializationFailed;
            }

            bool upperAddress =
                (createInfo.Flags & VmaVirtualAllocationCreateFlags.UpperAddressBit) != 0;
            uint strategy = (uint)(createInfo.Flags & VmaVirtualAllocationCreateFlags.StrategyMask);

            if (!metadata.CreateAllocationRequest(
                    createInfo.Size, alignment, upperAddress,
                    VmaSuballocationType.Unknown, strategy, out var request))
            {
                allocation = VmaVirtualAllocation.Null;
                offset = 0;
                return Result.ErrorOutOfDeviceMemory;
            }

            metadata.Alloc(request, VmaSuballocationType.Unknown, createInfo.UserData);

            allocation = new VmaVirtualAllocation(request.AllocHandle);
            offset = metadata.GetAllocationOffset(request.AllocHandle);
            return Result.Success;
        }

        /// <summary>
        /// Releases a previously-returned allocation. Passing
        /// <see cref="VmaVirtualAllocation.Null"/> is a no-op. Equivalent to
        /// <c>vmaVirtualFree</c>.
        /// </summary>
        public void Free(VmaVirtualAllocation allocation)
        {
            if (allocation.IsNull) return;
            RequireMetadata().Free(allocation.Handle);
        }

        /// <summary>
        /// Releases every allocation in the block in one call. Equivalent to
        /// <c>vmaClearVirtualBlock</c>.
        /// </summary>
        public void Clear()
        {
            RequireMetadata().Clear();
        }

        /// <summary>
        /// Replaces the user data attached to an allocation. Equivalent to
        /// <c>vmaSetVirtualAllocationUserData</c>.
        /// </summary>
        public void SetAllocationUserData(VmaVirtualAllocation allocation, object? userData)
        {
            RequireMetadata().SetAllocationUserData(allocation.Handle, userData);
        }

        /// <summary>
        /// Fills <paramref name="stats"/> with cheap block-level counters.
        /// Equivalent to <c>vmaGetVirtualBlockStatistics</c>.
        /// </summary>
        public void GetStatistics(out VmaStatistics stats)
        {
            stats = default;
            RequireMetadata().AddStatistics(ref stats);
        }

        /// <summary>
        /// Fills <paramref name="stats"/> with detailed counters including
        /// unused-range size extrema. Equivalent to
        /// <c>vmaCalculateVirtualBlockStatistics</c>.
        /// </summary>
        public void CalculateStatistics(out VmaDetailedStatistics stats)
        {
            stats = default;
            RequireMetadata().AddDetailedStatistics(ref stats);
        }

        private VmaBlockMetadata RequireMetadata()
        {
            return m_Metadata ?? throw new ObjectDisposedException(nameof(VmaVirtualBlock));
        }
    }
}
