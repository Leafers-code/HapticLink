using System;

namespace HapticLink.Sources
{
    /// <summary>
    /// A trigger source. Every source publishes a raw 0..1 value; shaping, smoothing
    /// and mixing happen centrally in the Engine so all sources behave consistently.
    /// </summary>
    public abstract class SourceBase : IDisposable
    {
        public abstract string Name { get; }

        private volatile int _rawBits;
        /// <summary>Latest raw value, 0..1. Written by the source's own thread, read by the engine.</summary>
        public double Raw
        {
            get => BitConverter.Int32BitsToSingle(_rawBits);
            protected set => _rawBits = BitConverter.SingleToInt32Bits((float)value);
        }

        /// <summary>False when a prerequisite (driver, device, port) is missing.</summary>
        public bool Available { get; protected set; } = true;

        /// <summary>Human-readable state for the UI — why it is or isn't working.</summary>
        public string Status { get; protected set; } = "Idle";

        public bool Running { get; protected set; }

        public abstract void Start();
        public abstract void Stop();

        /// <summary>Called by the engine each output tick, for sources that generate rather than listen.</summary>
        public virtual void Tick(double dtMs) { }

        public virtual void Dispose() => Stop();
    }
}
