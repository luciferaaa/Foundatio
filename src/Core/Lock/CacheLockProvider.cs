﻿using System;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Messaging;
using Nito.AsyncEx;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Foundatio.Extensions;

namespace Foundatio.Lock {
    public class CacheLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, AsyncManualResetEvent> _resetEvents = new ConcurrentDictionary<string, AsyncManualResetEvent>();

        public CacheLockProvider(ICacheClient cacheClient, IMessageBus messageBus) {
            _cacheClient = new ScopedCacheClient(cacheClient, "lock");
            _messageBus = messageBus;
            _messageBus.Subscribe<CacheLockReleased>(message => OnLockReleased(message));
        }
        
        private void OnLockReleased(CacheLockReleased msg) {
            Logger.Trace().Message($"Got lock released message: {msg.Name}").Write();
            AsyncManualResetEvent resetEvent;
            if (_resetEvents.TryGetValue(msg.Name, out resetEvent))
                resetEvent.Set();
        }

        public async Task<IDisposable> AcquireLockAsync(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message("AcquireLock: {0}", name).Write();
            if (!lockTimeout.HasValue)
                lockTimeout = TimeSpan.FromMinutes(20);
            if (!acquireTimeout.HasValue)
                acquireTimeout = TimeSpan.FromMinutes(1);

            var acquireTimeoutCancellationToken = new CancellationTokenSource(acquireTimeout.Value);
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(acquireTimeoutCancellationToken.Token, cancellationToken).Token;

            var timeoutTime = DateTime.UtcNow.Add(acquireTimeout.Value);
            Logger.Trace().Message($"Timeout time: {timeoutTime.ToString("mm:ss.fff")}").Write();
            bool allowLock = false;

            do {
                bool gotLock;
                if (lockTimeout.Value == TimeSpan.Zero) // no lock timeout
                    gotLock = await _cacheClient.AddAsync(name, DateTime.UtcNow).AnyContext();
                else
                    gotLock = await _cacheClient.AddAsync(name, DateTime.UtcNow, lockTimeout.Value).AnyContext();

                if (gotLock) {
                    allowLock = true;
                    Logger.Trace().Message("Acquired lock: {0}", name).Write();
                    break;
                }

                Logger.Trace().Message("Failed to acquire lock: {0}", name).Write();
                
                if (acquireTimeout.Value == TimeSpan.Zero || timeoutTime < DateTime.UtcNow) {
                    Logger.Trace().Message("Acquire time out of zero or timeout exceeded").Write();
                    break;
                }

                var keyExpiration = DateTime.UtcNow.Add(await _cacheClient.GetExpirationAsync(name).AnyContext() ??  TimeSpan.FromSeconds(1));
                var delayAmount = (timeoutTime < keyExpiration ? timeoutTime.Subtract(DateTime.UtcNow) : keyExpiration.Subtract(DateTime.UtcNow)).Max(TimeSpan.FromSeconds(1));
                Logger.Trace().Message("Delay time: {0}", delayAmount).Write();
                var resetEvent = _resetEvents.GetOrAdd(name, new AsyncManualResetEvent());
                await Task.WhenAny(Task.Delay(delayAmount, linkedCancellationToken), resetEvent.WaitAsync()).AnyContext();
                resetEvent.Reset();

                if (linkedCancellationToken.IsCancellationRequested) {
                    Logger.Trace().Message("Cancellation requested.").Write();
                    break;
                }
            } while (!linkedCancellationToken.IsCancellationRequested && DateTime.UtcNow <= timeoutTime);

            if (!allowLock)
                return null;

            Logger.Trace().Message("Returning lock: {0}", name).Write();
            return new DisposableLock(name, this);
        }

        public async Task<bool> IsLockedAsync(string name) {
            return await _cacheClient.GetAsync<object>(name).AnyContext() != null;
        }

        public async Task ReleaseLockAsync(string name) {
            Logger.Trace().Message("ReleaseLockAsync: {0}", name).Write();
            await _cacheClient.RemoveAsync(name).AnyContext();
            await _messageBus.PublishAsync(new CacheLockReleased { Name = name }).AnyContext();
        }
        
        public void Dispose() { }
    }

    internal class CacheLockReleased {
        public string Name { get; set; }
    }
}