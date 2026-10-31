using System;
using System.Collections.Generic;

namespace RentalWarehouse.Core
{
    /// <summary>Hand off a snapshot of staged objects, retaining the actual remainder even when a native operation throws.</summary>
    public static class TransferRecovery
    {
        public static bool Run<T>(IReadOnlyList<T> items, Action<T> detach, Action<T> handoff, Func<T, bool> transferred, Action<T> retain)
        {
            bool complete = true;
            foreach (var item in items)
            {
                try { detach(item); handoff(item); }
                finally
                {
                    if (!transferred(item))
                    {
                        retain(item);
                        complete = false;
                    }
                }
            }
            return complete;
        }
    }
}
