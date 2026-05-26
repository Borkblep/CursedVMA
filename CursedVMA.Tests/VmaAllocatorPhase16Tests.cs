// Phase 16 tests: DeviceMemoryCallbacks, pNext chaining (MemoryPriority,
// BufferDeviceAddress), and aliasing resource creation.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System;
using Xunit;

namespace CursedVMA.Tests
{
    // ── Shared fixture ──────────────────────────────────────────────────────

    internal sealed class Phase16Fixture
    {
        public FakeVulkanFunctions Fake { get; }
        public PhysicalDevice Pd { get; }
        public Device Dev { get; }

        public Phase16Fixture(VmaAllocatorCreateFlags extraFlags = VmaAllocatorCreateFlags.None,
                              VmaDeviceMemoryCallbacks? callbacks = null)
        {
            Pd  = new PhysicalDevice(1);
            Dev = new Device(2);

            Fake = new FakeVulkanFunctions
            {
                MemoryProperties         = BuildMemProps(),
                BufferMemoryRequirements = new MemoryRequirements
                {
                    Size           = 1024,
                    Alignment      = 256,
                    MemoryTypeBits = 1,
                },
                ImageMemoryRequirements = new MemoryRequirements
                {
                    Size           = 512,
                    Alignment      = 128,
                    MemoryTypeBits = 1,
                },
            };

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice        = Pd,
                Device                = Dev,
                Flags                 = extraFlags,
                DeviceMemoryCallbacks = callbacks,
            };

            var r = VmaAllocator.Create(Fake, in info, out var allocator);
            Assert.Equal(Result.Success, r);
            Allocator = allocator!;
        }

        public VmaAllocator Allocator { get; }

        private static PhysicalDeviceMemoryProperties BuildMemProps()
        {
            var mp = new PhysicalDeviceMemoryProperties
            {
                MemoryTypeCount = 1,
                MemoryHeapCount = 1,
            };
            mp.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex     = 0,
            };
            mp.MemoryHeaps.Element0 = new MemoryHeap
            {
                Size  = 1024ul * 1024 * 1024,
                Flags = MemoryHeapFlags.DeviceLocalBit,
            };
            return mp;
        }
    }

    // ── DeviceMemoryCallbacks ───────────────────────────────────────────────

    public sealed class VmaDeviceMemoryCallbacksTests
    {
        [Fact]
        public void AllocCallback_FiredAfterDedicatedAllocation()
        {
            int callCount = 0;
            DeviceMemory lastMemory = default;
            uint lastMemType = 99;
            ulong lastSize = 0;

            var callbacks = new VmaDeviceMemoryCallbacks
            {
                PfnAllocate = (alloc, memType, memory, size, userData) =>
                {
                    callCount++;
                    lastMemType = memType;
                    lastMemory  = memory;
                    lastSize    = size;
                },
            };

            var f = new Phase16Fixture(callbacks: callbacks);
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            var r = f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, callCount);
            Assert.Equal(0u, lastMemType);
            Assert.Equal(1024ul, lastSize);
            Assert.NotEqual(default, lastMemory);
            f.Allocator.Dispose();
        }

        [Fact]
        public void FreeCallback_FiredBeforeDedicatedFree()
        {
            int callCount = 0;
            DeviceMemory capturedMemory = default;

            var callbacks = new VmaDeviceMemoryCallbacks
            {
                PfnFree = (alloc, memType, memory, size, userData) =>
                {
                    callCount++;
                    capturedMemory = memory;
                },
            };

            var f = new Phase16Fixture(callbacks: callbacks);
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal(0, callCount);

            f.Allocator.FreeMemory(alloc);

            Assert.Equal(1, callCount);
            Assert.NotEqual(default, capturedMemory);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllocCallback_FiredAfterBlockCreation()
        {
            int callCount = 0;
            var callbacks = new VmaDeviceMemoryCallbacks
            {
                PfnAllocate = (_, _, _, _, _) => callCount++,
            };

            var f = new Phase16Fixture(callbacks: callbacks);
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo();

            // First sub-allocation triggers block creation.
            var r = f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, callCount);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void FreeCallback_FiredWhenBlockFreed()
        {
            int freeCount = 0;
            var callbacks = new VmaDeviceMemoryCallbacks
            {
                PfnFree = (_, _, _, _, _) => freeCount++,
            };

            var f = new Phase16Fixture(callbacks: callbacks);
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo();

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal(0, freeCount);

            // Freeing the last allocation causes the empty block to be freed.
            f.Allocator.FreeMemory(alloc);
            Assert.Equal(1, freeCount);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CallbackUserData_PassedThrough()
        {
            var expected = new object();
            object? received = null;

            var callbacks = new VmaDeviceMemoryCallbacks
            {
                PfnAllocate = (_, _, _, _, userData) => received = userData,
                UserData    = expected,
            };

            var f = new Phase16Fixture(callbacks: callbacks);
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Same(expected, received);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void DisposeFiresFreeCallbackForLiveAllocations()
        {
            int freeCount = 0;
            var callbacks = new VmaDeviceMemoryCallbacks
            {
                PfnFree = (_, _, _, _, _) => freeCount++,
            };

            var f = new Phase16Fixture(callbacks: callbacks);
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            f.Allocator.AllocateMemory(in req, in ci, out _);
            Assert.Equal(0, freeCount);

            f.Allocator.Dispose();
            Assert.Equal(1, freeCount);
        }
    }

    // ── MemoryPriority pNext chaining ───────────────────────────────────────

    public sealed class VmaMemoryPriorityTests
    {
        [Fact]
        public void DedicatedAlloc_PriorityChainedWhenFlagSet()
        {
            var f = new Phase16Fixture(extraFlags: VmaAllocatorCreateFlags.ExtMemoryPriorityBit);
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags    = VmaAllocationCreateFlags.DedicatedMemoryBit,
                Priority = 0.8f,
            };

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(StructureType.MemoryPriorityAllocateInfoExt,
                f.Fake.LastAllocatePNextSType);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void BlockAlloc_PriorityChainedWhenFlagSet()
        {
            var f = new Phase16Fixture(extraFlags: VmaAllocatorCreateFlags.ExtMemoryPriorityBit);
            var req = new MemoryRequirements { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo();

            // Block creation triggers AllocateMemory; check that pNext was set.
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(StructureType.MemoryPriorityAllocateInfoExt,
                f.Fake.LastAllocatePNextSType);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void NoPriorityChain_WhenFlagNotSet()
        {
            var f = new Phase16Fixture();
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags    = VmaAllocationCreateFlags.DedicatedMemoryBit,
                Priority = 0.8f,
            };

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Null(f.Fake.LastAllocatePNextSType);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }
    }

    // ── BufferDeviceAddress pNext chaining ──────────────────────────────────

    public sealed class VmaBufferDeviceAddressTests
    {
        [Fact]
        public void DeviceAddressFlagChained_WhenFlagSet()
        {
            var f = new Phase16Fixture(extraFlags: VmaAllocatorCreateFlags.BufferDeviceAddressBit);
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(StructureType.MemoryAllocateFlagsInfo,
                f.Fake.LastAllocatePNextSType);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void PriorityChain_IsHeadWhenBothFlagsSet()
        {
            var flags = VmaAllocatorCreateFlags.BufferDeviceAddressBit
                      | VmaAllocatorCreateFlags.ExtMemoryPriorityBit;
            var f = new Phase16Fixture(extraFlags: flags);
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags    = VmaAllocationCreateFlags.DedicatedMemoryBit,
                Priority = 0.5f,
            };

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            // Priority is chained last (head of chain) in the build order.
            Assert.Equal(StructureType.MemoryPriorityAllocateInfoExt,
                f.Fake.LastAllocatePNextSType);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void NoDeviceAddressChain_WhenFlagNotSet()
        {
            var f = new Phase16Fixture();
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };

            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Null(f.Fake.LastAllocatePNextSType);
            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }
    }

    // ── Aliasing resources ──────────────────────────────────────────────────

    public sealed class VmaAliasingResourceTests
    {
        private Phase16Fixture MakeFixture() => new Phase16Fixture();

        [Fact]
        public void CreateAliasingBuffer_BindsToExistingAllocation()
        {
            var f   = MakeFixture();
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            int bindBefore = f.Fake.BindBufferMemoryCallCount;
            int createBefore = f.Fake.CreateBufferCallCount;

            var bufCI = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 512 };
            var r = f.Allocator.CreateAliasingBuffer(alloc!, in bufCI, out var buf);

            Assert.Equal(Result.Success, r);
            Assert.NotEqual(default, buf);
            Assert.Equal(createBefore + 1, f.Fake.CreateBufferCallCount);
            Assert.Equal(bindBefore   + 1, f.Fake.BindBufferMemoryCallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateAliasingImage_BindsToExistingAllocation()
        {
            var f   = MakeFixture();
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            int bindBefore   = f.Fake.BindImageMemoryCallCount;
            int createBefore = f.Fake.CreateImageCallCount;

            var imgCI = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            var r = f.Allocator.CreateAliasingImage(alloc!, in imgCI, out var img);

            Assert.Equal(Result.Success, r);
            Assert.NotEqual(default, img);
            Assert.Equal(createBefore + 1, f.Fake.CreateImageCallCount);
            Assert.Equal(bindBefore   + 1, f.Fake.BindImageMemoryCallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateAliasingBuffer_RollsBackOnCreateFailure()
        {
            var f   = MakeFixture();
            f.Fake.CreateBufferResult = Result.ErrorOutOfDeviceMemory;

            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            var bufCI = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 512 };
            var r = f.Allocator.CreateAliasingBuffer(alloc!, in bufCI, out var buf);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Equal(default, buf);
            Assert.Equal(0, f.Fake.BindBufferMemoryCallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateAliasingBuffer_RollsBackOnBindFailure()
        {
            var f   = MakeFixture();
            f.Fake.BindBufferMemoryResult = Result.ErrorOutOfDeviceMemory;

            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            int destroyBefore = f.Fake.DestroyBufferCallCount;
            var bufCI = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 512 };
            var r = f.Allocator.CreateAliasingBuffer(alloc!, in bufCI, out var buf);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Equal(default, buf);
            Assert.Equal(destroyBefore + 1, f.Fake.DestroyBufferCallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateAliasingBuffer2_NullPNext_BindsViaVkBindBufferMemory()
        {
            // CreateAliasingBuffer2 always passes pNext=null to its underlying
            // BindBufferMemory2 helper, which falls through to vkBindBufferMemory
            // (not the vkBindBufferMemory2 variant). Direct callers can reach
            // the vkBindBufferMemory2 path via VmaAllocator.BindBufferMemory2
            // with a non-null pNext; that path is covered by the existing
            // BindBufferMemory2_WithPNext tests.
            var f   = MakeFixture();
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            int bindBefore = f.Fake.BindBufferMemoryCallCount;
            var bufCI = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 256 };
            var r = f.Allocator.CreateAliasingBuffer2(alloc!, 0, in bufCI, out var buf);

            Assert.Equal(Result.Success, r);
            Assert.NotEqual(default, buf);
            Assert.Equal(bindBefore + 1, f.Fake.BindBufferMemoryCallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CreateAliasingImage2_NullPNext_BindsViaVkBindImageMemory()
        {
            // Same as the buffer variant above: CreateAliasingImage2 passes
            // pNext=null, so vkBindImageMemory (1-variant) is used. The
            // vkBindImageMemory2 path is covered by BindImageMemory2_WithPNext.
            var f   = MakeFixture();
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            int bindBefore = f.Fake.BindImageMemoryCallCount;
            var imgCI = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            var r = f.Allocator.CreateAliasingImage2(alloc!, 0, in imgCI, out var img);

            Assert.Equal(Result.Success, r);
            Assert.NotEqual(default, img);
            Assert.Equal(bindBefore + 1, f.Fake.BindImageMemoryCallCount);

            f.Allocator.FreeMemory(alloc);
            f.Allocator.Dispose();
        }
    }
}
