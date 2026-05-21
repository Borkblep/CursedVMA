// Mirrors VmaDefragmentationMove from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// A single proposed move in a defragmentation pass. The caller is expected
    /// to either copy the data from <see cref="SrcAllocation"/> to
    /// <see cref="DstTmpAllocation"/> and leave <see cref="Operation"/> as
    /// <see cref="VmaDefragmentationMoveOperation.Copy"/>, or set
    /// <see cref="Operation"/> to <see cref="VmaDefragmentationMoveOperation.Ignore"/>
    /// or <see cref="VmaDefragmentationMoveOperation.Destroy"/> as appropriate.
    /// </summary>
    public struct VmaDefragmentationMove
    {
        /// <summary>Caller's response to the move (see field comment).</summary>
        public VmaDefragmentationMoveOperation Operation;

        /// <summary>The allocation that currently holds the data.</summary>
        public VmaAllocation? SrcAllocation;

        /// <summary>Pre-reserved destination allocation owned by VMA; valid for
        /// this pass only. On <see cref="VmaDefragmentationMoveOperation.Copy"/>
        /// VMA swaps <see cref="SrcAllocation"/> over to this location after the
        /// pass ends.</summary>
        public VmaAllocation? DstTmpAllocation;
    }
}
