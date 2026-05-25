// Phase 20 tests: KhrDedicatedAllocationBit, WithinBudgetBit, CanAliasBit,
// and CreateBufferWithAlignment.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    // ── Shared fixture ────────────────────────────────────────────────────────

    internal sealed class Phase20Fixture
    {
        public FakeVulkanFunctions Fake { get; }
        public VmaAllocator Allocator { get; }
        public PhysicalDevice Pd { get; }
        public Device Dev { get; }

        public Phase20Fixture(VmaAllocatorCreateFlags allocatorFlags = VmaAllocatorCreateFlags.None)
        {
            Pd  = new PhysicalDevice(1);
            Dev = new Device(2);

            Fake = new FakeVulkanFunctions
            {
                MemoryProperties         = BuildMemProps(),
                BufferMemoryRequirements = new MemoryRequirements
                {
                    Size           = 256,
                    Alignment      = 1,
                    MemoryTypeBits = 0b1,
                },
                ImageMemoryRequirements = new MemoryRequirements
                {
                    Size           = 512,
                    Alignment      = 1,
                    MemoryTypeBits = 0b1,
                },
            };

            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = Pd,
                Device         = Dev,
                Flags          = allocatorFlags,
            };
            VmaAllocator.Create(Fake, in ci, out var alloc);
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

    // ── KhrDedicatedAllocationBit tests ───────────────────────────────────────

    public sealed class VmaKhrDedicatedAllocationTests
    {
        [Fact]
        public void AllocateMemoryForBuffer_UsesGetBufferMemoryRequirements2_WhenKhrDedicatedSet()
        {
            var f = new Phase20Fixture(VmaAllocatorCreateFlags.KhrDedicatedAllocationBit);

            var buf = new Silk.NET.Vulkan.Buffer(1);
            var ci  = new VmaAllocationCreateInfo();
            f.Allocator.AllocateMemoryForBuffer(buf, in ci, out var alloc);

            Assert.Equal(1, f.Fake.GetBufferMemoryRequirements2CallCount);
            Assert.Equal(0, f.Fake.GetImageMemoryRequirements2CallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocateMemoryForBuffer_DoesNotUseV2_WhenKhrDedicatedNotSet()
        {
            var f = new Phase20Fixture();

            var buf = new Silk.NET.Vulkan.Buffer(1);
            var ci  = new VmaAllocationCreateInfo();
            f.Allocator.AllocateMemoryForBuffer(buf, in ci, out var alloc);

            Assert.Equal(0, f.Fake.GetBufferMemoryRequirements2CallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocateMemoryForBuffer_ForcesDedicated_WhenRequiresDedicatedAllocation()
        {
            var f = new Phase20Fixture(VmaAllocatorCreateFlags.KhrDedicatedAllocationBit);
            f.Fake.FakeDedicatedRequires = true;

            var buf = new Silk.NET.Vulkan.Buffer(1);
            var ci  = new VmaAllocationCreateInfo();
            f.Allocator.AllocateMemoryForBuffer(buf, in ci, out var alloc);

            Assert.NotNull(alloc);
            Assert.True(alloc!.IsDedicated);
            Assert.Equal(1, f.Fake.AllocateMemoryCallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocateMemoryForBuffer_ForcesDedicated_WhenPrefersDedicatedAndNoCanAlias()
        {
            var f = new Phase20Fixture(VmaAllocatorCreateFlags.KhrDedicatedAllocationBit);
            f.Fake.FakeDedicatedPrefers = true;

            var buf = new Silk.NET.Vulkan.Buffer(1);
            var ci  = new VmaAllocationCreateInfo();
            f.Allocator.AllocateMemoryForBuffer(buf, in ci, out var alloc);

            Assert.NotNull(alloc);
            Assert.True(alloc!.IsDedicated);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocateMemoryForBuffer_DoesNotForceDedicated_WhenPrefersDedicatedButCanAlias()
        {
            var f = new Phase20Fixture(VmaAllocatorCreateFlags.KhrDedicatedAllocationBit);
            f.Fake.FakeDedicatedPrefers = true;

            var buf = new Silk.NET.Vulkan.Buffer(1);
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.CanAliasBit,
            };
            f.Allocator.AllocateMemoryForBuffer(buf, in ci, out var alloc);

            Assert.NotNull(alloc);
            Assert.False(alloc!.IsDedicated);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocateMemoryForImage_UsesGetImageMemoryRequirements2_WhenKhrDedicatedSet()
        {
            var f = new Phase20Fixture(VmaAllocatorCreateFlags.KhrDedicatedAllocationBit);

            var img = new Image(1);
            var ci  = new VmaAllocationCreateInfo();
            f.Allocator.AllocateMemoryForImage(img, in ci, out var alloc);

            Assert.Equal(1, f.Fake.GetImageMemoryRequirements2CallCount);
            Assert.Equal(0, f.Fake.GetBufferMemoryRequirements2CallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocateMemoryForImage_ForcesDedicated_WhenRequiresDedicatedAllocation()
        {
            var f = new Phase20Fixture(VmaAllocatorCreateFlags.KhrDedicatedAllocationBit);
            f.Fake.FakeDedicatedRequires = true;

            var img = new Image(1);
            var ci  = new VmaAllocationCreateInfo();
            f.Allocator.AllocateMemoryForImage(img, in ci, out var alloc);

            Assert.NotNull(alloc);
            Assert.True(alloc!.IsDedicated);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }
    }

    // ── WithinBudgetBit tests ─────────────────────────────────────────────────

    public sealed class VmaWithinBudgetTests
    {
        // Builds a fake with a tiny heap so a single large allocation exceeds budget.
        private static Phase20Fixture SmallHeapFixture()
        {
            var f = new Phase20Fixture();
            // Override the heap to be very small so the budget (80% of heap) is tiny.
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
            // Heap of 1 KiB → budget ≈ 819 bytes (80 %).
            mp.MemoryHeaps.Element0 = new MemoryHeap
            {
                Size  = 1024,
                Flags = MemoryHeapFlags.None,
            };
            f.Fake.MemoryProperties = mp;
            return f;
        }

        [Fact]
        public void AllocateMemory_Succeeds_WhenWithinBudget()
        {
            var f   = new Phase20Fixture();
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.WithinBudgetBit,
            };

            Result r = f.Allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal(Result.Success, r);
            Assert.NotNull(alloc);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocateMemory_ReturnsOutOfDeviceMemory_WhenExceedsBudget()
        {
            // Use real allocator but configure the budget properties via
            // ExtMemoryBudgetBit + FakeBudgetProperties so budget = 0.
            var pd  = new PhysicalDevice(1);
            var dev = new Device(2);
            var mp  = new PhysicalDeviceMemoryProperties { MemoryTypeCount = 1, MemoryHeapCount = 1 };
            mp.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex     = 0,
            };
            mp.MemoryHeaps.Element0 = new MemoryHeap { Size = 256ul * 1024 * 1024, Flags = MemoryHeapFlags.None };

            var fake = new FakeVulkanFunctions
            {
                MemoryProperties = mp,
                BufferMemoryRequirements = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 },
            };

            // Configure budget: usage=250 MiB, budget=200 MiB → any allocation will exceed.
            var fakeBudget = new PhysicalDeviceMemoryBudgetPropertiesEXT();
            unsafe
            {
                fakeBudget.HeapUsage[0]  = 250ul * 1024 * 1024;
                fakeBudget.HeapBudget[0] = 200ul * 1024 * 1024;
            }
            fake.FakeBudgetProperties = fakeBudget;

            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = pd,
                Device         = dev,
                Flags          = VmaAllocatorCreateFlags.ExtMemoryBudgetBit,
            };
            VmaAllocator.Create(fake, in ci, out var allocator);

            var req    = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var allocCi = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.WithinBudgetBit,
            };

            Result r = allocator!.AllocateMemory(in req, in allocCi, out _);
            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);

            allocator.Dispose();
        }

        [Fact]
        public void AllocateMemory_IgnoresBudget_WhenWithinBudgetBitNotSet()
        {
            // Even with usage > budget, allocation without WithinBudgetBit succeeds.
            var pd  = new PhysicalDevice(1);
            var dev = new Device(2);
            var mp  = new PhysicalDeviceMemoryProperties { MemoryTypeCount = 1, MemoryHeapCount = 1 };
            mp.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex     = 0,
            };
            mp.MemoryHeaps.Element0 = new MemoryHeap { Size = 256ul * 1024 * 1024, Flags = MemoryHeapFlags.None };

            var fake = new FakeVulkanFunctions
            {
                MemoryProperties         = mp,
                BufferMemoryRequirements = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 },
            };

            var fakeBudget = new PhysicalDeviceMemoryBudgetPropertiesEXT();
            unsafe
            {
                fakeBudget.HeapUsage[0]  = 250ul * 1024 * 1024;
                fakeBudget.HeapBudget[0] = 200ul * 1024 * 1024;
            }
            fake.FakeBudgetProperties = fakeBudget;

            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = pd,
                Device         = dev,
                Flags          = VmaAllocatorCreateFlags.ExtMemoryBudgetBit,
            };
            VmaAllocator.Create(fake, in ci, out var allocator);

            var req     = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var allocCi = new VmaAllocationCreateInfo();

            Result r = allocator!.AllocateMemory(in req, in allocCi, out var alloc);
            Assert.Equal(Result.Success, r);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }
    }

    // ── CanAliasBit tests ─────────────────────────────────────────────────────

    public sealed class VmaCanAliasBitTests
    {
        [Fact]
        public void CanAlias_IsFalse_WhenCanAliasBitNotSet()
        {
            var f   = new Phase20Fixture();
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo();
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.False(alloc!.CanAlias);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CanAlias_IsTrue_WhenCanAliasBitSet()
        {
            var f   = new Phase20Fixture();
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.CanAliasBit,
            };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.True(alloc!.CanAlias);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CanAlias_IsTrue_ForDedicatedAllocation()
        {
            var f   = new Phase20Fixture();
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit
                      | VmaAllocationCreateFlags.CanAliasBit,
            };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.True(alloc!.CanAlias);
            Assert.True(alloc.IsDedicated);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }
    }

    // ── CreateBufferWithAlignment tests ───────────────────────────────────────

    public sealed class VmaCreateBufferWithAlignmentTests
    {
        [Fact]
        public void CreateBufferWithAlignment_Succeeds_AndReturnsAlignment()
        {
            var f = new Phase20Fixture();

            var bufCi = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size  = 256,
                Usage = BufferUsageFlags.VertexBufferBit,
            };
            var allocCi = new VmaAllocationCreateInfo();

            Result r = f.Allocator.CreateBufferWithAlignment(
                in bufCi, in allocCi, minAlignment: 256,
                out var buffer, out var alloc, out _);

            Assert.Equal(Result.Success, r);
            Assert.NotEqual(default, buffer);
            Assert.NotNull(alloc);
            // Alignment must be at least minAlignment.
            Assert.Equal(0ul, alloc!.Offset % 256ul);

            f.Allocator.DestroyBuffer(buffer, alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateBufferWithAlignment_UsesLargerOfMinAlignmentAndRequiredAlignment()
        {
            var f = new Phase20Fixture();
            // Resource alignment = 1 (from FakeVulkanFunctions.BufferMemoryRequirements).
            // minAlignment = 128 should dominate.

            var bufCi = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size  = 64,
                Usage = BufferUsageFlags.UniformBufferBit,
            };
            var allocCi = new VmaAllocationCreateInfo();

            Result r = f.Allocator.CreateBufferWithAlignment(
                in bufCi, in allocCi, minAlignment: 128,
                out var buffer, out var alloc, out _);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0ul, alloc!.Offset % 128ul);

            f.Allocator.DestroyBuffer(buffer, alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateBufferWithAlignment_RollsBack_WhenAllocationFails()
        {
            var f = new Phase20Fixture();
            f.Fake.AllocateMemoryResult = Result.ErrorOutOfDeviceMemory;

            var bufCi = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size  = 64,
                Usage = BufferUsageFlags.VertexBufferBit,
            };
            var allocCi = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };

            Result r = f.Allocator.CreateBufferWithAlignment(
                in bufCi, in allocCi, minAlignment: 64,
                out var buffer, out var alloc, out _);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Equal(default, buffer);
            Assert.Null(alloc);
            // The buffer must have been created and then destroyed on failure.
            Assert.Equal(1, f.Fake.CreateBufferCallCount);
            Assert.Equal(1, f.Fake.DestroyBufferCallCount);

            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateBufferWithAlignment_ZeroMinAlignment_BehavesLikeCreateBuffer()
        {
            var f = new Phase20Fixture();

            var bufCi = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size  = 128,
                Usage = BufferUsageFlags.VertexBufferBit,
            };
            var allocCi = new VmaAllocationCreateInfo();

            Result r1 = f.Allocator.CreateBufferWithAlignment(
                in bufCi, in allocCi, minAlignment: 0,
                out var buf1, out var alloc1, out _);

            Result r2 = f.Allocator.CreateBuffer(
                in bufCi, in allocCi,
                out var buf2, out var alloc2, out _);

            Assert.Equal(Result.Success, r1);
            Assert.Equal(Result.Success, r2);

            f.Allocator.DestroyBuffer(buf1, alloc1);
            f.Allocator.DestroyBuffer(buf2, alloc2);
            f.Allocator.Dispose();
        }
    }
}
