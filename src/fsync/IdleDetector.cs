using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace fsync
{
    class IdleDetector
    {
        public void Start()
        {
            if (Timeout == TimeSpan.Zero)
                throw new Exception();
            Timer = new Timer(Timer_callback);
        }
        public TimeSpan Timeout { get; set; }
        //DateTime LastPing;
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Ping()
        {
            //LastPing = DateTime.Now;
            Timer.Change(Timeout, TimeSpan.FromMilliseconds(-1));
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void Timer_callback(object state)
        {
            if (Idle != null)
                Idle();
        }

        public Action Idle;
        private Timer Timer;

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Stop()
        {
            Timer.Change(-1, -1);
        }
    }

}
