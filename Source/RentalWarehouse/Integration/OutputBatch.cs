using System;
using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Physical staging area saved with the world, separate from the JSON ledger. Prepared items are made once;
    /// partial placement and failed handoffs return the original remainder here rather than recreating a manifest.
    /// </summary>
    public sealed class OutputBatch : IExposable, IThingHolder
    {
        public ThingOwner<Thing> Contents;
        public bool Prepared;
        public bool Committed;
        public bool Refunding;
        public string MapId;
        public IntVec3 Cell;
        public OutputBatch() => Contents = new ThingOwner<Thing>(this, false, LookMode.Deep);
        public IThingHolder ParentHolder => null;
        public ThingOwner GetDirectlyHeldThings() => Contents;
        public void GetChildHolders(List<IThingHolder> children) => ThingOwnerUtility.AppendThingHoldersFromThings(children, Contents);
        public void ExposeData()
        {
            Scribe_Deep.Look(ref Contents, "contents", this);
            Scribe_Values.Look(ref Prepared, "prepared");
            Scribe_Values.Look(ref Committed, "committed");
            Scribe_Values.Look(ref Refunding, "refunding");
            Scribe_Values.Look(ref MapId, "mapId");
            Scribe_Values.Look(ref Cell, "cell");
        }

        public void Prepare(Func<List<Thing>> make)
        {
            if (Prepared) return;
            var items = make(); // no world mutation until the complete manifest has been made
            foreach (var item in items) Contents.TryAdd(item, canMergeWithExistingStacks: false);
            Prepared = true;
        }

        public bool Transfer(Action<Thing> handoff)
        {
            TransferRecovery.Run(Contents.ToArray(), item => Contents.Remove(item), handoff,
                item => item.Destroyed || item.Spawned || item.MapHeld != null,
                item =>
                {
                    if (Contents.Contains(item)) return;
                    item.holdingOwner?.Remove(item);
                    if (!Contents.TryAdd(item, canMergeWithExistingStacks: false))
                        throw new InvalidOperationException("Cannot retain staged output " + item);
                });
            return Contents.Count == 0;
        }
    }
}
