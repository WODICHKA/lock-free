using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using PNServer.Core.Network;

namespace TestMemoryStreamV
{
    class Program
    {
        private static int nThreads = 6;
        private static int threadsPacketsCounts = 100000;
        private static int minPacketSize = 32;
        private static int max_89PercentPacketSize = 1024;
        private static int min_1PercentPacketSize = 16 * 1024;
        private static int min_10PercentPacketSize = 4096;
        private static int maxPacketSize = (32 * 1024) - 2;

        private static long streamSize = ((long)2 * 1024 * 1024 * 1024);

        private static int chunksSended = 0;
        private static int chunksReaded = 0;

        private static long totalBytesSended = 0;
        private static long totalBytesReaded = 0;

        private static int threadsStarted = 0;

        private static ConcurrentMemoryStream currentStream;
        private static ConcurrentQueue<byte[]> currentQueue;

        private unsafe static void makePacket(byte* _outBuffer, out int resultSize, Random rnd)
        {
            int randomValue = rnd.Next(0, 100);

            if (randomValue == 0)
                resultSize = rnd.Next(min_1PercentPacketSize, maxPacketSize);
            else if (randomValue < 10)
                resultSize = rnd.Next(min_10PercentPacketSize, maxPacketSize);
            else
                resultSize = rnd.Next(minPacketSize, max_89PercentPacketSize);

            Span<byte> tmpSpan = new Span<byte>((void*)(_outBuffer + 8), resultSize - 16);

            rnd.NextBytes(tmpSpan);

            byte* tmpHash = stackalloc byte[16];

            using (MD5 hash = MD5.Create())
            {
                if (!hash.TryComputeHash(tmpSpan, new Span<byte>(tmpHash, 16), out int bytesWritten))
                {
                    Console.WriteLine("error make hash? [make]");
                    Environment.Exit(-1);
                }
            }

            *(long*)(_outBuffer) = *(long*)(tmpHash);
            *(long*)(_outBuffer + (resultSize - 8)) = *(long*)(tmpHash + 8);
        }
        private unsafe static bool verifyPacket(byte* buffer, int size)
        {
            byte* trueHash = stackalloc byte[16];

            ReadOnlySpan<byte> hashPart = new ReadOnlySpan<byte>(buffer + 8, size - 16);

            using (MD5 hash = MD5.Create())
            {
                if (!hash.TryComputeHash(hashPart, new Span<byte>(trueHash, 16), out int bytesWritten))
                {
                    Console.WriteLine("error make hash? [verify]");
                    Environment.Exit(-1);
                }
            }

            long* trueHashPart1 = (long*)(trueHash);
            long* truhHashPart2 = (long*)(trueHash + 8);

            long* packetHashPart1 = (long*)(buffer);
            long* packetHashPart2 = (long*)(buffer + (size - 8));

            if ((*trueHashPart1 != *packetHashPart1) || (*truhHashPart2 != *packetHashPart2))
                return false;

            return true;
        }

        unsafe delegate void writeFunc(byte[] buff, int cb);
        unsafe delegate Span<byte> readFunc(out bool success, byte* ptr);

        private unsafe static void _sender(int packetsToSend, writeFunc wf)
        {
            byte* buffer = stackalloc byte[maxPacketSize];

            Random rnd = new Random((int)(Environment.TickCount + (int)DateTime.Now.Ticks));

            Interlocked.Increment(ref threadsStarted);
            SpinWait sw = new SpinWait();
            while (Volatile.Read(ref threadsStarted) != Volatile.Read(ref nThreads) + 1)
            { sw.SpinOnce(); }

            for (int i = 0; i < packetsToSend; ++i)
            {
                makePacket(buffer, out int packetsSize, rnd);
                // приходится делать .ToArray() из-за совместимости с очередью
                wf(new Span<byte>(buffer, packetsSize).ToArray(), packetsSize);

                Interlocked.Increment(ref chunksSended);
                Interlocked.Add(ref totalBytesSended, packetsSize);
            }
        }
        private unsafe static void _reader(int totalChunks, readFunc rf)
        {
            byte* buffer = stackalloc byte[maxPacketSize];

            Interlocked.Increment(ref threadsStarted);

            SpinWait sw = new SpinWait();
            int readed = 0;

            while (Volatile.Read(ref threadsStarted) != Volatile.Read(ref nThreads) + 1)
            { sw.SpinOnce(); }

            while (chunksReaded != totalChunks)
            {
                Span<byte> span = rf(out bool success, buffer);

                if (!success)
                {
                    sw.SpinOnce();
                    continue;
                }

                readed = span.Length;

                if (rf == readStream)
                {
                    if (!verifyPacket(buffer, readed))
                    {
                        Console.WriteLine("ERROR!");
                        Environment.Exit(-1);
                    }
                }
                else
                {
                    fixed (byte* bfRef = &MemoryMarshal.GetReference(span))
                        if (!verifyPacket(bfRef, readed))
                        {
                            Console.WriteLine("ERROR!");
                            Environment.Exit(-1);
                        }
                }
                chunksReaded++;
                totalBytesReaded += readed;
            }
        }

        private unsafe static void writeStream(byte[] buff, int cb)
        {
            fixed (byte* pinnedBuffer = buff)
                currentStream.Write(pinnedBuffer, cb);
        }
        private unsafe static void writeQueue(byte[] buff, int cb)
        {
            currentQueue.Enqueue(buff);
        }
        private unsafe static Span<byte> readStream(out bool success, byte* ptr)
        {
            int readed = currentStream.Read(ptr);

            if (readed == 0)
            {
                success = false;
                return default;
            }
            else
            {
                success = true;
                return new Span<byte>(ptr, readed);
            }
        }
        private unsafe static Span<byte> readQueue(out bool success, byte* ptr)
        {
            if (currentQueue.TryDequeue(out byte[] rslt))
            {
                success = true;
                return new Span<byte>(rslt);
            }
            else
            {
                success = false;
                return default;
            }
        }

        private static unsafe void refreshLocals()
        {
            chunksReaded = 0;
            chunksSended = 0;
            totalBytesReaded = 0;
            totalBytesSended = 0;
            threadsStarted = 0;
        }

        private unsafe static void Main(string[] args)
        {
            test_memStream();
            Console.WriteLine();
            test_queue();

            while (true)
                Console.ReadKey();
        }
        private unsafe static void test_queue()
        {
            currentQueue = new ConcurrentQueue<byte[]>();

            new Thread(() =>
            {
                Stopwatch sw = new Stopwatch();

                List<Thread> threads = new List<Thread>();

                for (int i = 0; i < nThreads; ++i)
                    threads.Add(new Thread(() => { _sender(threadsPacketsCounts, writeQueue); }));

                for (int i = 0; i < nThreads; ++i)
                    threads[i].Start();

                SpinWait spin = new SpinWait();

                DateTime start = DateTime.Now;

                while (Volatile.Read(ref chunksSended) != (threadsPacketsCounts * nThreads))
                { spin.SpinOnce(sleep1Threshold: -1); }

                DateTime end = DateTime.Now;
                TimeSpan result = end - start;

                Console.WriteLine("[Queue]: {0} packets sended for [{1} ms] total size is {2}",
                    chunksSended, result.TotalMilliseconds, totalBytesSended);
            }).Start();

            DateTime start = DateTime.Now;

            _reader(threadsPacketsCounts * nThreads, readQueue);

            DateTime end = DateTime.Now;
            TimeSpan result = end - start;

            Console.WriteLine("[Queue]: {0} packets readed for [{1} ms] total size is {2}",
                chunksReaded, result.TotalMilliseconds, totalBytesReaded);

            currentQueue = null;
            GC.Collect();
            refreshLocals();
        }
        private unsafe static void test_memStream()
        {
            currentStream = new ConcurrentMemoryStream((IntPtr)streamSize);

            new Thread(() =>
            {
                Stopwatch sw = new Stopwatch();

                List<Thread> threads = new List<Thread>();

                for (int i = 0; i < nThreads; ++i)
                    threads.Add(new Thread(() => { _sender(threadsPacketsCounts, writeStream); }));

                for (int i = 0; i < nThreads; ++i)
                    threads[i].Start();

                SpinWait spin = new SpinWait();

                DateTime start = DateTime.Now;

                while (Volatile.Read(ref chunksSended) != (threadsPacketsCounts * nThreads))
                { spin.SpinOnce(sleep1Threshold: -1); }

                DateTime end = DateTime.Now;
                TimeSpan result = end - start;

                Console.WriteLine("[Stream]: {0} packets sended for [{1} ms] total size is {2}",
                    chunksSended, result.TotalMilliseconds, totalBytesSended);
            }).Start();

            DateTime start = DateTime.Now;

            _reader(threadsPacketsCounts * nThreads, readStream);

            DateTime end = DateTime.Now;
            TimeSpan result = end - start;

            Console.WriteLine("[Stream]: {0} packets readed for [{1} ms] total size is {2}",
                chunksReaded, result.TotalMilliseconds, totalBytesReaded);

            currentStream = null;
            GC.Collect();
            refreshLocals();
        }
    }
}
