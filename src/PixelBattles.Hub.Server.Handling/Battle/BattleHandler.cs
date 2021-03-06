﻿using Nito.AsyncEx;
using PixelBattles.Chunkler.Client;
using PixelBattles.Hub.Server.Handlers.Chunk;
using PixelBattles.Hub.Server.Handling.Battle;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PixelBattles.Hub.Server.Handlers.Battle
{
    internal class BattleHandler : IBattleHandler
    {
        private readonly IChunklerClient _chunklerClient;
        private readonly IChunkHandlerFactory _chunkHandlerFactory;
        private readonly ConcurrentDictionary<ChunkKey, AsyncLazy<ChunkHandler>> _chunkHandlers;
        private readonly Dictionary<ChunkKey, AsyncLazy<ChunkHandler>> _compactedChunkHandlers;

        private BattleSettings _battleSettings;

        private int _subscriptionCounter;
        public int SubscriptionCounter => _subscriptionCounter;

        private readonly long _battleId;
        public long BattleId => _battleId;

        private long _lastUpdatedTicksUTC;
        public long LastUpdatedTicksUTC => _lastUpdatedTicksUTC;
        
        private bool _isClosing;
        public bool IsClosing => Volatile.Read(ref _isClosing);

        public BattleHandler(long battleId, BattleSettings battleSettings, IChunklerClient chunklerClient, IChunkHandlerFactory chunkHandlerFactory)
        {
            _isClosing = false;
            _battleId = battleId;
            _battleSettings = battleSettings ?? throw new ArgumentNullException(nameof(battleSettings));
            _subscriptionCounter = 0;
            _lastUpdatedTicksUTC = DateTime.UtcNow.Ticks;
            _chunklerClient = chunklerClient ?? throw new ArgumentNullException(nameof(chunklerClient));
            _chunkHandlerFactory = chunkHandlerFactory ?? throw new ArgumentNullException(nameof(chunkHandlerFactory));
            _chunkHandlers = new ConcurrentDictionary<ChunkKey, AsyncLazy<ChunkHandler>>();
            _compactedChunkHandlers = new Dictionary<ChunkKey, AsyncLazy<ChunkHandler>>();
        }
        
        public async Task<IChunkHandlerSubscription> GetChunkHandlerAndSubscribeAsync(ChunkKey chunkKey, Func<ChunkKey, ChunkUpdate, Task> onUpdate)
        {
            if (!IsValidChunkKey(chunkKey))
            {
                throw new InvalidOperationException($"Chunk {chunkKey} is not allowed.");
            }

            while (true)
            {
                var chunkHandlerLazy = _chunkHandlers.GetOrAdd(
                    key: chunkKey,
                    valueFactory: key => new AsyncLazy<ChunkHandler>(
                        factory: () => _chunkHandlerFactory.CreateChunkHandlerAsync(_battleId, _battleSettings.ChunkSettings, chunkKey, _chunklerClient),
                        flags: AsyncLazyFlags.RetryOnFailure));

                var chunkHandler = await chunkHandlerLazy;

                if (chunkHandler == null)
                {
                    throw new InvalidOperationException($"Chunk {chunkKey} is not found.");
                }

                chunkHandler.IncrementSubscriptionCounter();
                if (chunkHandler.IsClosing)
                {
                    chunkHandler.DecrementSubscriptionCounter();
                    continue;
                }
                
                var subscription = chunkHandler.CreateSubscription(onUpdate);
                return subscription;
            }
        }
        
        public async Task<(int chunkHandlersNotCompacted, int chunkHandlersCompacted)> CompactChunkHandlersAsync(long unusedChunkHanlderTicksUTCLimit)
        {
            int chunkHandlersNotCompacted = 0;
            int chunkHandlersCompacted = 0;
            foreach (var chunkHandlerKVPair in _chunkHandlers)
            {
                if (_compactedChunkHandlers.ContainsKey(chunkHandlerKVPair.Key))
                {
                    ++chunkHandlersNotCompacted;
                    continue;
                }

                var chunkHandler = await chunkHandlerKVPair.Value;
                if (chunkHandler == null || (chunkHandler.LastUpdatedTicksUTC < unusedChunkHanlderTicksUTCLimit && chunkHandler.SubscriptionCounter == 0))
                {
                    if (_chunkHandlers.TryRemove(chunkHandlerKVPair.Key, out var ignore))
                    {
                        chunkHandler.MarkAsClosing();
                        _compactedChunkHandlers.Add(chunkHandlerKVPair.Key, chunkHandlerKVPair.Value);
                        ++chunkHandlersCompacted;
                    }
                    else
                    {
                        throw new InvalidOperationException("ChunkHandler was removed during compaction in unexpected way.");
                    }
                }
                else
                {
                    ++chunkHandlersNotCompacted;
                }
            }
            return (chunkHandlersNotCompacted, chunkHandlersCompacted);
        }

        public async Task<(int chunkHandlersNotRemoved, int chunkHandlersRemoved)> ClearCompactedChunkHandlersAsync()
        {
            int chunkHandlersNotRemoved = 0;
            int chunkHandlersRemoved = 0;
            if (_compactedChunkHandlers.Count == 0)
            {
                return (chunkHandlersNotRemoved, chunkHandlersRemoved);
            }

            var chunkHandlersToDelete = new List<ChunkHandler>(_compactedChunkHandlers.Count);
            foreach (var chunkHandlerLazy in _compactedChunkHandlers)
            {
                var chunkHandler = await chunkHandlerLazy.Value;
                if (chunkHandler.SubscriptionCounter == 0)
                {
                    chunkHandlersToDelete.Add(chunkHandler);
                    ++chunkHandlersRemoved;
                }
                else
                {
                    ++chunkHandlersNotRemoved;
                }
            }

            foreach (var chunkHandler in chunkHandlersToDelete)
            {
                if (!_compactedChunkHandlers.Remove(chunkHandler.ChunkKey))
                {
                    throw new InvalidOperationException("ChunkHandler was deleted in unexpected way from compaction.");
                }
                await chunkHandler.CloseAsync();
            }
            return (chunkHandlersNotRemoved, chunkHandlersRemoved);
        }

        public void IncrementSubscriptionCounter()
        {
            Interlocked.Increment(ref _subscriptionCounter);
        }

        public void DecrementSubscriptionCounter()
        {
            Interlocked.Decrement(ref _subscriptionCounter);
        }

        public void MarkAsClosing()
        {
            Volatile.Write(ref _isClosing, true);
        }

        public void Dispose()
        {
            _chunklerClient?.Dispose();
        }

        private bool IsValidChunkKey(ChunkKey chunkKey)
        {
            return chunkKey.X >= _battleSettings.MinWidthIndex
                && chunkKey.X <= _battleSettings.MaxWidthIndex
                && chunkKey.Y >= _battleSettings.MinHeightIndex
                && chunkKey.Y <= _battleSettings.MaxHeightIndex;
        }
    }
}