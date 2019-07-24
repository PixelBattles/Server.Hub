﻿using PixelBattles.Chunkler.Client;
using System.Threading.Tasks;

namespace PixelBattles.Hub.Server.Handlers.Chunk
{
    public interface IChunkHandlerFactory
    {
        Task<IChunkHandler> CreateChunkHandlerAsync(long battleId, ChunkKey chunkKey, IChunklerClient chunklerClient);
    }
}