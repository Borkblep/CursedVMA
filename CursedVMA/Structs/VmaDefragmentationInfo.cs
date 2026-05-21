// Mirrors VmaDefragmentationInfo (and PFN_vmaCheckDefragmentationBreakFunction)
// from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Optional break callback invoked by VMA between defragmentation candidates;
    /// returning <c>true</c> stops the pass early. Counterpart of
    /// <c>PFN_vmaCheckDefragmentationBreakFunction</c>.
    /// </summary>
    public delegate bool VmaCheckDefragmentationBreakDelegate(object? userData);

    /// <summary>
    /// Parameters for <c>VmaAllocator.BeginDefragmentation</c>.
    /// </summary>
    public struct VmaDefragmentationInfo
    {
        /// <summary>Combination of <see cref="VmaDefragmentationFlags"/>; selects
        /// the algorithm aggressiveness.</summary>
        public VmaDefragmentationFlags Flags;

        /// <summary>If non-null, limits defragmentation to a single custom pool;
        /// otherwise every default pool is considered.</summary>
        public VmaPool? Pool;

        /// <summary>Soft cap on bytes moved per pass; zero means no cap.</summary>
        public ulong MaxBytesPerPass;

        /// <summary>Soft cap on allocations moved per pass; zero means no cap.</summary>
        public uint MaxAllocationsPerPass;

        /// <summary>Optional callback called between candidate evaluations;
        /// returning <c>true</c> aborts the current pass.</summary>
        public VmaCheckDefragmentationBreakDelegate? PfnBreakCallback;

        /// <summary>Arbitrary user data forwarded to
        /// <see cref="PfnBreakCallback"/>.</summary>
        public object? BreakCallbackUserData;
    }
}
