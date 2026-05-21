// Mirrors VmaAllocationRequest (and VmaAllocationRequestType) from
// vk_mem_alloc.cpp. A VmaAllocationRequest is produced by
// VmaBlockMetadata.CreateAllocationRequest and consumed by the matching
// Alloc call to commit the placement.

namespace CursedVMA.Internal
{
    /// <summary>
    /// Discriminator that tells the consumer how to interpret an
    /// <see cref="VmaAllocationRequest"/>. Different block algorithms produce
    /// different request shapes.
    /// </summary>
    internal enum VmaAllocationRequestType
    {
        /// <summary>Standard placement (TLSF or Linear lower-stack).</summary>
        Normal,

        /// <summary>TLSF-specific placement; carries TLSF block metadata in
        /// <see cref="VmaAllocationRequest.CustomData"/>.</summary>
        TLSF,

        /// <summary>Linear-algorithm upper-stack placement.</summary>
        UpperAddress,

        /// <summary>Linear-algorithm: append to the end of the first vector.</summary>
        EndOf1st,

        /// <summary>Linear-algorithm: append to the end of the second vector.</summary>
        EndOf2nd,
    }

    /// <summary>
    /// Description of a proposed allocation produced by
    /// <see cref="Algorithms.VmaBlockMetadata.CreateAllocationRequest"/>;
    /// passing it to <see cref="Algorithms.VmaBlockMetadata.Alloc"/> commits
    /// the placement.
    /// </summary>
    internal struct VmaAllocationRequest
    {
        /// <summary>Internal allocation handle that will be assigned by Alloc.</summary>
        public ulong AllocHandle;

        /// <summary>Size, in bytes, of the proposed allocation. May exceed the
        /// caller's requested size when alignment padding has been folded in.</summary>
        public ulong Size;

        /// <summary>Algorithm-specific scratch (e.g. TLSF block pointer cast to
        /// an object reference). Interpretation depends on <see cref="Type"/>.</summary>
        public object? CustomData;

        /// <summary>Algorithm-specific scratch (e.g. encoded list index).
        /// Interpretation depends on <see cref="Type"/>.</summary>
        public ulong AlgorithmData;

        /// <summary>Discriminator for the algorithm-specific fields.</summary>
        public VmaAllocationRequestType Type;
    }
}
