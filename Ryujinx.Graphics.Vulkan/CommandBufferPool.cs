﻿using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Thread = System.Threading.Thread;

namespace Ryujinx.Graphics.Vulkan
{
    class CommandBufferPool : IDisposable
    {
        public const int MaxCommandBuffers = 16;

        private int _totalCommandBuffers;
        private int _totalCommandBuffersMask;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly Queue _queue;
        private readonly CommandPool _pool;
        private readonly Thread _owner;

        public int PerFrame = 0;

        public bool OwnedByCurrentThread => _owner == Thread.CurrentThread;

        private struct ReservedCommandBuffer
        {
            public bool InUse;
            public bool InConsumption;
            public bool Active;
            public CommandBuffer CommandBuffer;
            public FenceHolder Fence;
            public SemaphoreHolder Semaphore;

            public List<IAuto> Dependants;
            public HashSet<MultiFenceHolder> Waitables;
            public HashSet<SemaphoreHolder> Dependencies;

            public void Initialize(Vk api, Device device, CommandPool pool)
            {
                var allocateInfo = new CommandBufferAllocateInfo()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandBufferCount = 1,
                    CommandPool = pool,
                    Level = CommandBufferLevel.Primary
                };

                api.AllocateCommandBuffers(device, allocateInfo, out CommandBuffer);

                Dependants = new List<IAuto>();
                Waitables = new HashSet<MultiFenceHolder>();
                Dependencies = new HashSet<SemaphoreHolder>();

                Active = true; // Fence should be refreshed.
            }
        }

        private readonly ReservedCommandBuffer[] _commandBuffers;

        private int _cursor;

        public unsafe CommandBufferPool(Vk api, Device device, Queue queue, uint queueFamilyIndex, bool isLight = false)
        {
            _api = api;
            _device = device;
            _queue = queue;
            _owner = Thread.CurrentThread;

            var commandPoolCreateInfo = new CommandPoolCreateInfo()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                Flags = CommandPoolCreateFlags.CommandPoolCreateTransientBit |
                        CommandPoolCreateFlags.CommandPoolCreateResetCommandBufferBit
            };

            api.CreateCommandPool(device, commandPoolCreateInfo, null, out _pool).ThrowOnError();

            _totalCommandBuffers = isLight ? 1 : MaxCommandBuffers;
            _totalCommandBuffersMask = _totalCommandBuffers - 1;

            _commandBuffers = new ReservedCommandBuffer[_totalCommandBuffers];

            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                _commandBuffers[i].Initialize(api, device, _pool);
            }
        }

        public void AddDependant(int cbIndex, IAuto dependant)
        {
            dependant.IncrementReferenceCount();
            _commandBuffers[cbIndex].Dependants.Add(dependant);
        }

        public void AddWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref var entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddDependency(int cbIndex, CommandBufferScoped dependencyCbs)
        {
            Debug.Assert(_commandBuffers[cbIndex].InUse);
            var semaphoreHolder = _commandBuffers[dependencyCbs.CommandBufferIndex].Semaphore;
            semaphoreHolder.Get();
            _commandBuffers[cbIndex].Dependencies.Add(semaphoreHolder);
        }

        public void AddWaitable(int cbIndex, MultiFenceHolder waitable)
        {
            ref var entry = ref _commandBuffers[cbIndex];
            waitable.AddFence(cbIndex, entry.Fence);
            entry.Waitables.Add(waitable);
        }

        public bool HasWaitable(int cbIndex, MultiFenceHolder waitable)
        {
            ref var entry = ref _commandBuffers[cbIndex];
            return entry.Waitables.Contains(waitable);
        }

        public bool HasWaitableOnRentedCommandBuffer(MultiFenceHolder waitable, int offset, int size)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref var entry = ref _commandBuffers[i];

                    if (entry.InUse &&
                        entry.Waitables.Contains(waitable) &&
                        waitable.IsBufferRangeInUse(i, offset, size))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsFenceOnRentedCommandBuffer(FenceHolder fence)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref var entry = ref _commandBuffers[i];

                    if (entry.InUse && entry.Fence == fence)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public FenceHolder GetFence(int cbIndex)
        {
            return _commandBuffers[cbIndex].Fence;
        }

        private void CheckConsumption(int startAt)
        {
            int wrapBefore = _totalCommandBuffers + 1;
            int index = (startAt + wrapBefore) % _totalCommandBuffers;

            for (int i = 0; i < _totalCommandBuffers - 1; i++)
            {
                ref var entry = ref _commandBuffers[index];

                if (!entry.InUse && entry.InConsumption && entry.Fence.IsSignaled())
                {
                    WaitAndDecrementRef(index);
                }

                index = (index + wrapBefore) % _totalCommandBuffers;
            }
        }

        public CommandBufferScoped ReturnAndRent(CommandBufferScoped cbs)
        {
            Return(cbs);
            return Rent();
        }

        public CommandBufferScoped Rent()
        {
            PerFrame++;
            lock (_commandBuffers)
            {
                CheckConsumption(_cursor);

                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    int currentIndex = _cursor;

                    ref var entry = ref _commandBuffers[currentIndex];

                    if (!entry.InUse)
                    {
                        WaitAndDecrementRef(currentIndex);
                        entry.InUse = true;
                        entry.Active = true;

                        var commandBufferBeginInfo = new CommandBufferBeginInfo()
                        {
                            SType = StructureType.CommandBufferBeginInfo
                        };

                        _api.BeginCommandBuffer(entry.CommandBuffer, commandBufferBeginInfo);

                        return new CommandBufferScoped(this, entry.CommandBuffer, currentIndex);
                    }

                    _cursor = (currentIndex + 1) & _totalCommandBuffersMask;
                }

                return default;
            }
        }

        public void Return(CommandBufferScoped cbs)
        {
            Return(cbs, null, null, null);
        }

        public unsafe void Return(
            CommandBufferScoped cbs,
            Semaphore[] waitSemaphores,
            PipelineStageFlags[] waitDstStageMask,
            Semaphore[] signalSemaphores)
        {
            lock (_commandBuffers)
            {
                int cbIndex = cbs.CommandBufferIndex;

                ref var entry = ref _commandBuffers[cbIndex];

                if (_cursor == cbIndex)
                {
                    _cursor = (cbIndex + 1) & _totalCommandBuffersMask;
                }

                Debug.Assert(entry.InUse);
                Debug.Assert(entry.CommandBuffer.Handle == cbs.CommandBuffer.Handle);
                entry.InUse = false;
                entry.InConsumption = true;

                var commandBuffer = entry.CommandBuffer;

                _api.EndCommandBuffer(commandBuffer);

                fixed (Semaphore* pWaitSemaphores = waitSemaphores, pSignalSemaphores = signalSemaphores)
                {
                    fixed (PipelineStageFlags* pWaitDstStageMask = waitDstStageMask)
                    {
                        SubmitInfo sInfo = new SubmitInfo()
                        {
                            SType = StructureType.SubmitInfo,
                            WaitSemaphoreCount = waitSemaphores != null ? (uint)waitSemaphores.Length : 0,
                            PWaitSemaphores = pWaitSemaphores,
                            PWaitDstStageMask = pWaitDstStageMask,
                            CommandBufferCount = 1,
                            PCommandBuffers = &commandBuffer,
                            SignalSemaphoreCount = signalSemaphores != null ? (uint)signalSemaphores.Length : 0,
                            PSignalSemaphores = pSignalSemaphores
                        };

                        _api.QueueSubmit(_queue, 1, sInfo, entry.Fence.GetUnsafe());
                    }
                }
                // _api.QueueWaitIdle(_queue);
            }
        }

        private void WaitAndDecrementRef(int cbIndex, bool refreshFence = true)
        {
            ref var entry = ref _commandBuffers[cbIndex];

            if (!entry.Active)
            {
                return;
            }

            entry.Active = false;

            if (entry.InConsumption)
            {
                entry.Fence.Wait();
                entry.InConsumption = false;
            }

            foreach (var dependant in entry.Dependants)
            {
                dependant.DecrementReferenceCount(cbIndex);
            }

            foreach (var waitable in entry.Waitables)
            {
                waitable.RemoveFence(cbIndex, entry.Fence);
                waitable.RemoveBufferUses(cbIndex);
            }

            foreach (var dependency in entry.Dependencies)
            {
                dependency.Put();
            }

            entry.Dependants.Clear();
            entry.Waitables.Clear();
            entry.Dependencies.Clear();
            entry.Fence?.Dispose();

            if (refreshFence)
            {
                entry.Fence = new FenceHolder(_api, _device);
            }
            else
            {
                entry.Fence = null;
            }
        }

        public unsafe void Dispose()
        {
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                WaitAndDecrementRef(i, refreshFence: false);
            }

            _api.DestroyCommandPool(_device, _pool, null);
        }
    }
}
