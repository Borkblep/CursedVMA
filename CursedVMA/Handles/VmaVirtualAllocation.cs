// C# port of VmaVirtualAllocation from vk_mem_alloc.h. Unlike the other VMA
// handle types (which are opaque pointers), VmaVirtualAllocation is a 64-bit
// value type in the C API, so it is represented here as a readonly struct.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Handle to a single allocation within a <see cref="VmaVirtualBlock"/>. Unlike
    /// <see cref="VmaAllocation"/>, this is a value type wrapping a 64-bit handle,
    /// matching the C API which types VmaVirtualAllocation as <c>uint64_t</c>.
    /// </summary>
    public readonly struct VmaVirtualAllocation : IEquatable<VmaVirtualAllocation>
    {
        /// <summary>Raw 64-bit handle value.</summary>
        public ulong Handle { get; }

        internal VmaVirtualAllocation(ulong handle)
        {
            Handle = handle;
        }

        /// <summary>The null handle (equivalent to VK_NULL_HANDLE in the C API).</summary>
        public static VmaVirtualAllocation Null => default;

        /// <summary>Returns true if this handle is the null handle.</summary>
        public bool IsNull => Handle == 0;

        /// <inheritdoc/>
        public bool Equals(VmaVirtualAllocation other) => Handle == other.Handle;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is VmaVirtualAllocation other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => Handle.GetHashCode();

        /// <summary>Equality operator.</summary>
        public static bool operator ==(VmaVirtualAllocation left, VmaVirtualAllocation right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(VmaVirtualAllocation left, VmaVirtualAllocation right) => !left.Equals(right);
    }
}
