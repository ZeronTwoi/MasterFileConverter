namespace FileConverter.Core;

/// <summary>How much of the machine one job occupies — decides which queue it waits in.</summary>
public enum JobWeight
{
    /// <summary>Roughly one core: audio encodes, stream copies, documents, GIFs.</summary>
    Light,

    /// <summary>One core but a whole decoded image in memory.</summary>
    Image,

    /// <summary>Disk-bound, and 7-Zip/WinRAR bring their own threads.</summary>
    Archive,

    /// <summary>Several cores but happy to share: GPU video encodes, frame extraction, merges.</summary>
    Heavy,

    /// <summary>A CPU video encode: needs every core and runs with nothing else beside it.</summary>
    Exclusive
}

/// <summary>
/// Admission control for a batch. Most conversions are single-threaded inside (an mp3 encode,
/// a png write), so the way to use a many-core CPU is to run one of them per core.
///
/// CPU video encoders are the opposite: one already spreads over every core, so company only
/// costs. Measured over minute-long x264 encodes on a 24-thread laptop: one at a time 42 frames/s,
/// two side by side 34 in total, three 32; and next to a batch of single-threaded jobs the encode
/// lost more than the batch gained. Those encodes therefore run alone
/// (<see cref="JobWeight.Exclusive"/>): running jobs finish, the encode gets the machine, then
/// the queue carries on. GPU encodes do scale (218 → 262 → 283 frames/s for one, two, three)
/// and share the machine like everything else.
/// </summary>
public sealed class WorkGates
{
    private readonly SemaphoreSlim _light;
    private readonly SemaphoreSlim _image;
    private readonly SemaphoreSlim _archive;
    private readonly SemaphoreSlim _heavy;
    private readonly SharedExclusiveLock _machine = new();

    public WorkGates()
    {
        int cores = Environment.ProcessorCount;
        double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;

        _light = new SemaphoreSlim(Math.Clamp(cores, 2, 32));
        // a decoded 24 MP photo is ~200 MB at 16 bits per channel — don't let a batch outgrow the RAM
        _image = new SemaphoreSlim(Math.Clamp(Math.Min(cores, (int)(ramGb * 1.5)), 2, 32));
        _archive = new SemaphoreSlim(2);
        _heavy = new SemaphoreSlim(2);
    }

    /// <summary>Waits for a slot of the given weight; dispose the result to give it back.</summary>
    public async Task<IDisposable> EnterAsync(JobWeight weight, CancellationToken ct)
    {
        if (weight == JobWeight.Exclusive)
            return await _machine.EnterAsync(exclusive: true, ct);

        var gate = weight switch
        {
            JobWeight.Heavy => _heavy,
            JobWeight.Image => _image,
            JobWeight.Archive => _archive,
            _ => _light
        };
        await gate.WaitAsync(ct);
        try
        {
            var shared = await _machine.EnterAsync(exclusive: false, ct);
            return new Slot(gate, shared);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Slot(SemaphoreSlim gate, IDisposable shared) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            shared.Dispose();
            gate.Release();
        }
    }

    /// <summary>
    /// Async reader/writer lock, first come first served: any number of ordinary jobs share the
    /// machine, an exclusive job waits for them to finish and holds it alone. Ordinary jobs that
    /// arrive while an exclusive one is waiting queue up behind it, so it can't be starved.
    /// </summary>
    private sealed class SharedExclusiveLock
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _queue = new();
        private int _shared;
        private bool _exclusive;

        private sealed class Waiter(bool exclusive)
        {
            public bool Exclusive { get; } = exclusive;
            public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<IDisposable> EnterAsync(bool exclusive, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            Waiter? waiter = null;
            lock (_sync)
            {
                if (_queue.Count == 0 && CanEnter(exclusive))
                    Take(exclusive);
                else
                    _queue.AddLast(waiter = new Waiter(exclusive));
            }

            if (waiter != null)
            {
                await using var registration = ct.Register(() => Abandon(waiter, ct));
                await waiter.Granted.Task;
            }
            return new Holder(this, exclusive);
        }

        private bool CanEnter(bool exclusive) => exclusive ? !_exclusive && _shared == 0 : !_exclusive;

        private void Take(bool exclusive)
        {
            if (exclusive) _exclusive = true;
            else _shared++;
        }

        /// <summary>Admits waiters from the front of the queue for as long as they are compatible.</summary>
        private void Admit()
        {
            while (_queue.First is { } node && CanEnter(node.Value.Exclusive))
            {
                _queue.RemoveFirst();
                Take(node.Value.Exclusive);
                node.Value.Granted.TrySetResult();
            }
        }

        private void Abandon(Waiter waiter, CancellationToken ct)
        {
            lock (_sync)
            {
                // already admitted: the caller owns the lock and will release it normally
                if (!_queue.Remove(waiter)) return;
                waiter.Granted.TrySetCanceled(ct);
                Admit(); // whoever was queued behind it may be able to go now
            }
        }

        private void Release(bool exclusive)
        {
            lock (_sync)
            {
                if (exclusive) _exclusive = false;
                else _shared--;
                Admit();
            }
        }

        private sealed class Holder(SharedExclusiveLock owner, bool exclusive) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(exclusive);
            }
        }
    }
}
