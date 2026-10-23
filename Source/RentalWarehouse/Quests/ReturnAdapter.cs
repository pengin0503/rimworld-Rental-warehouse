using System.Collections.Generic;
using System.Linq;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Matches crates whose contract is in its return phase; racks refuse them and return pods accept only them.</summary>
    public class SpecialThingFilterWorker_ReturningCargo : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => ReturnAdapter.IsReturning(t);

        public override bool CanEverMatch(ThingDef def) => def == RWDefOf.RW_SealedCargo;
    }

    public class SpecialThingFilterWorker_NotReturningCargo : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => t.def == RWDefOf.RW_SealedCargo && !ReturnAdapter.IsReturning(t);

        public override bool CanEverMatch(ThingDef def) => def == RWDefOf.RW_SealedCargo;
    }

    /// <summary>
    /// A contract's return pod. Haulers load returning crates into it like any storage. Only the "send" command returns
    /// them (spec 5: loading alone is not a return). If it is destroyed, a new one can be requested; the deadline stays.
    /// </summary>
    public class Building_ReturnPod : Building_Storage
    {
        public string contractId;
        public string podId;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref contractId, "contractId");
            Scribe_Values.Look(ref podId, "podId");
        }

        public ContractState Contract => WarehouseWorldComponent.Instance?.State.Contract(contractId);

        public List<Thing> LoadedCrates()
        {
            var list = new List<Thing>();
            if (!Spawned) return list;
            foreach (var cell in AllSlotCellsList())
            {
                foreach (var t in cell.GetThingList(Map))
                {
                    if (CargoAdapter.Comp(t)?.Record?.ContractId == contractId) list.Add(t);
                }
            }
            return list;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            var loaded = LoadedCrates();
            var send = new Command_Action
            {
                defaultLabel = "RW_SendReturnPod".Translate(),
                defaultDesc = "RW_SendReturnPodDesc".Translate(),
                icon = TexCommand.Install,
                action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "RW_SendReturnPodConfirm".Translate(loaded.Count, ReturnAdapter.Remaining(Contract)), () => ReturnAdapter.Send(this))),
            };
            if (loaded.Count == 0) send.Disable("RW_ReturnPodEmpty".Translate());
            yield return send;
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            var c = Contract;
            if (c != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("RW_ReturnPodInfo".Translate(contractId, LoadedCrates().Count, ReturnAdapter.Remaining(c)));
                if (c.ReturnDeadlineTick.HasValue)
                {
                    sb.AppendLine();
                    sb.Append("RW_ReturnDeadline".Translate(((int)System.Math.Max(0, c.ReturnDeadlineTick.Value - Ids.Now)).ToStringTicksToPeriod()));
                }
            }
            return sb.ToString();
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (mode != DestroyMode.Vanish) ReturnAdapter.Notify_PodLost(contractId, podId);
            base.Destroy(mode);
        }
    }

    public static class ReturnAdapter
    {
        public static ThingDef PodDef => DefDatabase<ThingDef>.GetNamed("RW_ReturnPod");

        public static bool IsReturning(Thing t)
        {
            var record = CargoAdapter.Comp(t)?.Record;
            if (record?.ContractId == null) return false;
            var contract = WarehouseWorldComponent.Instance?.State.Contract(record.ContractId);
            return contract != null && contract.Phase == ContractPhase.Returning;
        }

        public static int Remaining(ContractState c) =>
            c == null ? 0 : c.CargoStates.Count(x => !x.Returned && x.Loss == LossCause.None);

        public static Building_ReturnPod FindPod(Map map, string podId)
        {
            if (map == null || podId == null) return null;
            foreach (var t in map.listerThings.ThingsOfDef(PodDef))
            {
                if (t is Building_ReturnPod pod && pod.podId == podId) return pod;
            }
            return null;
        }

        /// <summary>CreateReturnPod effect: places the pod near the reception (reconciles by pod ID first).</summary>
        public static bool CreatePod(WarehouseWorldComponent world, EffectRequest effect)
        {
            var contract = world.State.Contract(effect.EntityId);
            if (contract == null || contract.Phase != ContractPhase.Returning || contract.ActiveReturnPodId != effect.EffectId) return true;
            var map = Ids.Map(contract.MapId);
            if (map == null) return false;
            if (FindPod(map, effect.EffectId) != null) return true;
            var pod = (Building_ReturnPod)ThingMaker.MakeThing(PodDef);
            pod.contractId = contract.Terms.ContractId;
            pod.podId = effect.EffectId;
            pod.SetFaction(Faction.OfPlayer);
            var spot = DeliveryAdapter.DeliverySpot(map);
            if (!GenPlace.TryPlaceThing(pod, spot, map, ThingPlaceMode.Near)) return false;
            FleckMaker.ThrowDustPuff(pod.Position, map, 2f);
            Messages.Message("RW_Msg_ReturnPodArrived".Translate(contract.Terms.ContractId), pod, MessageTypeDefOf.PositiveEvent, historical: false);
            return true;
        }

        public static void Send(Building_ReturnPod pod)
        {
            var world = WarehouseWorldComponent.Instance;
            var contract = pod.Contract;
            if (world == null || contract == null) return;
            var crates = pod.LoadedCrates();
            var ids = crates.Select(c => CargoAdapter.Comp(c).cargoId).ToList();
            var d = ReturnService.Dispatch(contract, ids, Ids.Now);
            if (!d.Success)
            {
                Messages.Message(d.ReasonKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            foreach (var crate in crates)
            {
                var record = CargoAdapter.Comp(crate).Record;
                if (record != null) record.Custody = CargoCustody.Returned;
                CargoAdapter.RemoveCrate(crate);
            }
            FleckMaker.ThrowSmoke(pod.DrawPos, pod.Map, 2f);
            pod.Destroy(DestroyMode.Vanish);
            world.Enqueue(d.Effects);
            PaymentService.SyncDebts(world.State, Ids.Now);
            Messages.Message("RW_Msg_Returned".Translate(contract.Terms.ContractId, ids.Count), MessageTypeDefOf.PositiveEvent, historical: true);
        }

        public static void Notify_PodLost(string contractId, string podId)
        {
            var contract = WarehouseWorldComponent.Instance?.State.Contract(contractId);
            if (contract == null) return;
            ReturnService.NotifyPodLost(contract, podId);
            Messages.Message("RW_Msg_ReturnPodLost".Translate(contractId), MessageTypeDefOf.NegativeEvent, historical: true);
        }

        public static void RequestPod(ContractState contract)
        {
            var world = WarehouseWorldComponent.Instance;
            var d = ReturnService.RequestPod(contract, Ids.Now);
            if (!d.Success) Messages.Message(d.ReasonKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
            else world.Enqueue(d.Effects);
        }
    }
}
