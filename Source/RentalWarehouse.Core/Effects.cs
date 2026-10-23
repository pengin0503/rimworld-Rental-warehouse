using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentalWarehouse.Core
{
    public enum EffectRecordState
    {
        Pending,
        Completed,
        Abandoned,
    }

    public sealed class EffectRecord
    {
        public EffectRecordState State;
        public long FirstTick;
        public long LastTick;
        public int Attempts;
        public int Failures;
    }

    /// <summary>
    /// Remembers which side effects ran (spec 9). An effect is marked complete only after its handler reports success,
    /// so a crash between "begin" and "complete" leads to a retry, and handlers reconcile against the world instead of
    /// trusting a flag set in advance.
    /// </summary>
    public sealed class EffectLedger
    {
        public Dictionary<string, EffectRecord> Records = new Dictionary<string, EffectRecord>();

        public bool TryBegin(string effectId, long now)
        {
            if (Records.TryGetValue(effectId, out var r))
            {
                if (r.State != EffectRecordState.Pending) return false;
            }
            else
            {
                r = new EffectRecord { State = EffectRecordState.Pending, FirstTick = now };
                Records[effectId] = r;
            }
            r.Attempts++;
            r.LastTick = now;
            return true;
        }

        public void Complete(string effectId, long now) => Mark(effectId, EffectRecordState.Completed, now);

        public void Abandon(string effectId, long now) => Mark(effectId, EffectRecordState.Abandoned, now);

        public int Fail(string effectId, long now)
        {
            if (!Records.TryGetValue(effectId, out var r)) return 0;
            r.Failures++;
            r.LastTick = now;
            return r.Failures;
        }

        private void Mark(string effectId, EffectRecordState state, long now)
        {
            if (!Records.TryGetValue(effectId, out var r))
            {
                r = new EffectRecord { FirstTick = now };
                Records[effectId] = r;
            }
            r.State = state;
            r.LastTick = now;
        }

        public bool IsCompleted(string effectId) => Records.TryGetValue(effectId, out var r) && r.State == EffectRecordState.Completed;

        public bool IsAbandoned(string effectId) => Records.TryGetValue(effectId, out var r) && r.State == EffectRecordState.Abandoned;

        public bool IsDone(string effectId) => Records.TryGetValue(effectId, out var r) && r.State != EffectRecordState.Pending;

        public int Attempts(string effectId) => Records.TryGetValue(effectId, out var r) ? r.Attempts : 0;

        /// <summary>Drops finished records whose IDs start with one of the prefixes (closed contracts and sites).</summary>
        public int Prune(Func<string, bool> isObsolete)
        {
            var remove = new List<string>();
            foreach (var kv in Records)
            {
                if (kv.Value.State != EffectRecordState.Pending && isObsolete(kv.Key)) remove.Add(kv.Key);
            }
            foreach (var id in remove) Records.Remove(id);
            return remove.Count;
        }
    }

    /// <summary>Persistent queue of requested side effects. Lives in the saved WorldState, so requests survive saving.</summary>
    public static class EffectQueue
    {
        public const int MaxAttempts = 5;

        public static void Enqueue(WorldState world, EffectRequest effect)
        {
            if (world.Effects.IsDone(effect.EffectId)) return;
            foreach (var e in world.PendingEffects)
            {
                if (e.EffectId == effect.EffectId) return;
            }
            world.PendingEffects.Add(effect);
        }

        public static void EnqueueAll(WorldState world, IEnumerable<EffectRequest> effects)
        {
            foreach (var e in effects) Enqueue(world, e);
        }

        /// <summary>
        /// Applies pending effects in order. A handler returns true when the effect now exists in the game (including
        /// when it finds it already existed), false to retry later. Exceptions count as failures; after
        /// <see cref="MaxAttempts"/> failures the effect is abandoned and reported instead of looping forever.
        /// </summary>
        public static int Process(WorldState world, Func<EffectRequest, bool> apply, Action<string>? onAbandoned = null, long now = 0)
        {
            // A handler that triggers processing again only enqueues; the outer pass picks the new effects up.
            if (world.ProcessingEffects) return 0;
            world.ProcessingEffects = true;
            try
            {
                return ProcessPending(world, apply, onAbandoned, now);
            }
            finally
            {
                world.ProcessingEffects = false;
            }
        }

        private static int ProcessPending(WorldState world, Func<EffectRequest, bool> apply, Action<string>? onAbandoned, long now)
        {
            int applied = 0;
            int i = 0;
            while (i < world.PendingEffects.Count)
            {
                var effect = world.PendingEffects[i];
                if (!world.Effects.TryBegin(effect.EffectId, now))
                {
                    world.PendingEffects.RemoveAt(i);
                    continue;
                }
                bool done;
                try
                {
                    done = apply(effect);
                }
                catch (Exception)
                {
                    if (world.Effects.Fail(effect.EffectId, now) >= MaxAttempts)
                    {
                        world.Effects.Abandon(effect.EffectId, now);
                        world.PendingEffects.RemoveAt(i);
                        onAbandoned?.Invoke(effect.EffectId);
                    }
                    else
                    {
                        i++;
                    }
                    continue;
                }
                if (done)
                {
                    world.Effects.Complete(effect.EffectId, now);
                    world.PendingEffects.RemoveAt(i);
                    applied++;
                }
                else
                {
                    i++;
                }
            }
            return applied;
        }
    }

    public sealed class SavedState
    {
        public int SchemaVersion;
        public string Payload = "";
    }

    public sealed class StateCodecException : Exception
    {
        public StateCodecException(string reasonKey, string message, Exception? inner = null) : base(message, inner)
        {
            ReasonKey = reasonKey;
        }

        public string ReasonKey { get; }
    }

    /// <summary>Serialises the whole ledger as JSON. The game stores the string in the save file.</summary>
    public static class StateCodec
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            IgnoreReadOnlyProperties = true,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
        };

        public static SavedState Export(WorldState world) => new SavedState
        {
            SchemaVersion = world.SchemaVersion,
            Payload = JsonSerializer.Serialize(world, Options),
        };

        public static WorldState Import(SavedState saved)
        {
            if (string.IsNullOrWhiteSpace(saved.Payload)) return new WorldState();
            if (saved.SchemaVersion > WorldState.CurrentSchemaVersion)
            {
                throw new StateCodecException("RW_Diag_LedgerFromNewerVersion", "ledger schema " + saved.SchemaVersion + " is newer than " + WorldState.CurrentSchemaVersion);
            }
            try
            {
                return JsonSerializer.Deserialize<WorldState>(saved.Payload, Options)
                    ?? throw new StateCodecException("RW_Diag_LedgerUnreadable", "ledger payload is null");
            }
            catch (JsonException e)
            {
                throw new StateCodecException("RW_Diag_LedgerUnreadable", "ledger payload is not valid JSON: " + e.Message, e);
            }
        }
    }
}
