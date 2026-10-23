using System.Collections.Generic;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    [DefOf]
    public static class RWDefOf
    {
        public static ThingDef RW_SealedCargo;
        public static ThingDef RW_Reception;
        public static ThingDef RW_StorageRack;
        public static ThingDef RW_Vault;

        static RWDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RWDefOf));
    }

    public class CompProperties_SealedCargo : CompProperties
    {
        public CompProperties_SealedCargo() => compClass = typeof(CompSealedCargo);
    }

    /// <summary>
    /// Links a crate Thing to its <see cref="CargoRecord"/>. The record is the source of truth; the Thing only carries the ID.
    /// </summary>
    public class CompSealedCargo : ThingComp
    {
        public string cargoId;

        /// <summary>Set by the warehouse before it destroys the crate on purpose, so the loss handler ignores it.</summary>
        [Unsaved] public bool removedByWarehouse;

        public CargoRecord Record
        {
            get
            {
                var world = WarehouseWorldComponent.Instance;
                if (world == null || cargoId == null) return null;
                world.State.Cargo.TryGetValue(cargoId, out var record);
                return record;
            }
        }

        public bool IsContractCrate => Record?.ContractId != null;

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref cargoId, "cargoId");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) WarehouseWorldComponent.Instance?.RegisterCrate(cargoId, parent);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            var world = WarehouseWorldComponent.Instance;
            if (world == null) return;
            world.RegisterCrate(cargoId, parent);
            world.Notify_CrateSpawned(cargoId, parent, parent.Map);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            WarehouseWorldComponent.Instance?.Notify_CrateDespawned(cargoId, parent);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            var world = WarehouseWorldComponent.Instance;
            if (world == null) return;
            world.UnregisterCrate(cargoId, parent);
            if (!removedByWarehouse) world.Notify_CrateDestroyed(cargoId);
        }

        public override bool AllowStackWith(Thing other) => false;

        public override float GetStatOffset(StatDef stat)
        {
            var record = Record;
            if (record == null) return 0f;
            if (stat == StatDefOf.MarketValue) return (float)WealthAdapter.CargoValue(record);
            if (stat == StatDefOf.Mass) return Mathf.Max(0f, (float)record.MassKg - parent.def.GetStatValueAbstract(StatDefOf.Mass));
            return 0f;
        }

        public override string TransformLabel(string label)
        {
            var record = Record;
            if (record == null) return label;
            var owner = Ids.Faction(record.Stolen ? record.OriginalOwnerFactionId : record.OwnerFactionId);
            return owner == null ? label : "RW_CrateLabel".Translate(label, owner.Name).Resolve();
        }

        public override string CompInspectStringExtra()
        {
            var record = Record;
            if (record == null) return "RW_CrateUnregistered".Translate().Resolve();
            var sb = new StringBuilder();
            sb.Append("RW_CrateValue".Translate(record.DeclaredValue.ToString("0")));
            sb.AppendLine();
            sb.Append("RW_CrateMass".Translate(record.MassKg.ToString("0.#")));
            if (record.ContractId != null)
            {
                sb.AppendLine();
                sb.Append("RW_CrateContract".Translate(record.ContractId));
                sb.AppendLine();
                sb.Append(StorageAdapter.IsValidlyStored(parent, record)
                    ? "RW_CrateStored".Translate()
                    : "RW_CrateNotStored".Translate(StorageAdapter.FeeStopReason(parent, record).Translate()));
            }
            else if (record.Stolen)
            {
                sb.AppendLine();
                sb.Append("RW_CrateStolen".Translate());
            }
            return sb.ToString();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            var record = Record;
            if (record == null || record.Opened || !parent.Spawned) yield break;
            if (record.ContractId != null && record.Custody == CargoCustody.Depositor)
            {
                yield return new Command_Action
                {
                    defaultLabel = "RW_Embezzle".Translate(),
                    defaultDesc = "RW_EmbezzleDesc".Translate(),
                    icon = TexCommand.ForbidOff,
                    action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "RW_EmbezzleConfirm".Translate(record.DeclaredValue.ToString("0")),
                        () => WarehouseWorldComponent.Instance?.Embezzle(cargoId), destructive: true)),
                };
            }
            else if (record.Custody == CargoCustody.Player)
            {
                yield return new Command_Action
                {
                    defaultLabel = "RW_OpenCrate".Translate(),
                    defaultDesc = "RW_OpenCrateDesc".Translate(),
                    icon = TexCommand.ForbidOff,
                    action = () => WarehouseWorldComponent.Instance?.OpenStolen(cargoId),
                };
            }
        }
    }

    /// <summary>Creates crate Things and their contents from Core records.</summary>
    public static class CargoAdapter
    {
        public static Thing MakeCrate(CargoRecord record)
        {
            var crate = ThingMaker.MakeThing(RWDefOf.RW_SealedCargo);
            var comp = crate.TryGetComp<CompSealedCargo>();
            comp.cargoId = record.CargoId;
            crate.HitPoints = Mathf.Clamp(record.HitPoints, 1, crate.MaxHitPoints);
            if (record.OwnerFactionId != CargoService.PlayerFactionId) crate.SetForbidden(false, warnOnFail: false);
            return crate;
        }

        public static CompSealedCargo Comp(Thing thing) => thing?.TryGetComp<CompSealedCargo>();

        /// <summary>Makes the manifest's Things. Missing defs are skipped and reported; nothing is invented to replace them.</summary>
        public static List<Thing> MakeContents(CargoRecord record, out List<string> missingDefs)
        {
            var things = new List<Thing>();
            missingDefs = new List<string>();
            foreach (var entry in record.Manifest)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(entry.ThingDefName);
                ThingDef stuff = entry.StuffDefName == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(entry.StuffDefName);
                if (def == null || (entry.StuffDefName != null && stuff == null))
                {
                    missingDefs.Add(entry.ThingDefName);
                    continue;
                }
                int remaining = entry.Count;
                while (remaining > 0)
                {
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
                    int stack = Mathf.Min(remaining, def.stackLimit);
                    thing.stackCount = stack;
                    if (entry.Quality.HasValue) thing.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)entry.Quality.Value, ArtGenerationContext.Outsider);
                    things.Add(thing);
                    remaining -= stack;
                }
            }
            return things;
        }

        /// <summary>Destroys a crate on the warehouse's behalf (return, removal, opening) without triggering loss handling.</summary>
        public static void RemoveCrate(Thing crate)
        {
            var comp = Comp(crate);
            if (comp != null) comp.removedByWarehouse = true;
            if (!crate.Destroyed) crate.Destroy(DestroyMode.Vanish);
        }
    }
}
