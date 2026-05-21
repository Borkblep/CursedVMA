// Mirrors the small free-function math helpers in vk_mem_alloc.cpp
// (VmaAlignUp, VmaAlignDown, VmaIsPow2, VmaBlocksOnSamePage,
// VmaIsBufferImageGranularityConflict, ...). Grouped into a single
// internal static class so call sites read e.g. VmaMath.AlignUp(value, a)
// which matches the C++ VmaAlignUp(value, a) up to a "." instead of nothing.
//
// All methods are aggressively inlined and use only intrinsics that
// NativeAOT can compile to direct CPU instructions (BitOperations.*).

using System.Numerics;
using System.Runtime.CompilerServices;

namespace CursedVMA.Internal
{
    internal static class VmaMath
    {
        /// <summary>
        /// Rounds <paramref name="value"/> up to the next multiple of
        /// <paramref name="alignment"/>. Equivalent to C++ <c>VmaAlignUp</c>.
        /// </summary>
        /// <remarks>
        /// <paramref name="alignment"/> must be a power of two. Passing 0 is
        /// undefined behavior (the C++ helper has the same precondition).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        /// <summary>
        /// Rounds <paramref name="value"/> down to the previous multiple of
        /// <paramref name="alignment"/>. Equivalent to C++ <c>VmaAlignDown</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignDown(ulong value, ulong alignment)
        {
            return value & ~(alignment - 1);
        }

        /// <summary>
        /// Integer division of <paramref name="x"/> by <paramref name="y"/>
        /// rounding the result up. Equivalent to C++ <c>VmaDivideRoundingUp</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong DivideRoundingUp(ulong x, ulong y)
        {
            return (x + y - 1) / y;
        }

        /// <summary>
        /// True when <paramref name="x"/> is a power of two and non-zero.
        /// Equivalent to C++ <c>VmaIsPow2</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPow2(ulong x)
        {
            return x != 0 && (x & (x - 1)) == 0;
        }

        /// <summary>
        /// Smallest power of two greater than or equal to <paramref name="value"/>.
        /// Returns 1 for inputs of 0 or 1. Equivalent to C++ <c>VmaNextPow2</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong NextPow2(ulong value)
        {
            return value <= 1 ? 1 : BitOperations.RoundUpToPowerOf2(value);
        }

        /// <summary>
        /// Largest power of two less than or equal to <paramref name="value"/>.
        /// Returns 0 for an input of 0. Equivalent to C++ <c>VmaPrevPow2</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PrevPow2(ulong value)
        {
            if (value == 0)
            {
                return 0;
            }

            int msb = 63 - BitOperations.LeadingZeroCount(value);
            return 1ul << msb;
        }

        /// <summary>
        /// Index of the lowest set bit (LSB) in <paramref name="mask"/>, or
        /// -1 if no bits are set. Equivalent to C++ <c>VmaBitScanLSB</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitScanLSB(uint mask)
        {
            return mask == 0u ? -1 : BitOperations.TrailingZeroCount(mask);
        }

        /// <summary>
        /// Index of the highest set bit (MSB) in <paramref name="mask"/>, or
        /// -1 if no bits are set. Equivalent to C++ <c>VmaBitScanMSB</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitScanMSB(uint mask)
        {
            return mask == 0u ? -1 : 31 - BitOperations.LeadingZeroCount(mask);
        }

        /// <summary>
        /// 64-bit overload of <see cref="BitScanLSB(uint)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitScanLSB(ulong mask)
        {
            return mask == 0ul ? -1 : BitOperations.TrailingZeroCount(mask);
        }

        /// <summary>
        /// 64-bit overload of <see cref="BitScanMSB(uint)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitScanMSB(ulong mask)
        {
            return mask == 0ul ? -1 : 63 - BitOperations.LeadingZeroCount(mask);
        }

        /// <summary>
        /// Returns true if the byte at <paramref name="resourceAOffset"/> +
        /// <paramref name="resourceASize"/> - 1 (the last byte of resource A)
        /// and the byte at <paramref name="resourceBOffset"/> (the first byte
        /// of resource B) fall within the same <paramref name="pageSize"/>-aligned
        /// page. Used to decide whether buffer-image-granularity padding is
        /// required between adjacent resources. Equivalent to C++
        /// <c>VmaBlocksOnSamePage</c>.
        /// </summary>
        /// <remarks>
        /// <paramref name="pageSize"/> must be a power of two.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool BlocksOnSamePage(
            ulong resourceAOffset,
            ulong resourceASize,
            ulong resourceBOffset,
            ulong pageSize)
        {
            ulong resourceAEnd = resourceAOffset + resourceASize - 1;
            ulong resourceAEndPage = resourceAEnd & ~(pageSize - 1);
            ulong resourceBStartPage = resourceBOffset & ~(pageSize - 1);
            return resourceAEndPage == resourceBStartPage;
        }

        /// <summary>
        /// Returns true when two suballocation types cannot share a Vulkan
        /// buffer-image-granularity page without padding between them.
        /// Equivalent to C++ <c>VmaIsBufferImageGranularityConflict</c>.
        /// </summary>
        public static bool IsBufferImageGranularityConflict(
            VmaSuballocationType type1,
            VmaSuballocationType type2)
        {
            if (type1 > type2)
            {
                (type1, type2) = (type2, type1);
            }

            switch (type1)
            {
                case VmaSuballocationType.Free:
                    return false;
                case VmaSuballocationType.Unknown:
                    return true;
                case VmaSuballocationType.Buffer:
                    return type2 == VmaSuballocationType.ImageUnknown
                        || type2 == VmaSuballocationType.ImageOptimal;
                case VmaSuballocationType.ImageUnknown:
                    return type2 == VmaSuballocationType.ImageUnknown
                        || type2 == VmaSuballocationType.ImageLinear
                        || type2 == VmaSuballocationType.ImageOptimal;
                case VmaSuballocationType.ImageLinear:
                    return type2 == VmaSuballocationType.ImageOptimal;
                case VmaSuballocationType.ImageOptimal:
                    return false;
                default:
                    return false;
            }
        }
    }
}
