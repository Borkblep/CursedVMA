// Mirrors VmaDefragmentationPassMoveInfo from vk_mem_alloc.h. In the C API the
// move array is a raw pointer owned by VMA for the duration of the pass; the
// managed port surfaces it as a regular C# array (VMA still owns the buffer
// and reuses it across passes).

namespace CursedVMA
{
    /// <summary>
    /// Move list produced by <c>VmaAllocator.BeginDefragmentationPass</c>. The
    /// caller iterates the moves, updates each <see cref="VmaDefragmentationMove.Operation"/>
    /// as appropriate, and passes the same struct back to
    /// <c>VmaAllocator.EndDefragmentationPass</c>.
    /// </summary>
    public struct VmaDefragmentationPassMoveInfo
    {
        /// <summary>Number of valid entries at the front of <see cref="Moves"/>.</summary>
        public uint MoveCount;

        /// <summary>Buffer of proposed moves; owned by VMA. Only the first
        /// <see cref="MoveCount"/> entries are valid.</summary>
        public VmaDefragmentationMove[]? Moves;
    }
}
