// Phase 15 tests: MinAlignment, HeapSizeLimit, DontBindBit, and the
// FindMemoryTypeIndexForBuffer/ImageInfo entry-points.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    // ── shared fixture ───────────────────────────────────────────────────────

    internal static class Phase15Fixture
    {
        // One heap of <heapSize>, one HOST_VISIBLE | DEVICE_LOCAL memory type.
        internal static FakeVulkanFunctions MakeFvk(ulong heapSize = 64ul * 1024 * 1024)
        {
            var fvk = new FakeVulkanFunctions();
            fvk.MemoryProperties.MemoryHeapCount = 1;
            fvk.MemoryProperties.MemoryTypeCount = 1;
            unsafe
            {
                fvk.MemoryProperties.MemoryHeaps[0] = new MemoryHeap { Size = heapSize };
                fvk.MemoryProperties.MemoryTypes[0] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
            }
            return fvk;
        }

        internal static VmaAllocator MakeAllocator(
            FakeVulkanFunctions fvk, ulong[]? heapSizeLimit = null)
        {
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
                HeapSizeLimit  = heapSizeLimit,
            };
            VmaAllocator.Create(fvk, info, out var a);
            return a!;
        }
    }

    // ── MinAlignment ─────────────────────────────────────────────────────────

    public sealed class VmaAllocatorMinAlignmentTests
    {
        [Fact]
        public void MinAlignment_LargerThanResourceAlignment_AppliedToOffset()
        {
            // Pool with 1KB blocks. Two allocations of size 64 each:
            //   - first with no MinAlignment   → offset 0
            //   - second with MinAlignment=256 → offset must be 256, not 64
            var fvk = Phase15Fixture.MakeFvk();
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            allocator.CreatePool(
                new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = 1024 },
                out var pool);

            var memReq = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };

            allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out var a);
            allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool, MinAlignment = 256 },
                out var b);

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(0ul, a!.Offset);
            Assert.Equal(256ul, b!.Offset);

            allocator.FreeMemory(a);
            allocator.FreeMemory(b);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void MinAlignment_SmallerThanResourceAlignment_ResourceAlignmentWins()
        {
            // Resource alignment 128 dominates MinAlignment 1.
            var fvk = Phase15Fixture.MakeFvk();
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            allocator.CreatePool(
                new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = 1024 },
                out var pool);

            var memReq = new MemoryRequirements { Size = 64, Alignment = 128, MemoryTypeBits = 1 };

            allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out var a);
            allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool, MinAlignment = 1 }, out var b);

            Assert.Equal(0ul, a!.Offset);
            Assert.Equal(128ul, b!.Offset);   // aligned to 128, not packed

            allocator.FreeMemory(a);
            allocator.FreeMemory(b);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void MinAlignment_Zero_BehavesLikeBefore()
        {
            // Sanity: explicitly zero MinAlignment should match the unspecified case.
            var fvk = Phase15Fixture.MakeFvk();
            var allocator = Phase15Fixture.MakeAllocator(fvk);
            allocator.CreatePool(
                new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = 1024 },
                out var pool);

            var memReq = new MemoryRequirements { Size = 64, Alignment = 16, MemoryTypeBits = 1 };
            allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool, MinAlignment = 0 }, out var a);

            Assert.Equal(0ul, a!.Offset);

            allocator.FreeMemory(a);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }
    }

    // ── HeapSizeLimit ────────────────────────────────────────────────────────

    public sealed class VmaAllocatorHeapSizeLimitTests
    {
        [Fact]
        public void HeapSizeLimit_AllocationExceedsLimit_ReturnsOutOfDeviceMemory()
        {
            // Real heap is 64 MB; cap at 256 KB. Block size for pool is 512 KB.
            // First (and only) block create should exceed the cap and fail.
            var fvk = Phase15Fixture.MakeFvk();
            var allocator = Phase15Fixture.MakeAllocator(
                fvk, heapSizeLimit: new ulong[] { 256ul * 1024 });

            allocator.CreatePool(
                new VmaPoolCreateInfo
                {
                    MemoryTypeIndex = 0,
                    BlockSize       = 512ul * 1024,
                },
                out var pool);

            var memReq = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            Result r = allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out var a);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Null(a);

            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void HeapSizeLimit_FreeingBlockRestoresHeadroom()
        {
            // 4 KB cap; two 2 KB pool blocks can both fit, but a 3rd would overflow.
            var fvk = Phase15Fixture.MakeFvk();
            var allocator = Phase15Fixture.MakeAllocator(
                fvk, heapSizeLimit: new ulong[] { 4ul * 1024 });

            allocator.CreatePool(
                new VmaPoolCreateInfo
                {
                    MemoryTypeIndex = 0,
                    BlockSize       = 2ul * 1024,
                },
                out var pool);

            // Each AllocateMemory with size 2048/alignment 1 fills a block.
            var memReq = new MemoryRequirements { Size = 2048, Alignment = 1, MemoryTypeBits = 1 };

            allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out var a);
            allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out var b);
            // Third would need a 3rd block (2 KB) → 6 KB total > 4 KB cap.
            Result r = allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out var c);
            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Null(c);

            // Free a → its block is destroyed → heap headroom restored → c fits.
            allocator.FreeMemory(a);
            Result r2 = allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out c);
            Assert.Equal(Result.Success, r2);
            Assert.NotNull(c);

            allocator.FreeMemory(b);
            allocator.FreeMemory(c);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void HeapSizeLimit_DedicatedAllocationCounted()
        {
            // Cap 8 KB; a 6 KB dedicated allocation succeeds, a follow-up 4 KB fails.
            var fvk = Phase15Fixture.MakeFvk();
            var allocator = Phase15Fixture.MakeAllocator(
                fvk, heapSizeLimit: new ulong[] { 8ul * 1024 });

            var memReq = new MemoryRequirements { Size = 6 * 1024, Alignment = 1, MemoryTypeBits = 1 };
            Result r1 = allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo
                {
                    Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
                },
                out var a);
            Assert.Equal(Result.Success, r1);

            var memReq2 = new MemoryRequirements { Size = 4 * 1024, Alignment = 1, MemoryTypeBits = 1 };
            Result r2 = allocator.AllocateMemory(in memReq2,
                new VmaAllocationCreateInfo
                {
                    Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
                },
                out var b);
            Assert.Equal(Result.ErrorOutOfDeviceMemory, r2);

            allocator.FreeMemory(a);
            allocator.Dispose();
        }

        [Fact]
        public void HeapSizeLimit_ZeroEntry_TreatedAsNoLimit()
        {
            // Zero in the limit array means "no cap" (matches VMA convention).
            var fvk = Phase15Fixture.MakeFvk();
            var allocator = Phase15Fixture.MakeAllocator(
                fvk, heapSizeLimit: new ulong[] { 0 });

            allocator.CreatePool(
                new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = 4096 },
                out var pool);

            var memReq = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            Result r = allocator.AllocateMemory(in memReq,
                new VmaAllocationCreateInfo { Pool = pool }, out var a);
            Assert.Equal(Result.Success, r);

            allocator.FreeMemory(a);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }
    }

    // ── DontBindBit ──────────────────────────────────────────────────────────

    public sealed class VmaAllocatorDontBindBitTests
    {
        [Fact]
        public void CreateBuffer_DontBindBitSet_SkipsBindBufferMemory()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.BufferMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var bufInfo  = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 64 };
            var allocInfo = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DontBindBit,
            };

            Result r = allocator.CreateBuffer(in bufInfo, in allocInfo,
                out var buf, out var alloc, out _);

            Assert.Equal(Result.Success, r);
            Assert.NotEqual(0ul, buf.Handle);
            Assert.NotNull(alloc);
            Assert.Equal(0, fvk.BindBufferMemoryCallCount);   // skipped

            allocator.DestroyBuffer(buf, alloc);
            allocator.Dispose();
        }

        [Fact]
        public void CreateBuffer_DontBindBitNotSet_BindsBufferMemory()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.BufferMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var bufInfo   = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 64 };
            var allocInfo = new VmaAllocationCreateInfo();   // no DontBindBit

            allocator.CreateBuffer(in bufInfo, in allocInfo, out var buf, out var alloc, out _);

            Assert.Equal(1, fvk.BindBufferMemoryCallCount);

            allocator.DestroyBuffer(buf, alloc);
            allocator.Dispose();
        }

        [Fact]
        public void CreateImage_DontBindBitSet_SkipsBindImageMemory()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.ImageMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var imgInfo   = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            var allocInfo = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DontBindBit,
            };

            allocator.CreateImage(in imgInfo, in allocInfo, out var img, out var alloc, out _);
            Assert.Equal(0, fvk.BindImageMemoryCallCount);

            allocator.DestroyImage(img, alloc);
            allocator.Dispose();
        }

        [Fact]
        public void CreateImage_DontBindBitNotSet_BindsImageMemory()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.ImageMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var imgInfo   = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            var allocInfo = new VmaAllocationCreateInfo();

            allocator.CreateImage(in imgInfo, in allocInfo, out var img, out var alloc, out _);
            Assert.Equal(1, fvk.BindImageMemoryCallCount);

            allocator.DestroyImage(img, alloc);
            allocator.Dispose();
        }
    }

    // ── FindMemoryTypeIndexFor{Buffer,Image}Info ─────────────────────────────

    public sealed class VmaAllocatorFindMemoryTypeIndexForInfoTests
    {
        [Fact]
        public void FindMemoryTypeIndexForBufferInfo_CreatesAndDestroysProbeBuffer()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.BufferMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var bufInfo = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 64 };
            int createBefore  = fvk.CreateBufferCallCount;
            int destroyBefore = fvk.DestroyBufferCallCount;

            Result r = allocator.FindMemoryTypeIndexForBufferInfo(
                in bufInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx);
            // Exactly one probe buffer was created and destroyed.
            Assert.Equal(createBefore + 1,  fvk.CreateBufferCallCount);
            Assert.Equal(destroyBefore + 1, fvk.DestroyBufferCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void FindMemoryTypeIndexForImageInfo_CreatesAndDestroysProbeImage()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.ImageMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var imgInfo = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            int createBefore  = fvk.CreateImageCallCount;
            int destroyBefore = fvk.DestroyImageCallCount;

            Result r = allocator.FindMemoryTypeIndexForImageInfo(
                in imgInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx);
            Assert.Equal(createBefore + 1,  fvk.CreateImageCallCount);
            Assert.Equal(destroyBefore + 1, fvk.DestroyImageCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void FindMemoryTypeIndexForBufferInfo_CreateBufferFails_ReturnsError()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.CreateBufferResult = Result.ErrorOutOfHostMemory;
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var bufInfo = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 64 };
            Result r = allocator.FindMemoryTypeIndexForBufferInfo(
                in bufInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.ErrorOutOfHostMemory, r);
            Assert.Equal(uint.MaxValue, idx);

            allocator.Dispose();
        }

        [Fact]
        public void FindMemoryTypeIndexForImageInfo_CreateImageFails_ReturnsError()
        {
            var fvk = Phase15Fixture.MakeFvk();
            fvk.CreateImageResult = Result.ErrorOutOfHostMemory;
            var allocator = Phase15Fixture.MakeAllocator(fvk);

            var imgInfo = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            Result r = allocator.FindMemoryTypeIndexForImageInfo(
                in imgInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.ErrorOutOfHostMemory, r);
            Assert.Equal(uint.MaxValue, idx);

            allocator.Dispose();
        }
    }
}
