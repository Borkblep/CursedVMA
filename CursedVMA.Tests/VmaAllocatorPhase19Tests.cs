// Phase 19 tests: debug margins and CheckCorruption / CheckPoolCorruption.
//
// FakeVulkanFunctions.MapMemory always returns the same pinned 4096-byte
// buffer, so WriteMagicValues and CheckCorruption can round-trip through it
// without a real GPU.

using CursedVMA.Internal.Algorithms;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using Xunit;

namespace CursedVMA.Tests
{
    // ── Shared fixture ────────────────────────────────────────────────────────

    internal sealed class Phase19Fixture
    {
        public FakeVulkanFunctions Fake { get; }
        public VmaAllocator Allocator { get; }
        public PhysicalDevice Pd { get; }
        public Device Dev { get; }
        public ulong Margin { get; }

        public Phase19Fixture(ulong margin = 16)
        {
            Pd     = new PhysicalDevice(1);
            Dev    = new Device(2);
            Margin = margin;

            Fake = new FakeVulkanFunctions
            {
                MemoryProperties         = BuildMemProps(),
                BufferMemoryRequirements = new MemoryRequirements
                {
                    Size           = 256,
                    Alignment      = 1,
                    MemoryTypeBits = 0b1,
                },
            };

            var createInfo = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = Pd,
                Device         = Dev,
                DebugMargin    = margin,
            };
            VmaAllocator.Create(Fake, in createInfo, out var alloc);
            Allocator = alloc!;
        }

        private static PhysicalDeviceMemoryProperties BuildMemProps()
        {
            var mp = new PhysicalDeviceMemoryProperties
            {
                MemoryTypeCount = 1,
                MemoryHeapCount = 1,
            };
            mp.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex     = 0,
            };
            mp.MemoryHeaps.Element0 = new MemoryHeap
            {
                Size  = 256ul * 1024 * 1024,
                Flags = MemoryHeapFlags.None,
            };
            return mp;
        }
    }

    // ── No-margin baseline ───────────────────────────────────────────────────

    public sealed class VmaCheckCorruptionNoMarginTests
    {
        [Fact]
        public void CheckCorruption_ReturnsFeatureNotPresent_WhenNoMargin()
        {
            var f = new Phase19Fixture(margin: 0);
            var r = f.Allocator.CheckCorruption(0b1);
            Assert.Equal(Result.ErrorFeatureNotPresent, r);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CheckPoolCorruption_ReturnsFeatureNotPresent_WhenNoMargin()
        {
            var f = new Phase19Fixture(margin: 0);
            var ci = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            f.Allocator.CreatePool(in ci, out var pool);
            var r = f.Allocator.CheckPoolCorruption(pool!);
            Assert.Equal(Result.ErrorFeatureNotPresent, r);
            f.Allocator.DestroyPool(pool!);
            f.Allocator.Dispose();
        }
    }

    // ── Clean-margin checks ───────────────────────────────────────────────────

    public sealed class VmaCheckCorruptionCleanTests
    {
        private static VmaAllocation Alloc(Phase19Fixture f)
        {
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);
            return alloc!;
        }

        [Fact]
        public void CheckCorruption_ReturnsSuccess_WhenMagicIntact()
        {
            var f  = new Phase19Fixture();
            var a  = Alloc(f);
            var r  = f.Allocator.CheckCorruption(0b1);

            // Dedicated allocations bypass block vectors. The block vector has
            // margin > 0 but no blocks, so CheckCorruption returns Success
            // (no blocks to check = no corruption found).
            Assert.Equal(Result.Success, r);

            f.Allocator.FreeMemory(a);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CheckCorruption_BlockAlloc_ReturnsSuccess_WhenMagicIntact()
        {
            var f = new Phase19Fixture();
            // Force a block (non-dedicated) allocation.
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.None };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            var r = f.Allocator.CheckCorruption(0b1);
            Assert.Equal(Result.Success, r);

            f.Allocator.FreeMemory(alloc!);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CheckPoolCorruption_ReturnsSuccess_WhenMagicIntact()
        {
            var f  = new Phase19Fixture();
            var poolCi = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            f.Allocator.CreatePool(in poolCi, out var pool);

            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags          = VmaAllocationCreateFlags.None,
                Pool = pool,
            };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            var r = f.Allocator.CheckPoolCorruption(pool!);
            Assert.Equal(Result.Success, r);

            f.Allocator.FreeMemory(alloc!);
            f.Allocator.DestroyPool(pool!);
            f.Allocator.Dispose();
        }
    }

    // ── Corruption-detection checks ──────────────────────────────────────────

    public sealed unsafe class VmaCheckCorruptionDetectionTests
    {
        // Pinned buffer exposed by FakeVulkanFunctions.MapMemory.
        private static readonly byte[] s_FakeMem = FakeVulkanFunctions.s_MappedBuffer;

        [Fact]
        public void CheckCorruption_DetectsPreMarginCorruption()
        {
            var f = new Phase19Fixture(margin: 8);
            var req = new MemoryRequirements { Size = 32, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.None };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            // The physical offset is userOffset - margin = alloc.Offset - margin.
            ulong physOffset = alloc!.Offset - f.Margin;

            // Corrupt the first byte of the pre-margin.
            s_FakeMem[physOffset] = 0x00;

            var r = f.Allocator.CheckCorruption(0b1);
            Assert.Equal(Result.ErrorUnknown, r);

            // Restore and verify recovery.
            s_FakeMem[physOffset] = VmaBlockMetadata.DebugMagicByte;
            r = f.Allocator.CheckCorruption(0b1);
            Assert.Equal(Result.Success, r);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CheckCorruption_DetectsPostMarginCorruption()
        {
            var f = new Phase19Fixture(margin: 8);
            var req = new MemoryRequirements { Size = 32, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.None };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            // Post-margin starts at userOffset + userSize.
            ulong postStart = alloc!.Offset + 32;
            s_FakeMem[postStart] = 0x00;

            var r = f.Allocator.CheckCorruption(0b1);
            Assert.Equal(Result.ErrorUnknown, r);

            s_FakeMem[postStart] = VmaBlockMetadata.DebugMagicByte;
            r = f.Allocator.CheckCorruption(0b1);
            Assert.Equal(Result.Success, r);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void UserOffsetIsAfterPreMargin()
        {
            var f   = new Phase19Fixture(margin: 16);
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.None };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            // Physical block starts at 0; user data starts at margin.
            Assert.Equal(f.Margin, alloc!.Offset);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CheckPoolCorruption_DetectsCorruption()
        {
            var f      = new Phase19Fixture(margin: 8);
            var poolCi = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            f.Allocator.CreatePool(in poolCi, out var pool);

            var req = new MemoryRequirements { Size = 32, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Pool = pool };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            ulong physOffset = alloc!.Offset - f.Margin;
            s_FakeMem[physOffset] = 0x00;

            var r = f.Allocator.CheckPoolCorruption(pool!);
            Assert.Equal(Result.ErrorUnknown, r);

            s_FakeMem[physOffset] = VmaBlockMetadata.DebugMagicByte;
            r = f.Allocator.CheckPoolCorruption(pool!);
            Assert.Equal(Result.Success, r);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.DestroyPool(pool!);
            f.Allocator.Dispose();
        }
    }

    // ── Metadata magic-value unit tests ──────────────────────────────────────

    public sealed class VmaBlockMetadataLinearMarginTests
    {
        [Fact]
        public void LinearMetadata_CheckCorruption_ReturnsFeatureNotPresent_WhenNoMargin()
        {
            var meta = new VmaBlockMetadataLinear(bufferImageGranularity: 1, isVirtual: false, debugMargin: 0);
            meta.Init(4096);
            unsafe
            {
                byte[] buf = new byte[4096];
                fixed (byte* p = buf)
                    Assert.Equal(Result.ErrorFeatureNotPresent, meta.CheckCorruption(p));
            }
        }

        [Fact]
        public void LinearMetadata_CheckCorruption_EmptyBlock_ReturnsSuccess()
        {
            var meta = new VmaBlockMetadataLinear(bufferImageGranularity: 1, isVirtual: false, debugMargin: 8);
            meta.Init(4096);
            unsafe
            {
                byte[] buf = new byte[4096];
                fixed (byte* p = buf)
                    Assert.Equal(Result.Success, meta.CheckCorruption(p));
            }
        }
    }

    public sealed class VmaBlockMetadataTlsfMarginTests
    {
        [Fact]
        public void TlsfMetadata_CheckCorruption_ReturnsFeatureNotPresent_WhenNoMargin()
        {
            var meta = new VmaBlockMetadataTlsf(bufferImageGranularity: 1, isVirtual: false, debugMargin: 0);
            meta.Init(4096);
            unsafe
            {
                byte[] buf = new byte[4096];
                fixed (byte* p = buf)
                    Assert.Equal(Result.ErrorFeatureNotPresent, meta.CheckCorruption(p));
            }
        }

        [Fact]
        public void TlsfMetadata_CheckCorruption_EmptyBlock_ReturnsSuccess()
        {
            var meta = new VmaBlockMetadataTlsf(bufferImageGranularity: 1, isVirtual: false, debugMargin: 8);
            meta.Init(4096);
            unsafe
            {
                byte[] buf = new byte[4096];
                fixed (byte* p = buf)
                    Assert.Equal(Result.Success, meta.CheckCorruption(p));
            }
        }
    }
}
