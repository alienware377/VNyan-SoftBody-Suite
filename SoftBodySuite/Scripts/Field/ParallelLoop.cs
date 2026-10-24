using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoftBodySuite.Field
{
    // Splits a per-node loop across CPU cores. Only for loops where every node writes its
    // OWN output slot and reads nothing another node writes in the same loop - then the
    // result is bit-identical to the plain loop, just faster. Small loops stay on one
    // thread: below a few thousand nodes the hand-off costs more than it saves.
    public static class ParallelLoop
    {
        public static bool Enabled = true;
        // persistent helper threads instead of a Parallel.For per call (see LoopPool)
        public static bool UsePool = true;
        const int MinNodes = 4096;
        static readonly int Chunks = Math.Max(1, Math.Min(Environment.ProcessorCount, 16));
        static readonly int PoolChunks = Math.Max(1, Math.Min(Environment.ProcessorCount, 9) * 4);

        // perf probe: how many loops really went wide this window (read and reset by the
        // plugin's perf line). Interlocked because the field solve runs off the main thread.
        public static int ParCalls;
        public static int SerialCalls;
        public static int PoolCalls;

        // One-off: the bare cost of a hand-off with an empty body, in microseconds.
        public static double MeasureDispatchUs(int reps)
        {
            if (Chunks <= 1 || reps <= 0) return 0;
            Action<int, int> nop = delegate (int a, int b) { };
            For(Chunks * 64, 1, nop);   // warm the pool
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) For(Chunks * 64, 1, nop);
            return sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
        }

        public static void For(int n, Action<int, int> body) { For(n, MinNodes, body); }

        // minN: below this many items the loop stays on one thread. The clothes pass uses a
        // lower bar than the field: its per-item work (a triangle frame, a neighbour average
        // of 6) is heavier than one blur tap, and garments have 2-5k groups, not 30k nodes.
        public static void For(int n, int minN, Action<int, int> body)
        {
            // inside a pool chunk (a garment's own chain): its loops stay on this thread.
            // Every index is independent, so one call over [0, n) is the same result.
            if (!Enabled || n < minN || Chunks <= 1 || LoopPool.InChunk) { Interlocked.Increment(ref SerialCalls); body(0, n); return; }
            Interlocked.Increment(ref ParCalls);
            // Same chunk boundaries either way, and every index writes only its own slot,
            // so which thread runs a chunk never changes a single bit of the result.
            // The pool cuts finer than the helper count: the 14900K mixes fast and slow
            // cores, and with one chunk per thread every loop waited on the slowest one.
            // Indices are independent, so where the cuts fall changes nothing.
            if (UsePool && LoopPool.TryRun(n, PoolChunks, body)) { Interlocked.Increment(ref PoolCalls); return; }
            int chunks = Chunks;
            Parallel.For(0, chunks, delegate (int c)
            {
                int a = (int)((long)n * c / chunks), b = (int)((long)n * (c + 1) / chunks);
                if (b > a) body(a, b);
            });
        }
    }

    // A fixed set of background threads that stay ready between loops. Parallel.For set
    // up its tasks, range manager and wait handles on every call (~2 KB of garbage and a
    // thread-pool wake each), and the cloth pass alone makes ~100 such calls a frame.
    //
    // One job at a time: whoever claims `busy` owns the pool; a second caller (the field's
    // async solve, or a loop nested inside a chunk) simply uses Parallel.For as before.
    // The caller runs chunks too, so a job always finishes even if no helper wakes up.
    static class LoopPool
    {
        static Thread[] workers;
        static readonly object initLock = new object();
        static int busy;                     // 1 while a job is in flight
        static long ticket;                  // (job generation << 32) | next chunk index
        static int gen;                      // written only by the pool's current owner
        static Action<int, int> jBody;       // job fields: stable while any chunk is pending
        static int jN, jChunks;
        static int pending;                  // chunks of the current job not finished yet
        static Exception jErr;
        static readonly ManualResetEventSlim wake = new ManualResetEventSlim(false, 0);
        static readonly ManualResetEventSlim done = new ManualResetEventSlim(false, 0);
        [ThreadStatic] static bool isWorker;
        [ThreadStatic] static int inChunk;
        public static bool InChunk { get { return inChunk > 0; } }
        // how long an idle helper keeps watching for the next job before it sleeps: the
        // loops come in bursts (9 seam passes, 10 smoothing passes back to back)
        static readonly long spinTicks = System.Diagnostics.Stopwatch.Frequency / 50000;   // 20 us
        // helpers beyond this many sleep straight away between jobs instead of watching:
        // a crowd of spinners steals the main thread's hyperthread twin and boost clock
        const int Watchers = 4;
        const int MaxWorkers = 8;
        static bool failed;

        public static bool TryRun(int n, int chunks, Action<int, int> body)
        {
            if (isWorker || failed) return false;
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
            try
            {
                if (!EnsureStarted(chunks - 1)) return false;
                jBody = body; jN = n; jChunks = chunks; jErr = null;
                Volatile.Write(ref pending, chunks);
                done.Reset();
                int g = gen + 1; if (g <= 0) g = 1; gen = g;
                Interlocked.Exchange(ref ticket, (long)g << 32);   // publish (full fence)
                wake.Set();
                RunChunks(g);                                      // the caller works too
                // every chunk is claimed now: idle helpers may go back to sleep instead of
                // spinning through the tail of the job (stragglers are already running)
                wake.Reset();
                if (Volatile.Read(ref pending) > 0)
                {
                    // the last chunks are usually a few microseconds away
                    long until = System.Diagnostics.Stopwatch.GetTimestamp() + spinTicks;
                    while (Volatile.Read(ref pending) > 0 && System.Diagnostics.Stopwatch.GetTimestamp() < until)
                        Thread.SpinWait(20);
                    while (Volatile.Read(ref pending) > 0) { done.Wait(); done.Reset(); }
                }
                Exception e = jErr;
                jBody = null; jErr = null;
                if (e != null) throw new AggregateException(e);
                return true;
            }
            finally { wake.Reset(); Volatile.Write(ref busy, 0); }
        }

        static void RunChunks(int g)
        {
            while (true)
            {
                long t = Volatile.Read(ref ticket);
                if ((int)(t >> 32) != g) return;
                int c = (int)(t & 0xffffffffL);
                if (c >= Volatile.Read(ref jChunks)) return;
                if (Interlocked.CompareExchange(ref ticket, t + 1, t) != t) continue;
                // chunk c of job g is ours; the job cannot change until it is counted off
                int n = jN, chunks = jChunks;
                Action<int, int> body = jBody;
                int a = (int)((long)n * c / chunks), b = (int)((long)n * (c + 1) / chunks);
                inChunk++;
                try { if (b > a && body != null) body(a, b); }
                catch (Exception ex) { Interlocked.CompareExchange(ref jErr, ex, null); }
                finally
                {
                    inChunk--;
                    if (Interlocked.Decrement(ref pending) == 0) done.Set();
                }
            }
        }

        static void WorkerLoop(object idxObj)
        {
            isWorker = true;
            bool watcher = (int)idxObj < Watchers;
            int seen = (int)(Volatile.Read(ref ticket) >> 32);
            while (true)
            {
                try
                {
                    long t = Volatile.Read(ref ticket);
                    int g = (int)(t >> 32);
                    if (g != seen) { seen = g; RunChunks(g); continue; }
                    // idle: watch briefly for the next job of a burst, then sleep
                    long until = watcher ? System.Diagnostics.Stopwatch.GetTimestamp() + spinTicks : 0;
                    bool got = false;
                    while (watcher && System.Diagnostics.Stopwatch.GetTimestamp() < until)
                    {
                        if ((int)(Volatile.Read(ref ticket) >> 32) != seen) { got = true; break; }
                        Thread.SpinWait(20);
                    }
                    if (!got) wake.Wait();
                }
                catch (ThreadAbortException) { return; }
                catch (Exception) { }
            }
        }

        static bool EnsureStarted(int count)
        {
            if (workers != null) return true;
            lock (initLock)
            {
                if (workers != null) return true;
                try
                {
                    Thread[] ws = new Thread[Math.Max(1, Math.Min(count, MaxWorkers))];
                    for (int i = 0; i < ws.Length; i++)
                    {
                        ws[i] = new Thread(WorkerLoop);
                        ws[i].IsBackground = true;
                        ws[i].Name = "SoftFleshField loop " + i;
                        ws[i].Start(i);
                    }
                    workers = ws;
                    return true;
                }
                catch (Exception) { failed = true; return false; }
            }
        }
    }
}
