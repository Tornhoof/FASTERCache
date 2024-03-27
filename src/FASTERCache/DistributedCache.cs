﻿using FASTER.core;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using static FASTERCache.DistributedCache;

namespace FASTERCache;

/// <summary>
/// Implements IDistributedCache
/// </summary>
internal sealed partial class DistributedCache :
    CacheBase<DistributedCache.Input, DistributedCache.Output, Empty, DistributedCache.CacheFunctions>,
    IDistributedCache, IDisposable, IExperimentalBufferCache
{
    protected override byte KeyPrefix => (byte)'D';

    // heavily influenced by https://github.com/microsoft/FASTER/blob/main/cs/samples/CacheStore/

    public DistributedCache(CacheService cacheService, IServiceProvider services)
        : this(cacheService, GetClock(services))
    {
    }

    static object? GetClock(IServiceProvider services)
    {
#if NET8_0_OR_GREATER
        if (services.GetService<TimeProvider>() is { } time)
        {
            return time;
        }
#endif
        return services.GetService<ISystemClock>();
    }

    internal DistributedCache(CacheService cacheService, object? clock) : base(cacheService, clock switch
    {
#if NET8_0_OR_GREATER
        TimeProvider timeProvider => CacheFunctions.Create(timeProvider),
#endif
        ISystemClock systemClock => CacheFunctions.Create(systemClock),
        _ => CacheFunctions.Create()
    })
    {
    }


    private bool Slide(in Output output, ref Input input)
    {
        if (output.SlidingExpiration > 0)
        {
            var newAbsolute = NowTicks + output.SlidingExpiration;
            if (newAbsolute > output.AbsoluteExpiration)
            {
                input = input.Slide(newAbsolute);
                return true;
            }
        }

        return false;
    }

    const int MAX_STACKALLOC_SIZE = 128;

    ReadOnlySpan<byte> WriteValue(Span<byte> target, byte[] value, out byte[] lease,
        DistributedCacheEntryOptions? options)
    {
        var absoluteExpiration = GetExpiryTicks(options, out var slidingExpiration);
        lease = EnsureSize(ref target, value.Length + 12);
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(0, 8), absoluteExpiration);
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(8, 4), slidingExpiration);
        value.CopyTo(target.Slice(12));
        return target;
    }

    Memory<byte> WriteValue(ReadOnlySequence<byte> value, out byte[] lease, DistributedCacheEntryOptions? options)
    {
        var absoluteExpiration = GetExpiryTicks(options, out var slidingExpiration);
        var length = checked((int)value.Length + 12);
        lease = ArrayPool<byte>.Shared.Rent(length);
        BinaryPrimitives.WriteInt64LittleEndian(new(lease, 0, 8), absoluteExpiration);
        BinaryPrimitives.WriteInt32LittleEndian(new(lease, 8, 4), slidingExpiration);
        value.CopyTo(new(lease, 12, (int)value.Length));
        return new(lease, 0, length);
    }

    private unsafe Task<TResult?> GetAsync<TResult>(string key, IBufferWriter<byte>? bufferWriter, bool readArray,
        CancellationToken token, Func<Output, TResult?> selector)
    {
        Span<byte> buffer = stackalloc byte[MAX_STACKALLOC_SIZE];
        WriteKey(ref buffer, key, out var keyLease);

        var session = GetSession();
        try
        {
            fixed (byte* __ = buffer)
            {
                var fixedKey = SpanByte.FromFixedSpan(buffer);

                var input = new Input(readArray ? Input.OperationFlags.ReadArray : Input.OperationFlags.None,
                    bufferWriter);

                var readResultTask = session.ReadAsync(fixedKey, input, token: token);
                if (readResultTask.IsCompletedSuccessfully)
                {
                    var result = readResultTask.Result;
                    var status = result.Status;
                    var output = result.Output;
                    if (status.IsPending)
                    {
                        (status, output) = result.Complete();
                    }

                    if (status is { IsCompletedSuccessfully: true, Found: true })
                    {
                        var finalResult = selector(output);
                        Debug.WriteLine($"Read: {status}");

                        if (Slide(output, ref input))
                        {
                            var upsertResultTask =
                                session.BasicContext.UpsertAsync(fixedKey, input, default, token: token);
                            if (upsertResultTask.IsCompletedSuccessfully)
                            {
                                var upsertResult = upsertResultTask.Result;
                                status = upsertResult.Status;
                                if (status.IsPending)
                                {
                                    upsertResult.Complete();
                                }
                            }
                            else
                            {
                                return AwaitUpsertResultTask(upsertResultTask, keyLease, session, finalResult);
                            }

                            Debug.WriteLine($"Upsert (slide): {status}");
                        }

                        keyLease?.Dispose();
                        ReuseSession(session);
                        return Task.FromResult(finalResult);
                    }

                    return Task.FromResult<TResult?>(default);

                }

                int length = fixedKey.Length;
                if (keyLease is null)
                {
                    // we now have to transition to async, the fixedKey is not valid anymore if it is on the stack
                    // convert it to memoryowner, the memory is probably larger than the length, so keep the length
                    // this should only happen for the slide case
                    (keyLease, _) = fixedKey.ToMemoryOwner(MemoryPool<byte>.Shared);
                }

                return AwaitReadResultTask(readResultTask, keyLease, length, session, selector, input, token);
            }
        }
        catch
        {
            FaultSession(session);
            throw;
        }
    }

    private async Task<TResult?> AwaitReadResultTask<TResult>(
        ValueTask<FasterKV<SpanByte, SpanByte>.ReadAsyncResult<Input, Output, Empty>> readResultTask,
        IMemoryOwner<byte> keyLease, int keyLeaseLength, ClientSession<SpanByte, SpanByte, Input, Output, Empty, CacheFunctions> session,
        Func<Output, TResult?> selector, Input input,  CancellationToken token)
    {
        var result = await readResultTask;
        var status = result.Status;
        var output = result.Output;
        if (status.IsPending)
        {
            (status, output) = result.Complete();
        }

        TResult? finalResult = default;
        if (status is {IsCompletedSuccessfully: true, Found: true})
        {
            finalResult = selector(output);
            Debug.WriteLine($"Read: {status}");

            if (Slide(output, ref input))
            {
                using (keyLease.Memory.Pin())
                {
                    var fixedKey = SpanByte.FromPinnedMemory(keyLease.Memory.Slice(0, keyLeaseLength));
                    var upsertResultTask =
                        session.BasicContext.UpsertAsync(fixedKey, input, default, token: token);
                    if (upsertResultTask.IsCompletedSuccessfully)
                    {
                        var upsertResult = upsertResultTask.Result;
                        status = upsertResult.Status;
                        if (status.IsPending)
                        {
                            upsertResult.Complete();
                        }
                    }
                    else
                    {
                        return await AwaitUpsertResultTask(upsertResultTask, keyLease, session, finalResult);
                    }

                    Debug.WriteLine($"Upsert (slide): {status}");
                }
            }
        }

        keyLease.Dispose();
        ReuseSession(session);
        return finalResult;
    }

    private async Task<TResult?> AwaitUpsertResultTask<TResult>(ValueTask<FasterKV<SpanByte, SpanByte>.UpsertAsyncResult<Input, Output, Empty>> upsertResultTask, IMemoryOwner<byte>? lease, ClientSession<SpanByte, SpanByte, Input, Output, Empty, CacheFunctions> session, TResult? finalResult)
    {
        var result = await upsertResultTask;
        var status = result.Status;
        if (status.IsPending)
        {
            result.Complete();
        }
        lease?.Dispose();
        ReuseSession(session);
        return finalResult;
    }

    private unsafe byte[]? Get(string key, bool getPayload)
    {
        var keySpan = WriteKey(key.Length <= MAX_STACKALLOC ? stackalloc byte[MAX_STACKALLOC] : default, key, out var lease);

        byte[]? finalResult = null;
        var session = GetSession();
        try
        {

            fixed (byte* keyPtr = keySpan)
            {
                var fixedKey = SpanByte.FromFixedSpan(keySpan);

                var input = new Input(getPayload ? Input.OperationFlags.ReadArray : Input.OperationFlags.None);
                Output output = default;
                var status = session.Read(ref fixedKey, ref input, ref output);
                Debug.WriteLine($"Read: {status}");
                finalResult = output.Payload;

                if (Slide(in output, ref input))
                {
                    SpanByte payload = default;
                    var upsert = session.BasicContext.Upsert(ref fixedKey, ref input, ref payload, ref output);
                    Debug.WriteLine($"Upsert (slide): {upsert}");
                }
                if (finalResult is not null)
                {
                    Debug.WriteLine("Read payload: " + BitConverter.ToString(finalResult));
                }
                session.CompletePending(true);
            }
            ReturnLease(lease);
            ReuseSession(session);
            return finalResult;
        }
        catch
        {
            FaultSession(session);
            throw;
        }
    }

    byte[]? IDistributedCache.Get(string key) => Get(key, getPayload: true);

    Task<byte[]?> IDistributedCache.GetAsync(string key, CancellationToken token) => GetAsync(key, bufferWriter: null, readArray: true, token: token, static x => x.Payload);

    unsafe void IDistributedCache.Refresh(string key) => Get(key, getPayload: false);

    Task IDistributedCache.RefreshAsync(string key, CancellationToken token) => GetAsync(key, bufferWriter: null, readArray: false, token: token, static _ => true);

    unsafe void IDistributedCache.Remove(string key)
    {
        var keySpan = WriteKey(key.Length <= MAX_STACKALLOC ? stackalloc byte[MAX_STACKALLOC] : default, key, out var lease);

        var session = GetSession();
        try
        {
            fixed (byte* ptr = keySpan)
            {
                var fixedKey = SpanByte.FromFixedSpan(keySpan);
                var result = session.Delete(ref fixedKey);
                Debug.WriteLine($"Delete: {result}");
                session.CompletePending(true);
            }
            ReturnLease(lease);
            ReuseSession(session);
        }
        catch
        {
            FaultSession(session);
            throw;
        }
    }

    async Task IDistributedCache.RemoveAsync(string key, CancellationToken token)
    {
        var keyMemory = WriteKey(key, out var lease);

        var session = GetSession();
        try
        {
            using (keyMemory.Pin())
            {
                var fixedKey = SpanByte.FromPinnedMemory(keyMemory);
                var result = await session.DeleteAsync(ref fixedKey, token: token);
                result.Complete();
                Debug.WriteLine($"Delete: {result.Status}");
                // await session.CompletePendingAsync(true, token: token);
            }
            ReturnLease(lease);
            ReuseSession(session);
        }
        catch
        {
            FaultSession(session);
            throw;
        }

    }

    private long NowTicks => base.Functions.NowTicks;

    private long GetExpiryTicks(DistributedCacheEntryOptions? options, out int sliding)
    {
        sliding = 0;
        if (options is not null)
        {
            if (options.SlidingExpiration is not null)
            {
                sliding = checked((int)options.SlidingExpiration.GetValueOrDefault().Ticks);
            }
            if (options.AbsoluteExpiration is not null)
            {
                return options.AbsoluteExpiration.GetValueOrDefault().DateTime.Ticks;
            }
            if (options.AbsoluteExpirationRelativeToNow is not null)
            {
                return NowTicks + options.AbsoluteExpirationRelativeToNow.GetValueOrDefault().Ticks;
            }
            if (sliding != 0)
            {
                return NowTicks + sliding;
            }
        }
        return NowTicks + OneMinuteTicks;
    }

    private static readonly long OneMinuteTicks = TimeSpan.FromMinutes(1).Ticks;

    unsafe void IDistributedCache.Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        var keySpan = WriteKey(key.Length <= MAX_STACKALLOC ? stackalloc byte[MAX_STACKALLOC] : default, key, out var keyLease);
        var valueSpan = WriteValue(value.Length <= MAX_STACKALLOC - 12 ? stackalloc byte[MAX_STACKALLOC] : default,
            value, out var valueLease, options);

        var session = GetSession();
        try
        {
            Debug.WriteLine("Write: " + BitConverter.ToString(valueSpan.Slice(12).ToArray()));
            fixed (byte* keyPtr = keySpan)
            fixed (byte* valuePtr = valueSpan)
            {
                var fixedKey = SpanByte.FromFixedSpan(keySpan);
                var fixedValue = SpanByte.FromFixedSpan(valueSpan);
                var result = session.Upsert(ref fixedKey, ref fixedValue);
                Debug.WriteLine($"Upsert: {result}");
                session.CompletePending(true);
            }
            ReturnLease(keyLease);
            ReturnLease(valueLease);
            ReuseSession(session);
        }
        catch
        {
            FaultSession(session);
            throw;
        }
    }

    Task IDistributedCache.SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token)
        => WriteAsync(key, new(value), options, token);

    async Task WriteAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token)
    {
        var keyMemory = WriteKey(key, out var keyLease);
        var valueMemory = WriteValue(value, out var valueLease, options);

        var session = GetSession();
        try
        {
            Debug.WriteLine("Write: " + BitConverter.ToString(valueMemory.Slice(12).ToArray()));
            using (keyMemory.Pin()) // TODO: better pinning
            using (valueMemory.Pin())
            {
                var fixedKey = SpanByte.FromPinnedMemory(keyMemory);
                var fixedValue = SpanByte.FromPinnedMemory(valueMemory);
                var upsertResult = await session.UpsertAsync(ref fixedKey, ref fixedValue, token: token);
                var status = upsertResult.Status;
                if (status.IsPending)
                {
                    status = upsertResult.Complete();
                }
                Debug.WriteLine($"Upsert: {status}");
                //await session.CompletePendingAsync(true, token: token);
            }
            ReturnLease(keyLease);
            ReturnLease(valueLease);
            ReuseSession(session);
        }
        catch
        {
            FaultSession(session);
            throw;
        }
    }

    Task<bool> IExperimentalBufferCache.GetAsync(string key, IBufferWriter<byte> target, CancellationToken token)
        => GetAsync(key, bufferWriter: target, readArray: false, token: token, selector: static x => true);

    Task IExperimentalBufferCache.SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token)
        => WriteAsync(key, value, options, token);
}
