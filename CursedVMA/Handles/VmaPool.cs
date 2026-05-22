// Ports VmaPool_T from vk_mem_alloc.cpp. A pool pins allocations to a specific
// Vulkan memory type and may select a non-default block algorithm, minimum /
// maximum block count, and a custom block size. Internally owns one
// VmaBlockVector that manages the pool's VkDeviceMemory blocks.

using CursedVMA.Internal;
using System;

namespace CursedVMA
{
    /// <summary>
    /// Custom memory pool pinned to a specific Vulkan memory type. Equivalent
    /// to the C++ <c>VmaPool_T</c>. Obtain via
    /// <see cref="VmaAllocator.CreatePool"/>; release with
    /// <see cref="VmaAllocator.DestroyPool"/> or <see cref="Dispose"/>.
    /// </summary>
    public sealed class VmaPool : IDisposable
    {
        private VmaBlockVector m_BlockVector;
        private readonly uint m_Id;
        private string? m_Name;
        private bool m_IsDisposed;

        internal VmaPool(VmaBlockVector blockVector, uint id)
        {
            m_BlockVector = blockVector;
            m_Id = id;
        }

        internal VmaBlockVector BlockVector => m_BlockVector;
        internal uint Id => m_Id;

        /// <summary>Optional debug name; set via <see cref="SetName"/>.</summary>
        public string? Name => m_Name;

        /// <summary>
        /// Attaches a debug name to this pool. Equivalent to
        /// <c>vmaSetPoolName</c>. Thread-safe.
        /// </summary>
        public void SetName(string? name) => m_Name = name;

        /// <summary>
        /// Fills <paramref name="stats"/> with cheap block-level counters for
        /// this pool. Equivalent to <c>vmaGetPoolStatistics</c>.
        /// </summary>
        public void GetStatistics(out VmaStatistics stats)
        {
            RequireNotDisposed();
            stats = default;
            m_BlockVector.AddStatistics(ref stats);
        }

        /// <summary>
        /// Fills <paramref name="stats"/> with detailed counters including
        /// unused-range extrema. Equivalent to
        /// <c>vmaCalculatePoolStatistics</c>.
        /// </summary>
        public void CalculateStatistics(out VmaDetailedStatistics stats)
        {
            RequireNotDisposed();
            stats = default;
            m_BlockVector.AddDetailedStatistics(ref stats);
        }

        /// <summary>
        /// Releases all <c>VkDeviceMemory</c> blocks owned by this pool.
        /// Prefer <see cref="VmaAllocator.DestroyPool"/> which also
        /// unregisters the pool from the parent allocator. Safe to call more
        /// than once.
        /// </summary>
        public void Dispose()
        {
            if (m_IsDisposed) return;
            m_IsDisposed = true;
            m_BlockVector.Destroy();
        }

        private void RequireNotDisposed()
        {
            if (m_IsDisposed)
                throw new ObjectDisposedException(nameof(VmaPool));
        }
    }
}
