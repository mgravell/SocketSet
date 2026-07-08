using System.Diagnostics;
using System.Net;

namespace FastNet;

public partial class SocketSet
{
    protected abstract class SocketSetShard
    {
        public int Index => _index;

        public SocketSet Set => _set;

        private int _index = -1;
        private SocketSet _set = null!; // set by init

        public void Init(SocketSet socketSet, int index)
        {
            if (Interlocked.CompareExchange(ref _index, index, -1) is not -1)
                Throw(); // don't get cute by trying to reuse these
            _set = socketSet;
            OnInit();
            static void Throw() => throw new InvalidOperationException();
        }

        protected virtual void OnInit()
        {
        }

        ~SocketSetShard()
        {
            GC.SuppressFinalize(this);
            OnDispose(false);
        }

        public void Stop()
        {
            if (TrySetFlag(FLAG_STOPPED, true))
            {
                // the actual stop is co-operative
                OnWake();
            }
        }

        protected virtual void OnWake()
        {
        }

        internal void Dispose()
        {
            // try to move to complete
            var old = _flags;
            switch (old)
            {
                case 0: // nothing happened
                case FLAG_STOPPED: // stopped only
                    Interlocked.CompareExchange(ref _flags, FLAG_COMPLETE | FLAG_STOPPED, old);
                    break;
            }
            OnDispose(true);
        }

        protected virtual void OnDispose(bool disposing)
        {
        }

        [ThreadStatic] private static SocketSetShard? _owned;

        private const int FLAG_STOPPED = 1, FLAG_ACTIVE = 2, FLAG_COMPLETE = 4;
        private volatile int _flags;

        internal bool IsCurrent => ReferenceEquals(_owned, this);

        protected bool IsStopped => (_flags & FLAG_STOPPED) != 0;
        protected bool IsComplete => (_flags & FLAG_COMPLETE) != 0;

        private bool TrySetFlag(int flag, bool value)
        {
            var oldVal = _flags;
            while (true)
            {
                var newVal = value ? oldVal | flag : oldVal & ~flag;
                if (newVal == oldVal) return false; // no change

                var observed = Interlocked.CompareExchange(ref _flags, newVal, oldVal);
                if (observed == oldVal) return true; // made a change
                oldVal = observed;
            }
        }

        internal void Run()
        {
            if (_owned is not null) return; // re-entrant somehow?
            if (Interlocked.CompareExchange(ref _flags, FLAG_ACTIVE, 0) is not 0) return;
            try
            {
                if (IsStopped) return;
                _owned = this;
                OnRun();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                TrySetFlag(FLAG_ACTIVE, false);
                TrySetFlag(FLAG_COMPLETE | FLAG_STOPPED, true);
                _owned = null;
            }
        }

        protected internal abstract void OnRun();

        public virtual void Listen(EndPoint endpoint) => throw new NotSupportedException();
    }
}