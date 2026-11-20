using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TileDownloader.Services
{
    /// <summary>
    /// 全局下载并发闸门：整个应用所有任务共享同一个并发上限。
    /// 改造前每个任务各用「并发数」个连接，同时跑 N 个任务就是 N×并发数；
    /// 现在无论开几个任务，同时在飞的瓦片请求总数不超过该上限。
    /// 支持运行中调整：调大立即放行等待者，调小则等既有请求自然结束后收敛。
    /// </summary>
    public sealed class GlobalDownloadLimiter
    {
        private sealed class Waiter
        {
            public readonly TaskCompletionSource<bool> Completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public LinkedListNode<Waiter>? Node;

            public CancellationTokenRegistration Registration;
        }

        private sealed class Slot : IDisposable
        {
            private GlobalDownloadLimiter? _owner;

            public Slot(GlobalDownloadLimiter owner) => _owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release();
            }
        }

        private readonly object _gate = new();
        private readonly LinkedList<Waiter> _waiters = new();

        private int _limit;
        private int _inFlight;

        public GlobalDownloadLimiter(int limit = 64)
        {
            _limit = Math.Max(1, limit);
        }

        /// <summary>当前全局并发上限</summary>
        public int Limit
        {
            get
            {
                lock (_gate)
                {
                    return _limit;
                }
            }
        }

        /// <summary>当前在飞请求数</summary>
        public int InFlight
        {
            get
            {
                lock (_gate)
                {
                    return _inFlight;
                }
            }
        }

        /// <summary>当前排队等待名额的请求数</summary>
        public int Waiting
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        /// <summary>调整全局并发上限（设置页改动后即时生效）</summary>
        public void SetLimit(int limit)
        {
            limit = Math.Max(1, limit);
            var granted = new List<Waiter>();
            lock (_gate)
            {
                _limit = limit;
                while (_waiters.Count > 0 && _inFlight < _limit)
                {
                    _inFlight++;
                    granted.Add(TakeWaiterLocked());
                }
            }

            foreach (var waiter in granted)
            {
                waiter.Completion.TrySetResult(true);
            }
        }

        /// <summary>申请一个并发名额；取消令牌触发时放弃排队</summary>
        public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            Waiter? waiter = null;
            lock (_gate)
            {
                if (_inFlight < _limit)
                {
                    _inFlight++;
                }
                else
                {
                    waiter = new Waiter();
                    waiter.Node = _waiters.AddLast(waiter);
                    if (ct.CanBeCanceled)
                    {
                        waiter.Registration = ct.Register(state => CancelWaiter((Waiter)state!), waiter);
                    }
                }
            }

            if (waiter != null)
            {
                using (waiter.Registration)
                {
                    await waiter.Completion.Task.ConfigureAwait(false);
                }
            }

            return new Slot(this);
        }

        private void Release()
        {
            Waiter? granted = null;
            lock (_gate)
            {
                if (_inFlight > _limit)
                {
                    // 上限被调小：先消化超额占用，不急于放行等待者
                    _inFlight--;
                }
                else if (_waiters.Count > 0)
                {
                    // 名额直接转交给最早的等待者：一放一收，在飞数不变
                    granted = TakeWaiterLocked();
                }
                else if (_inFlight > 0)
                {
                    _inFlight--;
                }
            }

            granted?.Completion.TrySetResult(true);
        }

        private void CancelWaiter(Waiter waiter)
        {
            Waiter? granted = null;
            lock (_gate)
            {
                if (waiter.Node != null)
                {
                    _waiters.Remove(waiter.Node);
                    waiter.Node = null;
                    // 排队者退出：若此时恰好有空闲名额，补给后面的等待者
                    if (_waiters.Count > 0 && _inFlight < _limit)
                    {
                        _inFlight++;
                        granted = TakeWaiterLocked();
                    }
                }
            }

            waiter.Completion.TrySetCanceled();

            granted?.Completion.TrySetResult(true);
        }

        /// <summary>取出队首等待者（调用方负责名额增减与持锁）</summary>
        private Waiter TakeWaiterLocked()
        {
            var waiter = _waiters.First!.Value;
            _waiters.RemoveFirst();
            waiter.Node = null;
            return waiter;
        }
    }
}
