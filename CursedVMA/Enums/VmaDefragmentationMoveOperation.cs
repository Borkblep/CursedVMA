// Mirrors VmaDefragmentationMoveOperation from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Caller's response to a single proposed defragmentation move; written into
    /// <see cref="VmaDefragmentationMove.Operation"/> before passing the move
    /// array back to VMA via VmaAllocator.EndDefragmentationPass.
    /// </summary>
    public enum VmaDefragmentationMoveOperation : uint
    {
        /// <summary>Caller has copied the data from src to dst; VMA will swap the
        /// allocation to point at the new location.</summary>
        Copy = 0,

        /// <summary>Caller could not perform this move this pass; leave the allocation alone.</summary>
        Ignore = 1,

        /// <summary>Caller wants to discard this allocation entirely.</summary>
        Destroy = 2,
    }
}
